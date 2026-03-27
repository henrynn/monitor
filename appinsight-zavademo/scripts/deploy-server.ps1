param(
    [Parameter(Mandatory = $true)]
    [string]$ResourceGroupName,

    [string]$Location = "eastus",

    [string]$Prefix = "zavademo",

    [string]$ImageTag = "v1"
)

$ErrorActionPreference = "Stop"
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptRoot
$platformTemplatePath = Join-Path $repoRoot "infra/platform.bicep"
$serverTemplatePath = Join-Path $repoRoot "infra/server.bicep"
$serverContextPath = Join-Path $repoRoot "server"

function Require-Command {
    param([string]$Name)

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command '$Name' was not found."
    }
}

function Assert-Success {
    param([string]$StepName)

    if ($LASTEXITCODE -ne 0) {
        throw "$StepName failed with exit code $LASTEXITCODE."
    }
}

function Wait-ForContainerAppReady {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ResourceGroupName,

        [Parameter(Mandatory = $true)]
        [string]$ContainerAppName,

        [int]$TimeoutSeconds = 300,

        [int]$PollIntervalSeconds = 15
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)

    while ((Get-Date) -lt $deadline) {
        $appJson = az containerapp show --name $ContainerAppName --resource-group $ResourceGroupName --output json 2>$null
        if ($LASTEXITCODE -eq 0 -and $appJson) {
            $app = $appJson | ConvertFrom-Json
            $revisionJson = az containerapp revision list --name $ContainerAppName --resource-group $ResourceGroupName --output json 2>$null

            if ($LASTEXITCODE -eq 0 -and $revisionJson) {
                $revisions = $revisionJson | ConvertFrom-Json
                $healthyRevision = $revisions | Where-Object {
                    $_.properties.runningState -eq "Running" -and
                    $_.properties.healthState -eq "Healthy" -and
                    $_.properties.provisioningState -eq "Provisioned"
                } | Select-Object -First 1

                if ($healthyRevision -and $app.properties.configuration.ingress.fqdn) {
                    return [pscustomobject]@{
                        ServerUrl = "https://$($app.properties.configuration.ingress.fqdn)"
                        LatestRevision = $healthyRevision.name
                    }
                }
            }
        }

        Start-Sleep -Seconds $PollIntervalSeconds
    }

    return $null
}

foreach ($path in @($platformTemplatePath, $serverTemplatePath, $serverContextPath)) {
    if (-not (Test-Path $path)) {
        throw "Required path was not found: $path"
    }
}

Require-Command -Name "az"

Write-Host "Creating or updating resource group $ResourceGroupName in $Location..."
az group create --name $ResourceGroupName --location $Location | Out-Null
Assert-Success -StepName "Resource group creation"

Write-Host "Deploying shared platform resources..."
$platformOutputs = az deployment group create `
    --resource-group $ResourceGroupName `
    --template-file $platformTemplatePath `
    --parameters prefix=$Prefix location=$Location `
    --query properties.outputs `
    --output json | ConvertFrom-Json
Assert-Success -StepName "Platform deployment"

if (-not $platformOutputs) {
    throw "Platform deployment returned no outputs."
}

$acrName = $platformOutputs.acrName.value
$acrLoginServer = $platformOutputs.acrLoginServer.value
$connectionString = $platformOutputs.appInsightsConnectionString.value
$containerAppEnvironmentName = $platformOutputs.containerAppEnvironmentName.value
$keyVaultName = $platformOutputs.keyVaultName.value
$keyVaultUri = $platformOutputs.keyVaultUri.value
$keyVaultSecretName = $platformOutputs.keyVaultSecretName.value
$storageAccountName = $platformOutputs.storageAccountName.value
$storageAccountUrl = $platformOutputs.storageAccountUrl.value
$containerAppName = "ca-$Prefix-server"
$serverImage = "$acrLoginServer/zava-demo-server:$ImageTag"

$acrCredentials = az acr credential show --name $acrName --output json | ConvertFrom-Json
Assert-Success -StepName "ACR credential retrieval"

if (-not $acrCredentials) {
    throw "ACR credential retrieval returned no data."
}

$registryUsername = $acrCredentials.username
$registryPassword = $acrCredentials.passwords[0].value

foreach ($value in @{
    acrName = $acrName
    acrLoginServer = $acrLoginServer
    connectionString = $connectionString
    containerAppEnvironmentName = $containerAppEnvironmentName
    keyVaultUri = $keyVaultUri
    keyVaultName = $keyVaultName
    keyVaultSecretName = $keyVaultSecretName
    registryUsername = $registryUsername
    registryPassword = $registryPassword
    storageAccountName = $storageAccountName
    storageAccountUrl = $storageAccountUrl
}.GetEnumerator()) {
    if ([string]::IsNullOrWhiteSpace($value.Value)) {
        throw "Platform deployment output '$($value.Key)' is empty."
    }
}

Write-Host "Building and pushing server image $serverImage..."
az acr build --registry $acrName --image "zava-demo-server:$ImageTag" $serverContextPath | Out-Null
Assert-Success -StepName "ACR build"

Write-Host "Deploying Azure Container App..."
$serverOutputs = az deployment group create `
    --resource-group $ResourceGroupName `
    --template-file $serverTemplatePath `
    --parameters `
        prefix=$Prefix `
        location=$Location `
        serverImage=$serverImage `
        appInsightsConnectionString=$connectionString `
        containerAppEnvironmentName=$containerAppEnvironmentName `
        acrName=$acrName `
        acrLoginServer=$acrLoginServer `
        registryUsername=$registryUsername `
        registryPassword=$registryPassword `
        keyVaultName=$keyVaultName `
        keyVaultSecretName=$keyVaultSecretName `
        keyVaultUri=$keyVaultUri `
        storageAccountName=$storageAccountName `
        storageAccountUrl=$storageAccountUrl `
    --query properties.outputs `
    --output json | ConvertFrom-Json

$serverDeploymentExitCode = $LASTEXITCODE

if ($serverDeploymentExitCode -ne 0) {
    Write-Warning "Server deployment returned a non-zero exit code. Checking whether the Container App became healthy after a control-plane timeout..."
    $containerAppState = Wait-ForContainerAppReady -ResourceGroupName $ResourceGroupName -ContainerAppName $containerAppName

    if (-not $containerAppState) {
        throw "Server deployment failed with exit code $serverDeploymentExitCode."
    }

    $serverUrl = $containerAppState.ServerUrl
    Write-Host "Container App became healthy after the deployment timeout. Revision: $($containerAppState.LatestRevision)"
}
else {
    if (-not $serverOutputs) {
        throw "Server deployment returned no outputs."
    }

    $serverUrl = $serverOutputs.serverUrl.value
}

Write-Host ""
Write-Host "Deployment finished."
Write-Host "Application Insights Connection String:" $connectionString
Write-Host "Server URL:" $serverUrl
Write-Host ""
Write-Host "Run the client with:"
Write-Host (Join-Path $scriptRoot "run-client.ps1") "-AppInsightsConnectionString '$connectionString' -ServerBaseUrl '$serverUrl'"
