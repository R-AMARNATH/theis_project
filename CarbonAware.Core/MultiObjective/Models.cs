using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CarbonAware.Core;

// ---------------------------------------------------------------------
// New signals (cost, latency) — mirror the shape of CarbonSignal
// ---------------------------------------------------------------------

public record CostSignal(
    string Cloud,
    string Region,
    string InstanceType,
    double HourlyUsd,
    DateTimeOffset RetrievedAt,
    string Source
);

public record LatencySignal(
    string Cloud,
    string Region,
    double? RoundTripMs,
    DateTimeOffset MeasuredAt,
    bool Reachable,
    string Source
);

public interface ICostSignalProvider
{
    Task<CostSignal?> GetCostAsync(string cloud, string region, CancellationToken ct = default);
}

public interface ILatencySignalProvider
{
    Task<LatencySignal> GetLatencyAsync(string cloud, string region, CancellationToken ct = default);
}

// ---------------------------------------------------------------------
// Weights — Table 1 in the proposal (Carbon-prioritised / Balanced / Cost-prioritised)
// ---------------------------------------------------------------------

public record ObjectiveWeights(double Carbon, double Cost, double Latency)
{
    public static readonly ObjectiveWeights CarbonPrioritised = new(0.60, 0.20, 0.20);
    public static readonly ObjectiveWeights Balanced = new(0.34, 0.33, 0.33);
    public static readonly ObjectiveWeights CostPrioritised = new(0.20, 0.60, 0.20);

    public static ObjectiveWeights FromProfileName(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "carbon" or "carbon-prioritised" or "carbon_prioritised" => CarbonPrioritised,
        "cost" or "cost-prioritised" or "cost_prioritised" => CostPrioritised,
        "balanced" or null or "" => Balanced,
        _ => throw new ArgumentException(
            $"Unknown weight profile '{name}'. Use 'carbon', 'balanced', 'cost', or supply CustomWeights.")
    };

    public ObjectiveWeights Normalized()
    {
        var sum = Carbon + Cost + Latency;
        if (sum <= 0 || !double.IsFinite(sum)) return Balanced;
        return new ObjectiveWeights(Carbon / sum, Cost / sum, Latency / sum);
    }
}

// ---------------------------------------------------------------------
// Per-candidate raw + normalised readings, and the overall result
// ---------------------------------------------------------------------

public record RegionCandidateScore(
    string Cloud,
    string Region,
    double? MoerGPerKwh,
    double? CostUsdPerHr,
    double? LatencyMs,
    double? NormMoer,
    double? NormCost,
    double? NormLatency,
    double? CompositeScore,
    bool Excluded,
    string? ExclusionReason,
    // Provenance — lets you report in the thesis exactly which readings were
    // live API calls vs. the static fallback table, per candidate per cycle.
    bool? CostIsLive = null,
    string? CostSource = null,
    string? LatencySource = null
);

public record MultiObjectiveAdviceResult(
    string? Cloud,
    string? Region,
    DateTimeOffset When,
    string Rationale,
    ObjectiveWeights Weights,
    string WeightProfile,
    IReadOnlyList<RegionCandidateScore> Candidates,
    string? SingleObjectiveCloud,
    string? SingleObjectiveRegion,
    bool RegionsDiffer
);

// Request DTO — extends OrchestrationRequest with a weight profile.
// CycleId is optional (see OrchestrationRequest) -- pass your experiment cycle id here
// for /schedule-multi so it lines up with the predicted/actual rows in CycleResultLog.
public record MultiObjectiveRequest(
    PolicySpec Policy,
    JobSpec Job,
    string? WeightProfile = null,
    ObjectiveWeights? CustomWeights = null,
    string? CycleId = null
)
{
    public ObjectiveWeights ResolveWeights() =>
        (CustomWeights ?? ObjectiveWeights.FromProfileName(WeightProfile)).Normalized();
}

public interface IMultiObjectivePolicyEngine
{
    Task<MultiObjectiveAdviceResult> AdviseMultiAsync(
        JobSpec job,
        PolicySpec policy,
        ObjectiveWeights weights,
        CorrelationContext correlation,
        CancellationToken ct = default);
}
