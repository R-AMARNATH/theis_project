using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CarbonAware.Core;

// NEW: identifies a concrete cloud+region candidate
public record LocationSpec(string Cloud, string Region);

public record CarbonSignal(
    string Zone,
    DateTimeOffset Timestamp,
    double IntensityGPerKwh,
    bool IsMarginal,
    int? ForecastHorizonMin,
    string Source
);


public interface ICarbonSignalProvider
{
    Task<CarbonSignal> GetSignalsAsync(
        string zones,
        DateTimeOffset at,
        bool marginal,
        CancellationToken ct = default);
}

// Optional capability for providers that can do batch-style averaging windows.
// Keeps Core independent of concrete provider classes.
public interface IBatchSignalProvider : ICarbonSignalProvider
{
    /// <summary>
    /// For run_now batch: average MOER over [now .. now+batchMinutes] for a region.
    /// Returns null if no data.
    /// </summary>
    Task<double?> GetBatchAverageNowAsync(
        string region,
        int batchMinutes,
        CancellationToken ct = default);

    /// <summary>
    /// For schedule_at batch: best (lowest average) batchMinutes window within [now..target].
    /// Returns (bestAvgMoer, startAt, usedHorizonHours). Values can be null if no data.
    /// </summary>
    Task<(double? bestAvgMoer, DateTimeOffset? startAt, int usedHorizonHours)>
        GetBestBatchWindowUntilAsync(
            string region,
            DateTimeOffset startAt,
            DateTimeOffset target,
            int batchMinutes,
            CancellationToken ct = default);
}


public interface IRegionMapper
{
    // e.g., ("gcp", "europe-west4") -> ["FR"]
    string GetGridZones(string cloud, string region);
    IReadOnlyDictionary<string, IReadOnlyList<string>> ListAllRegionsByCloud();
}

public record PolicySpec
{
    // Two modes only
    // "run_now"      -> choose cleanest region right now (current MOER)
    // "schedule_at"  -> choose cleanest region at a specific time (forecast MOER)
    public string Mode { get; init; } = "run_now";

    // Optional: only used when Mode == "schedule_at"
    public DateTimeOffset? ScheduledAt { get; init; }

    // Region preferences still apply
    public IReadOnlyList<string>? PreferredRegions { get; init; }
    public string? FallbackRegion { get; init; }

    public DateTimeOffset ScheduleFrom { get; init; }   // lower bound of window
    public DateTimeOffset ScheduleUntil { get; init; }  // upper bound of window

    // Preferred cloud+region candidates(recommended)
    public IReadOnlyList<LocationSpec>? PreferredLocations { get; init; }

    public int? BatchDurationMinutes { get; init; }
    public IReadOnlyList<string>? CloudPreference { get; init; }
}


public record JobSpec
{
    // Preferred field
    [JsonPropertyName("clouds")]
    public IReadOnlyList<string>? Clouds { get; init; }

    // Accept "cloud" as either a string OR an array
    [JsonPropertyName("cloud")]
    public JsonElement CloudCompat { get; init; } = default;

    // Helper to normalize to a list
    public IReadOnlyList<string> GetEffectiveClouds()
    {
        if (Clouds is { Count: > 0 }) return Clouds;

        if (CloudCompat.ValueKind == JsonValueKind.String)
            return new[] { CloudCompat.GetString()! };

        if (CloudCompat.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var el in CloudCompat.EnumerateArray())
                if (el.ValueKind == JsonValueKind.String) list.Add(el.GetString()!);
            if (list.Count > 0) return list;
        }

        return Array.Empty<string>();
    }
}


public record AdviceResult(
    string Cloud,
    string Region,
    DateTimeOffset When,
    string Rationale,
    double? EstimatedIntensityGPerKwh,
    string? HighestEmissionCloud = null,
    string? HighestEmissionRegion = null,
    double? HighestEmissionGPerKwh = null,
    double? AverageEstimatedSavingPercent = null,
    // Best forecast point across ALL candidates up to the target
    string? BestWindowCloud = null,
    string? BestWindowRegion = null,
    double? BestWindowMoerGPerKwh = null,
    DateTimeOffset? BestWindowWhen = null
)
{
    // Absolute savings vs. highest (in g/kWh)
    public double? EstimatedSavingGPerKwh =>
        (HighestEmissionGPerKwh.HasValue && EstimatedIntensityGPerKwh.HasValue)
            ? HighestEmissionGPerKwh.Value - EstimatedIntensityGPerKwh.Value
            : (double?)null;

    // % cleaner vs. the dirtiest MOER candidate
    public double? EstimatedSavingPercent =>
        (HighestEmissionGPerKwh.HasValue &&
         EstimatedIntensityGPerKwh.HasValue &&
         HighestEmissionGPerKwh.Value > 0)
        ? 100.0 * (HighestEmissionGPerKwh.Value - EstimatedIntensityGPerKwh.Value) / HighestEmissionGPerKwh.Value
        : (double?)null;
};

// DTO to bind requests easily. CycleId is optional -- when omitted, /schedule and
// /schedule-multi generate one so the run is still logged/dispatchable, but for the
// real 84-cycle experiment you should pass your own cycle id (1-84) so predicted rows
// in CycleResultLog line up with the *_actual rows batch_job.py reports later.
public record OrchestrationRequest(PolicySpec Policy, JobSpec Job, string? CycleId = null);

public interface IPolicyEngine
{
    Task<AdviceResult> AdviseAsync(
        JobSpec job,
        PolicySpec policy,
        CorrelationContext correlation,
        CancellationToken ct = default);
}

// Carries experiment bookkeeping (cycle id, objective type, weight profile) down to the
// GitHub Actions target so the dispatched workflow gets the inputs it requires (cycleId
// is a *required* workflow input -- without this, workflow_dispatch fails with 422) and
// so the eventual POST /results/actual from batch_job.py can be matched back to the
// predicted row this scheduling call wrote.
public sealed record ExperimentContext(string CycleId, string ObjectiveType, string? WeightProfile);

public interface ICloudTarget
{
    Task<string> ScheduleAsync(
        AdviceResult advice,
        JobSpec job,
        CorrelationContext correlation,
        ExperimentContext experiment,
        CancellationToken ct = default);
}

/// <summary>
/// Optional capability for providers that can return the lowest MOER window up to a target time.
/// Keeping this in Core avoids Core → Providers references.
/// </summary>
public interface IBestWindowSignalProvider
{
    /// <summary>
    /// Returns the lowest MOER (g/kWh) and its timestamp within [now .. target] for a region,
    /// plus the horizon_hours the provider actually used.
    /// </summary>
    Task<(double? bestMoer, DateTimeOffset? at, int usedHorizonHours)>
        GetBestUntilAsync(string region, DateTimeOffset target, CancellationToken ct = default);
}
