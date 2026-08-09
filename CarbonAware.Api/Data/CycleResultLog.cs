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
    public double? PredictedLatencyMs { get; set; }
    public DateTimeOffset? PredictedAtUtc { get; set; }

    // actual -- from batch_job.py after the deployment runs
    public DateTimeOffset? ActualTimestampStart { get; set; }
    public DateTimeOffset? ActualTimestampEnd { get; set; }
    public double? LatencyActualSec { get; set; }
    public double? ExecutionTimeSec { get; set; }
    public double? ActualMoerGPerKwh { get; set; }   // WattTime historical endpoint, if you wire it in
    public bool? DeploymentSuccess { get; set; }
    public string? ErrorNotes { get; set; }
}
