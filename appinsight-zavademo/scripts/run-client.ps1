param(
    [Parameter(Mandatory = $true)]
    [string]$AppInsightsConnectionString,

    [Parameter(Mandatory = $true)]
    [string]$ServerBaseUrl,

    [string]$ServiceName = "zava-demo-client"
)

$ErrorActionPreference = "Stop"
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptRoot
$clientProjectPath = Join-Path $repoRoot "client/ZavaDemo.Client.csproj"

if (-not (Test-Path $clientProjectPath)) {
    throw "Client project was not found: $clientProjectPath"
}

$env:APPLICATIONINSIGHTS_CONNECTION_STRING = $AppInsightsConnectionString
$env:OTEL_SERVICE_NAME = $ServiceName
$env:ServerBaseUrl = $ServerBaseUrl

dotnet run --project $clientProjectPath
