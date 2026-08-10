namespace CarbonAware.Core.Auditing;

public interface IAuditSink
{
    Task LogWattTimeCallAsync(WattTimeCallRecord rec, CancellationToken ct = default);
    Task LogAdviceAsync(AdviceRecord rec, IEnumerable<AdviceCandidateRecord>? candidates = null, CancellationToken ct = default);
}

public sealed class WattTimeCallRecord
{
    public string Method { get; init; } = "GET";
    public string RequestUrl { get; init; } = default!;
    public string? Region { get; init; }
    public string? SignalType { get; init; }
    public int? HorizonHours { get; init; }
    public int StatusCode { get; init; }
    public bool Success { get; init; }
    public int DurationMs { get; init; }
    public string? ResponseBody { get; init; }
    public string? Error { get; init; }
    public string? SourceFile { get; init; }
    public int? SourceLine { get; init; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public Guid? RequestId { get; init; }
}

public sealed class AdviceRecord
{
    public string Mode { get; init; } = default!;               // run_now | schedule_at | multi_objective_<profile>
    public DateTimeOffset TargetWhen { get; init; }
    public string? PreferredCloudsCsv { get; init; }
    public string? PreferredRegionsCsv { get; init; }

    // multi-objective identity (null for single-objective /advise runs)
    public string? ObjectiveType { get; init; }                 // single | multi
    public string? WeightProfile { get; init; }                 // carbon-prioritised | balanced | cost-prioritised
    public double? WeightCarbon { get; init; }
    public double? WeightCost { get; init; }
    public double? WeightLatency { get; init; }

    // selected / best candidate
    public string SelectedCloud { get; init; } = default!;
    public string SelectedRegion { get; init; } = default!;
    public DateTimeOffset? SelectedWhen { get; init; }
    public double? SelectedMoerGPerKwh { get; init; }
    public double? SelectedCostUsdPerHr { get; init; }
    public double? SelectedLatencyMs { get; init; }
    public double? SelectedCompositeScore { get; init; }
    public string Rationale { get; init; } = default!;

    // worst / average -- carbon (MOER)
    public string? HighestEmissionCloud { get; init; }
    public string? HighestEmissionRegion { get; init; }
    public double? HighestEmissionGPerKwh { get; init; }
    public double? EstimatedSavingGPerKwh { get; init; }
    public double? EstimatedSavingPercent { get; init; }
    public double? AverageEmissionGPerKwh { get; init; }
    public double? AverageEstimatedSavingPercent { get; init; }

    // worst / average -- cost (multi-objective only)
    public string? HighestCostCloud { get; init; }
    public string? HighestCostRegion { get; init; }
    public double? HighestCostUsdPerHr { get; init; }
    public double? AverageCostUsdPerHr { get; init; }

    // worst / average -- latency (multi-objective only)
    public string? HighestLatencyCloud { get; init; }
    public string? HighestLatencyRegion { get; init; }
    public double? HighestLatencyMs { get; init; }
    public double? AverageLatencyMs { get; init; }

    // how many candidates fed the averages/worsts above (== "complete" candidates
    // that had all required signals; excluded candidates are logged separately
    // in AdviceCandidateRecord but don't count toward these stats)
    public int? CandidateCount { get; init; }

    // single-objective (carbon-only) pick, for multi-objective runs -- lets you
    // report "different region chosen" rate directly from the DB
    public string? SingleObjectiveCloud { get; init; }
    public string? SingleObjectiveRegion { get; init; }
    public bool? RegionsDiffer { get; init; }

    public string? BestWindowCloud { get; init; }
    public string? BestWindowRegion { get; init; }
    public double? BestWindowMoerGPerKwh { get; init; }
    public DateTimeOffset? BestWindowWhen { get; init; }
    public DateTimeOffset CreatedUtc { get; init; } 
    public Guid? RequestId { get; init; }
}

public sealed class AdviceCandidateRecord
{
    public string Cloud { get; init; } = default!;
    public string Region { get; init; } = default!;

    // carbon
    public double? MoerAtTarget { get; init; }
    public double? BestMoerUntilTarget { get; init; }
    public DateTimeOffset? BestMoerAt { get; init; }

    // cost / latency (multi-objective only)
    public double? CostUsdPerHr { get; init; }
    public double? LatencyMs { get; init; }
    public double? CompositeScore { get; init; }
    public bool? Excluded { get; init; }
    public string? ExclusionReason { get; init; }
    public bool? CostIsLive { get; init; }
    public string? CostSource { get; init; }
    public string? LatencySource { get; init; }
}

public sealed class AuditOptions
{
    public bool EnableDatabaseLogging { get; set; } = true;
}
