namespace CarbonAware.Api.Data;

// One row per experiment cycle+region deployment. Predicted fields are filled in
// when the scheduler advises (POST /results/predicted); actual fields are filled
// in when batch_job.py finishes on the VM and the GitHub Actions workflow (or a
// small orchestrator script) reports them back (POST /results/actual). Both
// endpoints upsert by CycleId, so it doesn't matter which one arrives first.
public sealed class CycleResultLog
{
    public long Id { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedUtc { get; set; }

    // identity
    public string CycleId { get; set; } = default!;
    public string? ObjectiveType { get; set; }     // single | multi
    public string? WeightConfig { get; set; }      // carbon-prioritised | balanced | cost-prioritised
    public string CloudProvider { get; set; } = default!;
    public string Region { get; set; } = default!;

    // predicted -- from the scheduler at scheduling time
    public double? PredictedMoerGPerKwh { get; set; }
    public double? PredictedCostUsdPerHr { get; set; }
    public double? PredictedLatencyMs { get; set; }   // NOTE: HTTP HEAD round-trip time (ms), NOT the same
                                                        // metric as LatencyActualSec below (storage download
                                                        // duration, sec) -- don't treat these as directly
                                                        // comparable predicted-vs-actual without normalizing.
    public DateTimeOffset? PredictedAtUtc { get; set; }

    // actual -- from batch_job.py after the deployment runs (via POST /results/actual)
    public DateTimeOffset? ActualTimestampStart { get; set; }
    public DateTimeOffset? ActualTimestampEnd { get; set; }
    public double? LatencyActualSec { get; set; }    // NOTE: storage-download duration, not RTT -- see PredictedLatencyMs comment
    public double? ExecutionTimeSec { get; set; }
    public bool? DeploymentSuccess { get; set; }
    public string? ErrorNotes { get; set; }

    // actual MOER/cost are NOT measured by batch_job.py (the VM has no WattTime/pricing
    // creds). Instead, POST /results/actual asks the API's own WattTime + cost providers
    // for a reading of that cloud/region at the moment the actual result is reported --
    // i.e. "conditions at reporting time", not a true historical value for the exact
    // execution window. This is a deliberate approximation; note it as a limitation.
    public double? ActualMoerGPerKwh { get; set; }
    public string? ActualMoerSource { get; set; }     // e.g. "watttime-at-report-time"
    public double? ActualCostUsdPerHr { get; set; }
    public string? ActualCostSource { get; set; }     // e.g. "azure-retail-prices" | "static-fallback:..."
}
