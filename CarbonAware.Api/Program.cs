using CarbonAware.Core;
using CarbonAware.Providers;
using CarbonAware.Providers.Options;
using CarbonAware.RegionMap;
using CarbonAware.Targets;
using System.Net.Http.Headers;
using CarbonAware.Targets.Options;
using Microsoft.EntityFrameworkCore;
using CarbonAware.Api.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using CarbonAware.Core.Auditing;



string gitHubApiURL = "https://api.github.com";
string wattTimeApiURL = "https://api.watttime.org";
var builder = WebApplication.CreateBuilder(args);

// Bind the Audit section
builder.Services.Configure<AuditOptions>(builder.Configuration.GetSection("Audit"));

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Bind options
builder.Services.Configure<WattTimeOptions>(builder.Configuration.GetSection("WattTime"));
// Bind GitHub options
builder.Services.Configure<GitHubActionsOptions>(builder.Configuration.GetSection("GitHub"));
// NEW: Bind cost and latency signal options for the multi-objective scheduler
builder.Services.Configure<CostSignalOptions>(builder.Configuration.GetSection("CostSignal"));
builder.Services.Configure<LatencySignalOptions>(builder.Configuration.GetSection("LatencySignal"));

// Add targets 
builder.Services.AddHttpClient<AzureGithubActionsTarget>(http => http.BaseAddress = new Uri(gitHubApiURL));
builder.Services.AddHttpClient<GcpGithubActionsTarget>(http => http.BaseAddress = new Uri(gitHubApiURL));
builder.Services.AddHttpClient<AwsGithubActionsTarget>(http => http.BaseAddress = new Uri(gitHubApiURL));

builder.Services.AddDbContext<LoggingDbContext>(opt =>
    opt.UseSqlServer(builder.Configuration.GetConnectionString("LoggingDb"),
        sql => sql.EnableRetryOnFailure(5, TimeSpan.FromSeconds(2), null)));

builder.Services.AddScoped<IAuditSink, EfAuditSink>();

// Register WattTimeProvider as a typed HttpClient (this sets BaseAddress)
builder.Services.AddHttpClient<WattTimeProvider>((sp, http) =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var baseUrl = cfg["WattTime:BaseUrl"] ?? wattTimeApiURL;
    http.BaseAddress = new Uri(baseUrl);
});

// Use the typed client as the active carbon signal provider
builder.Services.AddScoped<ICarbonSignalProvider>(sp => sp.GetRequiredService<WattTimeProvider>());
builder.Services.AddScoped<IBestWindowSignalProvider>(sp => sp.GetRequiredService<WattTimeProvider>());
builder.Services.AddSingleton<ICorrelationContext, CorrelationContext>();

// NEW: cost and latency signal providers for the multi-objective scheduler
builder.Services.AddHttpClient<CostSignalProvider>();
builder.Services.AddHttpClient<LatencySignalProvider>();
builder.Services.AddScoped<ICostSignalProvider>(sp => sp.GetRequiredService<CostSignalProvider>());
builder.Services.AddScoped<ILatencySignalProvider>(sp => sp.GetRequiredService<LatencySignalProvider>());

// Background service can safely depend on WattTimeProvider (typed client)
builder.Services.AddHostedService<WattTimeAuthBackgroundService>();

// Region map / engine / target
builder.Services.AddSingleton<IRegionMapper, StaticRegionMapper>();
builder.Services.AddSingleton<IRegionAllowlist, ConfigRegionAllowlist>();
builder.Services.AddScoped<IPolicyEngine, WattTimeTwoModeEngine>();
// NEW: multi-objective scheduling engine (runs alongside IPolicyEngine, doesn't replace it)
builder.Services.AddScoped<IMultiObjectivePolicyEngine, MultiObjectiveScoringEngine>();
builder.Services.AddScoped<ICloudTarget>(sp =>
    new TargetRouter(
        sp.GetRequiredService<ILogger<TargetRouter>>(),
        new Dictionary<string, ICloudTarget>(StringComparer.OrdinalIgnoreCase)
        {
            { "gcp",   sp.GetRequiredService<GcpGithubActionsTarget>() },
            { "azure", sp.GetRequiredService<AzureGithubActionsTarget>() },
            { "aws", sp.GetRequiredService<AwsGithubActionsTarget>() }
        }
    )
);

var app = builder.Build();
// Path for favorites file (cloud+region pairs)
var favoritesPath = Path.Combine(app.Environment.ContentRootPath, "favorites.json");

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapPost("/advise", async (OrchestrationRequest req, IPolicyEngine engine, CancellationToken ct) =>
{
    CorrelationContext correlation = new CorrelationContext();
    correlation.Current = Guid.NewGuid();
    try
    {
        var advice = await engine.AdviseAsync(req.Job, req.Policy, correlation, ct);
        return Results.Ok(advice);
    }
    finally { correlation.Current = null; }
})
.WithName("Advise");

app.MapPost("/schedule", async (OrchestrationRequest req, IPolicyEngine engine, ICloudTarget target, LoggingDbContext db, CancellationToken ct) =>
{
    CorrelationContext correlation = new CorrelationContext();
    correlation.Current = Guid.NewGuid();
    try
    {
        var advice = await engine.AdviseAsync(req.Job, req.Policy, correlation, ct);

        var cycleId = string.IsNullOrWhiteSpace(req.CycleId)
            ? $"auto-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}"
            : req.CycleId!;
        var experiment = new ExperimentContext(cycleId, "single", null);

        await UpsertPredictedAsync(db, new PredictedResultRequest(
            cycleId, "single", null, advice.Cloud, advice.Region,
            advice.EstimatedIntensityGPerKwh, null, null), ct);

        var id = await target.ScheduleAsync(advice, req.Job, correlation, experiment, ct);
        return Results.Ok(new { advice, scheduledId = id, cycleId });
    }
    finally { correlation.Current = null; }
})
.WithName("Schedule");

// ---------------------------------------------------------------------
// NEW: multi-objective endpoints (carbon + cost + latency)
// ---------------------------------------------------------------------

app.MapPost("/advise-multi", async (MultiObjectiveRequest req, IMultiObjectivePolicyEngine engine, CancellationToken ct) =>
{
    var correlation = new CorrelationContext { Current = Guid.NewGuid() };
    try
    {
        var weights = req.ResolveWeights();
        var advice = await engine.AdviseMultiAsync(req.Job, req.Policy, weights, correlation, ct);
        return Results.Ok(advice);
    }
    finally { correlation.Current = null; }
})
.WithName("AdviseMulti");

app.MapPost("/schedule-multi", async (MultiObjectiveRequest req, IMultiObjectivePolicyEngine engine, ICloudTarget target, LoggingDbContext db, CancellationToken ct) =>
{
    var correlation = new CorrelationContext { Current = Guid.NewGuid() };
    try
    {
        var weights = req.ResolveWeights();
        var advice = await engine.AdviseMultiAsync(req.Job, req.Policy, weights, correlation, ct);
        if (advice.Cloud is null || advice.Region is null)
            return Results.UnprocessableEntity(new { error = "No candidate had complete carbon+cost+latency signals.", advice });

        var cycleId = string.IsNullOrWhiteSpace(req.CycleId)
            ? $"auto-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}"
            : req.CycleId!;
        var experiment = new ExperimentContext(cycleId, "multi", advice.WeightProfile);

        var bestCandidate = advice.Candidates.FirstOrDefault(c =>
            string.Equals(c.Cloud, advice.Cloud, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(c.Region, advice.Region, StringComparison.OrdinalIgnoreCase));

        await UpsertPredictedAsync(db, new PredictedResultRequest(
            cycleId, "multi", advice.WeightProfile, advice.Cloud!, advice.Region!,
            bestCandidate?.MoerGPerKwh, bestCandidate?.CostUsdPerHr, bestCandidate?.LatencyMs), ct);

        var singleShapedAdvice = new AdviceResult(advice.Cloud!, advice.Region!, advice.When, advice.Rationale, null);
        var id = await target.ScheduleAsync(singleShapedAdvice, req.Job, correlation, experiment, ct);
        return Results.Ok(new { advice, scheduledId = id, cycleId });
    }
    finally { correlation.Current = null; }
})
.WithName("ScheduleMulti");

app.MapPost("/advise-multi/compare", async (OrchestrationRequest req, IMultiObjectivePolicyEngine engine, CancellationToken ct) =>
{
    var correlation = new CorrelationContext { Current = Guid.NewGuid() };
    try
    {
        var profiles = new (string Name, ObjectiveWeights Weights)[]
        {
            ("carbon-prioritised", ObjectiveWeights.CarbonPrioritised),
            ("balanced", ObjectiveWeights.Balanced),
            ("cost-prioritised", ObjectiveWeights.CostPrioritised)
        };

        var results = new Dictionary<string, MultiObjectiveAdviceResult>();
        foreach (var (name, weights) in profiles)
            results[name] = await engine.AdviseMultiAsync(req.Job, req.Policy, weights, correlation, ct);

        return Results.Ok(results);
    }
    finally { correlation.Current = null; }
})
.WithName("AdviseMultiCompare");

app.MapGet("/regions", (IRegionMapper mapper) =>
{
    var byCloud = mapper.ListAllRegionsByCloud();
    return Results.Ok(byCloud); // { "azure": [...], "gcp": [...], "aws": [...] }
});

// DEBUGGING PURPOSES ONLY
app.MapGet("/debug/watttime-index/{region}", async (
    string region,
    WattTimeProvider wt,
    IConfiguration cfg,
    CancellationToken ct) =>
{
    // Make sure we're logged in so the provider has a fresh token
    await wt.EnsureTokenAsync(ct);

    // Get token from the provider (via reflection since it's private)
    var tokenField = typeof(WattTimeProvider)
        .GetField("_bearerToken", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
    var token = tokenField?.GetValue(wt) as string ?? string.Empty;

    // Build the request to v3/signal-index
    var baseUrl = cfg["WattTime:BaseUrl"] ?? wattTimeApiURL;
    using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };

    var uri = $"/v3/signal-index?region={Uri.EscapeDataString(region)}&signal_type=co2_moer";
    using var req = new HttpRequestMessage(HttpMethod.Get, uri);
    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

    var resp = await http.SendAsync(req, ct);
    var body = await resp.Content.ReadAsStringAsync(ct);

    // Return raw JSON from WattTime for easy inspection
    return Results.Text(body, "application/json");
});

// Favorites API (use LocationSpec: { cloud, region }) ---
app.MapGet("/favorites", () =>
{
    if (!System.IO.File.Exists(favoritesPath))
        return Results.Ok(Array.Empty<LocationSpec>());

    try
    {
        var json = System.IO.File.ReadAllText(favoritesPath);
        var favs = JsonSerializer.Deserialize<List<CarbonAware.Core.LocationSpec>>(json)
                   ?? new List<CarbonAware.Core.LocationSpec>();
        return Results.Ok(favs);
    }
    catch
    {
        // If file is corrupt, return empty list instead of 500
        return Results.Ok(Array.Empty<CarbonAware.Core.LocationSpec>());
    }
});

app.MapPost("/favorites", async (List<CarbonAware.Core.LocationSpec> favs, CancellationToken ct) =>
{
    // Normalize: distinct cloud/region pairs
    var distinct = favs
        .Where(f => !string.IsNullOrWhiteSpace(f.Cloud) && !string.IsNullOrWhiteSpace(f.Region))
        .GroupBy(f => (f.Cloud.ToLowerInvariant(), f.Region.ToLowerInvariant()))
        .Select(g => g.First())
        .ToList();

    var json = JsonSerializer.Serialize(distinct, new JsonSerializerOptions
    {
        WriteIndented = true
    });

    await System.IO.File.WriteAllTextAsync(favoritesPath, json, ct);
    return Results.Ok(new { saved = distinct.Count });
});

app.MapDelete("/favorites", () =>
{
    if (System.IO.File.Exists(favoritesPath))
        System.IO.File.Delete(favoritesPath);

    return Results.Ok(new { cleared = true });
});


// ---------------------------------------------------------------------
// NEW: experiment results logging (2-3 week actuals collection).
// Both endpoints upsert CycleResultLog by (CycleId, CloudProvider, Region),
// so it doesn't matter which one arrives first. UpsertPredictedAsync is also
// called directly from /schedule and /schedule-multi so every dispatched cycle
// gets its predicted row written automatically -- callers only need
// POST /results/predicted for cycles scheduled via /advise[-multi] instead
// (i.e. you drove the workflow_dispatch yourself rather than using /schedule).
// ---------------------------------------------------------------------

async Task<CycleResultLog> UpsertPredictedAsync(LoggingDbContext db, PredictedResultRequest req, CancellationToken ct)
{
    var now = DateTimeOffset.UtcNow;
    var row = await db.CycleResults.FirstOrDefaultAsync(r =>
        r.CycleId == req.CycleId && r.CloudProvider == req.CloudProvider && r.Region == req.Region, ct);

    if (row is null)
    {
        row = new CycleResultLog
        {
            CycleId = req.CycleId,
            CloudProvider = req.CloudProvider,
            Region = req.Region,
            CreatedUtc = now
        };
        db.CycleResults.Add(row);
    }
    else
    {
        row.UpdatedUtc = now;
    }

    row.ObjectiveType = req.ObjectiveType ?? row.ObjectiveType;
    row.WeightConfig = req.WeightConfig ?? row.WeightConfig;
    row.PredictedMoerGPerKwh = req.MoerGPerKwh;
    row.PredictedCostUsdPerHr = req.CostUsdPerHr;
    row.PredictedLatencyMs = req.LatencyMs;
    row.PredictedAtUtc = now;

    await db.SaveChangesAsync(ct);
    return row;
}

app.MapPost("/results/predicted", async (
    PredictedResultRequest req,
    LoggingDbContext db,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.CycleId) || string.IsNullOrWhiteSpace(req.CloudProvider) || string.IsNullOrWhiteSpace(req.Region))
        return Results.BadRequest(new { error = "cycle_id, cloud_provider, and region are required." });

    var row = await UpsertPredictedAsync(db, req, ct);
    return Results.Ok(new { id = row.Id, upserted = "predicted" });
})
.WithName("ResultsPredicted");

app.MapPost("/results/actual", async (
    ActualResultRequest req,
    LoggingDbContext db,
    ICarbonSignalProvider carbon,
    ICostSignalProvider cost,
    IRegionMapper mapper,
    CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.CycleId) || string.IsNullOrWhiteSpace(req.CloudProvider) || string.IsNullOrWhiteSpace(req.Region))
        return Results.BadRequest(new { error = "cycle_id, cloud_provider, and region are required." });

    var now = DateTimeOffset.UtcNow;
    var row = await db.CycleResults.FirstOrDefaultAsync(r =>
        r.CycleId == req.CycleId && r.CloudProvider == req.CloudProvider && r.Region == req.Region, ct);

    if (row is null)
    {
        // Actual arrived with no matching predicted row -- still record it rather than
        // silently dropping deployment data, but this shouldn't normally happen.
        row = new CycleResultLog
        {
            CycleId = req.CycleId,
            CloudProvider = req.CloudProvider,
            Region = req.Region,
            CreatedUtc = now
        };
        db.CycleResults.Add(row);
    }

    row.UpdatedUtc = now;
    row.ObjectiveType ??= req.ObjectiveType;
    row.WeightConfig ??= req.WeightConfig;
    row.ActualTimestampStart = req.TimestampStart;
    row.ActualTimestampEnd = req.TimestampEnd;
    row.LatencyActualSec = req.LatencyActualSec;
    row.ExecutionTimeSec = req.ExecutionTimeSec;
    row.DeploymentSuccess = req.DeploymentSuccess;
    row.ErrorNotes = req.ErrorNotes;

    // batch_job.py runs on the deployed VM with no WattTime/pricing credentials, so it
    // can't measure actual MOER/cost itself. Best-effort fill-in: ask the API's own
    // providers for a reading of this cloud/region right now, at report time. This is
    // an approximation of "actual" -- see the comment on CycleResultLog -- not a true
    // historical reading for the exact execution window. Failures here must not block
    // saving the actuals that batch_job.py DID measure (latency/execution time/success).
    try
    {
        var zone = mapper.GetGridZones(req.CloudProvider, req.Region);
        var sig = await carbon.GetSignalsAsync(zone, now, marginal: true, ct);
        if (sig is not null && double.IsFinite(sig.IntensityGPerKwh) && sig.IntensityGPerKwh > 0)
        {
            row.ActualMoerGPerKwh = sig.IntensityGPerKwh * 0.45359237; // lbs/MWh -> g/kWh
            row.ActualMoerSource = "watttime-at-report-time";
        }
    }
    catch { /* best-effort; predicted values remain the primary record */ }

    try
    {
        var costSig = await cost.GetCostAsync(req.CloudProvider, req.Region, ct);
        if (costSig is not null)
        {
            row.ActualCostUsdPerHr = costSig.HourlyUsd;
            row.ActualCostSource = costSig.Source;
        }
    }
    catch { /* best-effort */ }

    await db.SaveChangesAsync(ct);
    return Results.Ok(new { id = row.Id, upserted = "actual" });
})
.WithName("ResultsActual");

app.UseDefaultFiles();   // serves index.html by default
app.UseStaticFiles();
app.Run();

// ---------------------------------------------------------------------
// DTOs for the results endpoints. snake_case on purpose: ActualResultRequest's shape
// matches batch_job.py's result_row exactly, so the GitHub Actions workflow can POST
// result.json to /results/actual with no transformation.
// ---------------------------------------------------------------------

public record PredictedResultRequest(
    [property: JsonPropertyName("cycle_id")] string CycleId,
    [property: JsonPropertyName("objective_type")] string? ObjectiveType,
    [property: JsonPropertyName("weight_config")] string? WeightConfig,
    [property: JsonPropertyName("cloud_provider")] string CloudProvider,
    [property: JsonPropertyName("region")] string Region,
    [property: JsonPropertyName("moer_g_per_kwh")] double? MoerGPerKwh,
    [property: JsonPropertyName("cost_usd_per_hr")] double? CostUsdPerHr,
    [property: JsonPropertyName("latency_ms")] double? LatencyMs
);

public record ActualResultRequest(
    [property: JsonPropertyName("cycle_id")] string CycleId,
    [property: JsonPropertyName("objective_type")] string? ObjectiveType,
    [property: JsonPropertyName("weight_config")] string? WeightConfig,
    [property: JsonPropertyName("cloud_provider")] string CloudProvider,
    [property: JsonPropertyName("region")] string Region,
    [property: JsonPropertyName("timestamp_start")] DateTimeOffset? TimestampStart,
    [property: JsonPropertyName("timestamp_end")] DateTimeOffset? TimestampEnd,
    [property: JsonPropertyName("latency_actual_sec")] double? LatencyActualSec,
    [property: JsonPropertyName("execution_time_sec")] double? ExecutionTimeSec,
    [property: JsonPropertyName("deployment_success")] bool? DeploymentSuccess,
    [property: JsonPropertyName("error_notes")] string? ErrorNotes
);