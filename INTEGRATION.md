# Wiring the multi-objective scheduler into CarbonAwareAPI

## 1. Copy files in
Drop these into your repo at the matching paths (create the `MultiObjective` subfolder under `CarbonAware.Core`):

```
CarbonAware.Core/MultiObjective/Models.cs
CarbonAware.Core/MultiObjective/CandidateResolver.cs
CarbonAware.Core/MultiObjective/MultiObjectiveScoringEngine.cs
CarbonAware.Providers/CostSignalProvider.cs
CarbonAware.Providers/LatencySignalProvider.cs
CarbonAware.Providers/Options/CostSignalOptions.cs
CarbonAware.Providers/Options/LatencySignalOptions.cs
```

No new NuGet packages are needed — everything uses what's already referenced (`System.Text.Json`, `Microsoft.Extensions.*`).

## 2. Program.cs additions

Add these lines near the existing option bindings (after the `WattTimeOptions`/`GitHubActionsOptions` block):

```csharp
builder.Services.Configure<CostSignalOptions>(builder.Configuration.GetSection("CostSignal"));
builder.Services.Configure<LatencySignalOptions>(builder.Configuration.GetSection("LatencySignal"));
```

Add these next to the existing `AddHttpClient<WattTimeProvider>` block:

```csharp
builder.Services.AddHttpClient<CostSignalProvider>();
builder.Services.AddHttpClient<LatencySignalProvider>();

builder.Services.AddScoped<ICostSignalProvider>(sp => sp.GetRequiredService<CostSignalProvider>());
builder.Services.AddScoped<ILatencySignalProvider>(sp => sp.GetRequiredService<LatencySignalProvider>());
builder.Services.AddScoped<IMultiObjectivePolicyEngine, MultiObjectiveScoringEngine>();
```

Add these two endpoints next to the existing `/advise` and `/schedule` minimal-API routes:

```csharp
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

app.MapPost("/schedule-multi", async (MultiObjectiveRequest req, IMultiObjectivePolicyEngine engine, ICloudTarget target, CancellationToken ct) =>
{
    var correlation = new CorrelationContext { Current = Guid.NewGuid() };
    try
    {
        var weights = req.ResolveWeights();
        var advice = await engine.AdviseMultiAsync(req.Job, req.Policy, weights, correlation, ct);
        if (advice.Cloud is null || advice.Region is null)
            return Results.UnprocessableEntity(new { error = "No candidate had complete carbon+cost+latency signals.", advice });

        // Reuse the existing single-objective AdviceResult shape for ICloudTarget by
        // constructing a minimal one from the multi-objective pick.
        var singleShapedAdvice = new AdviceResult(
            advice.Cloud!, advice.Region!, advice.When, advice.Rationale, null);

        var id = await target.ScheduleAsync(singleShapedAdvice, req.Job, correlation, ct);
        return Results.Ok(new { advice, scheduledId = id });
    }
    finally { correlation.Current = null; }
})
.WithName("ScheduleMulti");

// Optional but recommended for your dissertation's sensitivity analysis (Table 1):
// runs all three weight profiles back-to-back against the same candidate set/signals-in-time
// so you can log region-selection disagreement per cycle without three separate HTTP calls.
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
```

## 3. appsettings.json additions

```json
{
  "CostSignal": {
    "InstanceTypeByCloud": {
      "aws": "t3.medium",
      "azure": "Standard_B2s",
      "gcp": "e2-medium"
    },
    "CacheTtlHours": 24,
    "StaticFallbackUsdPerHr": {
      "aws:eu-west-1": 0.0416,
      "gcp:europe-west1": 0.034,
      "azure:northeurope": 0.0472
    }
  },
  "LatencySignal": {
    "TimeoutMs": 3000,
    "SamplesPerRegion": 3,
    "EndpointByRegion": {
      "aws:eu-west-1": "https://ec2.eu-west-1.amazonaws.com",
      "aws:us-east-1": "https://ec2.us-east-1.amazonaws.com",
      "aws:eu-west-2": "https://ec2.eu-west-2.amazonaws.com",
      "aws:us-west-2": "https://ec2.us-west-2.amazonaws.com"
    }
  }
}
```

The AWS `ec2.{region}.amazonaws.com` pattern is AWS's own documented service endpoint
convention, so those four are safe to use as-is — add more AWS regions the same way. For
Azure/GCP regions you plan to test, find a real per-region public endpoint from each
provider's docs and add it the same way; don't guess at hostnames, an unreachable one
just means that candidate gets excluded from the composite score (see `Excluded`/
`ExclusionReason` in the response) rather than corrupting the result.

The static fallback prices above are illustrative — replace with real current on-demand
rates for your chosen instance types before you start the 4-week run.

## 4. Test it

```bash
# Single weight profile
curl -X POST https://localhost:PORT/advise-multi \
  -H "Content-Type: application/json" \
  -d '{
    "policy": { "mode": "run_now", "preferredLocations": [
      {"cloud":"aws","region":"eu-west-1"},
      {"cloud":"aws","region":"us-east-1"},
      {"cloud":"aws","region":"eu-west-2"}
    ]},
    "job": { "clouds": ["aws"] },
    "weightProfile": "balanced"
  }'

# All three profiles at once (for your sensitivity analysis)
curl -X POST https://localhost:PORT/advise-multi/compare \
  -H "Content-Type: application/json" \
  -d '{
    "policy": { "mode": "run_now", "preferredLocations": [
      {"cloud":"aws","region":"eu-west-1"},
      {"cloud":"aws","region":"us-east-1"},
      {"cloud":"aws","region":"eu-west-2"}
    ]},
    "job": { "clouds": ["aws"] }
  }'
```

Or just hit Swagger UI (`/swagger`) once it's running — the three new endpoints will show
up alongside `/advise` and `/schedule`.

## 5. What this gives you for the dissertation write-up

- **Region-selection disagreement (§VI.A):** compare `advice.Cloud/Region` (multi-objective)
  vs `advice.SingleObjectiveCloud/SingleObjectiveRegion` — `RegionsDiffer` is computed for you
  on every response. Log this across your 84 cycles to get the ">40%" statistic.
- **Sensitivity analysis (Table 1):** `/advise-multi/compare` runs all three weight profiles
  against the same signals in a single call — no timing skew between profiles.
- **Trade-off quantification (§VI.B):** each `RegionCandidateScore` carries the raw
  `MoerGPerKwh`, `CostUsdPerHr`, and `LatencyMs` for every candidate, not just the winner —
  so you can compute "X% carbon reduction for Y% cost reduction" directly from one response.
- **Excluded candidates are visible, not silently dropped** (`Excluded`/`ExclusionReason`) —
  useful evidence for your methodology/limitations section if a region's cost or latency
  signal is unavailable during a cycle.

## 6. Suggested next steps, in order

1. Get `/advise-multi` returning sane results for your 3 AWS regions locally (mode `run_now`,
   `weightProfile: "balanced"`) before touching `/schedule-multi`.
2. Fill in real AWS on-demand prices for `t3.medium` in your candidate regions as the
   `StaticFallbackUsdPerHr` seed — this is your safety net if the live AWS Price List
   fetch is slow/fails during an unattended run.
3. Wire `/schedule-multi` the same way you already wired `/schedule` — it reuses your
   existing `ICloudTarget`/`TargetRouter`/GitHub Actions dispatch untouched.
4. Extend your GitHub Actions cron (3x/day) to call `/advise-multi/compare` and persist the
   JSON response (S3, or a simple append to a CSV on the runner, or push to your existing
   SQL Server audit sink if you re-enable `EnableDatabaseLogging`) — that log *is* your
   four-week dataset for the results chapter.
5. Only once (1)-(4) work reliably: add the real production workload execution (CI/CD job,
   batch job, load test) that actually runs in both the single- and multi-objective picked
   region, per your methodology §V.D.
