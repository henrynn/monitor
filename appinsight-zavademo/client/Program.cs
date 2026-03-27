using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

var serviceName = builder.Configuration["OTEL_SERVICE_NAME"] ?? "zava-demo-client";
var serviceVersion = "1.1.0";
var serverBaseUrl = builder.Configuration["ServerBaseUrl"] ?? "http://localhost:8080";

builder.Services
    .AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(serviceName: serviceName, serviceVersion: serviceVersion))
    .WithTracing(tracing => tracing.AddSource(DemoTelemetry.ActivitySourceName))
    .WithMetrics(metrics => metrics.AddMeter(DemoTelemetry.MeterName))
    .UseAzureMonitor(options =>
    {
        var connectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            options.ConnectionString = connectionString;
        }
    });

builder.Services.AddHttpClient<ServerDemoClient>(client =>
{
    client.BaseAddress = new Uri(serverBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.Add("User-Agent", "zava-demo-client");
});

builder.Services.AddHttpClient<ExternalProbeClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.Add("User-Agent", "zava-demo-client");
});

builder.Services.AddScoped<ClientWorkflowService>();

var app = builder.Build();

app.UseStaticFiles();

app.MapGet("/api/config", () => Results.Ok(new
{
    serviceName,
    serviceVersion,
    serverBaseUrl
}));

app.MapPost("/api/demo", async (ServerDemoClient demoClient, ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    using var activity = DemoTelemetry.ActivitySource.StartActivity("client.run-demo", ActivityKind.Internal);
    activity?.SetTag("demo.scenario", "happy-path");
    activity?.AddEvent(new ActivityEvent("client.demo.start"));
    DemoTelemetry.RequestCounter.Add(1, new KeyValuePair<string, object?>("scenario", "happy-path"));

    try
    {
        var result = await demoClient.RunScenarioAsync(includeFailure: false, cancellationToken);
        DemoTelemetry.ServerLatency.Record(result.ClientDurationMs, new KeyValuePair<string, object?>("scenario", "happy-path"));
        activity?.SetTag("demo.success", true);

        logger.LogInformation("Happy path completed in {DurationMs} ms", result.ClientDurationMs);

        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        DemoTelemetry.RecordException(activity, ex);
        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        logger.LogError(ex, "Happy path request failed");
        throw;
    }
});

app.MapPost("/api/demo/rich", async (ClientWorkflowService workflowService, ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    using var activity = DemoTelemetry.ActivitySource.StartActivity("client.run-rich-demo", ActivityKind.Internal);
    activity?.SetTag("demo.scenario", "rich-client-path");
    activity?.AddEvent(new ActivityEvent("client.rich-demo.start"));
    DemoTelemetry.RequestCounter.Add(1, new KeyValuePair<string, object?>("scenario", "rich-client-path"));
    DemoTelemetry.WorkflowCounter.Add(1, new KeyValuePair<string, object?>("scenario", "rich-client-path"));

    try
    {
        var result = await workflowService.RunRichWorkflowAsync(includeExpectedFailure: false, cancellationToken);
        logger.LogInformation("Rich client workflow completed with {StepCount} steps", result.Steps.Length);
        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        DemoTelemetry.RecordException(activity, ex);
        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        logger.LogError(ex, "Rich client workflow failed");
        throw;
    }
});

app.MapPost("/api/demo/rich/failure", async (ClientWorkflowService workflowService, ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    using var activity = DemoTelemetry.ActivitySource.StartActivity("client.run-rich-demo-failure", ActivityKind.Internal);
    activity?.SetTag("demo.scenario", "rich-client-with-failure");
    activity?.AddEvent(new ActivityEvent("client.rich-demo.failure.start"));
    DemoTelemetry.RequestCounter.Add(1, new KeyValuePair<string, object?>("scenario", "rich-client-with-failure"));
    DemoTelemetry.WorkflowCounter.Add(1, new KeyValuePair<string, object?>("scenario", "rich-client-with-failure"));

    var result = await workflowService.RunRichWorkflowAsync(includeExpectedFailure: true, cancellationToken);
    logger.LogInformation(
        "Rich failure workflow completed. Successful steps: {SuccessCount}, failed steps: {FailureCount}",
        result.SuccessCount,
        result.FailureCount);

    return Results.Ok(result);
});

app.MapPost("/api/demo/failure", async (ServerDemoClient demoClient, ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    using var activity = DemoTelemetry.ActivitySource.StartActivity("client.run-demo-failure", ActivityKind.Internal);
    activity?.SetTag("demo.scenario", "failure-path");
    DemoTelemetry.RequestCounter.Add(1, new KeyValuePair<string, object?>("scenario", "failure-path"));

    try
    {
        var result = await demoClient.RunScenarioAsync(includeFailure: true, cancellationToken);
        DemoTelemetry.ServerLatency.Record(result.ClientDurationMs, new KeyValuePair<string, object?>("scenario", "failure-path"));

        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        DemoTelemetry.RecordException(activity, ex);
        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        logger.LogError(ex, "Failure path produced the expected error");

        return Results.Problem(
            title: "Server failure path triggered",
            detail: ex.Message,
            statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapGet("/api/boom", () =>
{
    throw new InvalidOperationException("Simulated client-side exception endpoint.");
});

app.MapFallbackToFile("index.html");

app.Run();

sealed class ServerDemoClient(HttpClient httpClient, ILogger<ServerDemoClient> logger)
{
    public async Task<HealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken)
    {
        using var activity = DemoTelemetry.ActivitySource.StartActivity("client.check-server-health", ActivityKind.Internal);
        activity?.SetTag("server.base_url", httpClient.BaseAddress?.ToString());

        var stopwatch = Stopwatch.StartNew();
        using var response = await httpClient.GetAsync("/healthz", cancellationToken);
        var payload = await response.Content.ReadFromJsonAsync<HealthCheckResult>(cancellationToken: cancellationToken);
        stopwatch.Stop();

        DemoTelemetry.DependencyCounter.Add(1, new KeyValuePair<string, object?>("target", "server-health"));
        DemoTelemetry.DependencyLatency.Record(stopwatch.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("target", "server-health"));

        if (!response.IsSuccessStatusCode || payload is null)
        {
            var exception = new HttpRequestException($"Health probe failed with {(int)response.StatusCode}.", null, response.StatusCode);
            DemoTelemetry.RecordException(activity, exception);
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            throw exception;
        }

        return payload with { DurationMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2) };
    }

    public async Task<DemoRunResult> RunScenarioAsync(bool includeFailure, CancellationToken cancellationToken)
    {
        using var activity = DemoTelemetry.ActivitySource.StartActivity("client.invoke-server", ActivityKind.Internal);
        activity?.SetTag("server.base_url", httpClient.BaseAddress?.ToString());
        activity?.SetTag("demo.failure_mode", includeFailure);

        var route = includeFailure ? "/api/demo?simulateFailure=true" : "/api/demo";
        var stopwatch = Stopwatch.StartNew();
        using var response = await httpClient.GetAsync(route, cancellationToken);
        var payload = await response.Content.ReadFromJsonAsync<ServerResponse>(cancellationToken: cancellationToken);
        stopwatch.Stop();

        var target = includeFailure ? "server-demo-failure" : "server-demo";
        DemoTelemetry.DependencyCounter.Add(1, new KeyValuePair<string, object?>("target", target));
        DemoTelemetry.DependencyLatency.Record(stopwatch.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("target", target));

        if (!response.IsSuccessStatusCode)
        {
            var message = payload?.Message ?? $"Server returned {(int)response.StatusCode}.";
            var exception = new HttpRequestException(message, null, response.StatusCode);
            DemoTelemetry.RecordException(activity, exception);
            activity?.SetStatus(ActivityStatusCode.Error, message);
            throw exception;
        }

        logger.LogInformation("Server call completed with {DependencyCount} dependencies", payload?.DependencyCount ?? 0);

        return new DemoRunResult(
            Status: "ok",
            Message: payload?.Message ?? "Server call completed.",
            DependencyCount: payload?.DependencyCount ?? 0,
            TriggeredDependencies: payload?.TriggeredDependencies ?? [],
            ClientDurationMs: Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2));
    }
}

sealed class ExternalProbeClient(HttpClient httpClient)
{
    public async Task<WorkflowStepResult> CallJsonPlaceholderAsync(CancellationToken cancellationToken)
    {
        using var activity = DemoTelemetry.ActivitySource.StartActivity("client.call-jsonplaceholder", ActivityKind.Internal);
        return await ExecuteStepAsync(
            stepName: "client.jsonplaceholder.todo",
            dependencyTarget: "jsonplaceholder",
            operation: async () =>
            {
                using var response = await httpClient.GetAsync("https://jsonplaceholder.typicode.com/todos/2", cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                response.EnsureSuccessStatusCode();
                return body.Length > 180 ? body[..180] : body;
            },
            activity);
    }

    public async Task<WorkflowStepResult> CallHttpBinUuidAsync(CancellationToken cancellationToken)
    {
        using var activity = DemoTelemetry.ActivitySource.StartActivity("client.call-httpbin-uuid", ActivityKind.Internal);
        return await ExecuteStepAsync(
            stepName: "client.httpbin.uuid",
            dependencyTarget: "httpbin-uuid",
            operation: async () =>
            {
                using var response = await httpClient.GetAsync("https://httpbin.org/uuid", cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                response.EnsureSuccessStatusCode();
                return body.Trim();
            },
            activity);
    }

    public async Task<WorkflowStepResult> CallExpectedFailureAsync(CancellationToken cancellationToken)
    {
        using var activity = DemoTelemetry.ActivitySource.StartActivity("client.call-httpbin-failure", ActivityKind.Internal);
        return await ExecuteStepAsync(
            stepName: "client.httpbin.expected-failure",
            dependencyTarget: "httpbin-failure",
            operation: async () =>
            {
                using var response = await httpClient.GetAsync("https://httpbin.org/status/503", cancellationToken);
                _ = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException(
                    $"Expected downstream failure returned {(int)response.StatusCode}.",
                    null,
                    response.StatusCode);
            },
            activity,
            treatExceptionAsExpected: true);
    }

    private static async Task<WorkflowStepResult> ExecuteStepAsync(
        string stepName,
        string dependencyTarget,
        Func<Task<string>> operation,
        Activity? activity,
        bool treatExceptionAsExpected = false)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var detail = await operation();
            stopwatch.Stop();
            DemoTelemetry.DependencyCounter.Add(1, new KeyValuePair<string, object?>("target", dependencyTarget));
            DemoTelemetry.DependencyLatency.Record(stopwatch.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("target", dependencyTarget));
            activity?.SetTag("workflow.step", stepName);
            activity?.SetTag("workflow.status", "success");

            return new WorkflowStepResult(stepName, true, Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2), detail);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            DemoTelemetry.DependencyCounter.Add(1, new KeyValuePair<string, object?>("target", dependencyTarget));
            DemoTelemetry.DependencyLatency.Record(stopwatch.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("target", dependencyTarget));
            DemoTelemetry.RecordException(activity, ex);
            activity?.SetTag("workflow.step", stepName);
            activity?.SetTag("workflow.status", treatExceptionAsExpected ? "expected-failure" : "failure");

            if (!treatExceptionAsExpected)
            {
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                throw;
            }

            return new WorkflowStepResult(stepName, false, Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2), ex.Message);
        }
    }
}

sealed class ClientWorkflowService(
    ServerDemoClient serverDemoClient,
    ExternalProbeClient externalProbeClient,
    ILogger<ClientWorkflowService> logger)
{
    public async Task<RichWorkflowResult> RunRichWorkflowAsync(bool includeExpectedFailure, CancellationToken cancellationToken)
    {
        using var activity = DemoTelemetry.ActivitySource.StartActivity("client.workflow.orchestrate", ActivityKind.Internal);
        activity?.SetTag("workflow.include_expected_failure", includeExpectedFailure);

        var stopwatch = Stopwatch.StartNew();
        var steps = new List<WorkflowStepResult>();

        var health = await serverDemoClient.CheckHealthAsync(cancellationToken);
        steps.Add(new WorkflowStepResult("client.server.health", true, health.DurationMs, $"server:{health.Service}, ok:{health.Ok}"));

        steps.Add(await externalProbeClient.CallHttpBinUuidAsync(cancellationToken));
        steps.Add(await externalProbeClient.CallJsonPlaceholderAsync(cancellationToken));

        var happyPath = await serverDemoClient.RunScenarioAsync(includeFailure: false, cancellationToken);
        steps.Add(new WorkflowStepResult(
            "client.server.demo",
            true,
            happyPath.ClientDurationMs,
            $"dependencies:{happyPath.DependencyCount}; server:{string.Join(",", happyPath.TriggeredDependencies)}"));

        if (includeExpectedFailure)
        {
            var failedServerStep = await CaptureExpectedServerFailureAsync(cancellationToken);
            steps.Add(failedServerStep);
            steps.Add(await externalProbeClient.CallExpectedFailureAsync(cancellationToken));
        }

        stopwatch.Stop();
        DemoTelemetry.WorkflowLatency.Record(stopwatch.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("workflow", includeExpectedFailure ? "rich-with-failure" : "rich"));
        activity?.SetTag("workflow.step_count", steps.Count);

        return new RichWorkflowResult(
            WorkflowName: includeExpectedFailure ? "rich-client-with-failure" : "rich-client-path",
            TotalDurationMs: Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2),
            SuccessCount: steps.Count(step => step.Succeeded),
            FailureCount: steps.Count(step => !step.Succeeded),
            Steps: steps.ToArray());
    }

    private async Task<WorkflowStepResult> CaptureExpectedServerFailureAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await serverDemoClient.RunScenarioAsync(includeFailure: true, cancellationToken);
            stopwatch.Stop();
            return new WorkflowStepResult("client.server.expected-failure", false, Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2), "Expected failure was not triggered.");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            logger.LogInformation(ex, "Captured expected server-side failure during rich workflow");
            return new WorkflowStepResult("client.server.expected-failure", false, Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2), ex.Message);
        }
    }
}

record HealthCheckResult(bool Ok, string Service, double DurationMs = 0);

record ServerResponse(string Message, int DependencyCount, string[] TriggeredDependencies);

record DemoRunResult(
    string Status,
    string Message,
    int DependencyCount,
    string[] TriggeredDependencies,
    double ClientDurationMs);

record WorkflowStepResult(string StepName, bool Succeeded, double DurationMs, string Detail);

record RichWorkflowResult(
    string WorkflowName,
    double TotalDurationMs,
    int SuccessCount,
    int FailureCount,
    WorkflowStepResult[] Steps);

static class DemoTelemetry
{
    public const string ActivitySourceName = "ZavaDemo.Client";
    public const string MeterName = "ZavaDemo.Client";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    public static readonly Meter Meter = new(MeterName);
    public static readonly Counter<long> RequestCounter = Meter.CreateCounter<long>("client.demo.requests");
    public static readonly Counter<long> WorkflowCounter = Meter.CreateCounter<long>("client.demo.workflows");
    public static readonly Counter<long> DependencyCounter = Meter.CreateCounter<long>("client.demo.dependencies");
    public static readonly Histogram<double> ServerLatency = Meter.CreateHistogram<double>("client.demo.server_latency", unit: "ms");
    public static readonly Histogram<double> DependencyLatency = Meter.CreateHistogram<double>("client.demo.dependency_latency", unit: "ms");
    public static readonly Histogram<double> WorkflowLatency = Meter.CreateHistogram<double>("client.demo.workflow_latency", unit: "ms");

    public static void RecordException(Activity? activity, Exception exception)
    {
        activity?.AddEvent(new ActivityEvent(
            "exception",
            tags: new ActivityTagsCollection
            {
                ["exception.type"] = exception.GetType().FullName,
                ["exception.message"] = exception.Message,
                ["exception.stacktrace"] = exception.StackTrace
            }));
    }
}
