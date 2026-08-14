using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using CarbonAware.Core.Auditing;

namespace CarbonAware.Core;

public sealed class WattTimeTwoModeEngine : IPolicyEngine
{
    private readonly IRegionMapper _mapper;
    private readonly ICarbonSignalProvider _signals;
    private readonly IAuditSink _audit;
    private readonly ICorrelationContext _correlationContext;

    public WattTimeTwoModeEngine(
        IRegionMapper mapper,
        ICarbonSignalProvider signals,
        ICorrelationContext correlation,
        IAuditSink audit)
    {
        _mapper = mapper;
        _signals = signals;
        _audit = audit;
        _correlationContext = correlation;
    }

    public async Task<AdviceResult> AdviseAsync(
        JobSpec job,
        PolicySpec policy,
        CorrelationContext correlation,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var isSchedule = string.Equals(policy.Mode, "schedule_at", StringComparison.OrdinalIgnoreCase);

        // Batch duration validation
        var batchMinutes = policy.BatchDurationMinutes.GetValueOrDefault(0);
        if (batchMinutes > 0 && batchMinutes % 5 != 0)
            throw new ArgumentException("BatchDurationMinutes must be multiple of 5.");
        if (batchMinutes > 300)
            throw new ArgumentException("BatchDurationMinutes cannot exceed 300 (5 hours).");

        var isBatch = batchMinutes > 0;

        // Schedule window [from..until]
        DateTimeOffset windowFrom = policy.ScheduleFrom;
        DateTimeOffset windowUntil = policy.ScheduleUntil;

        if (windowUntil <= windowFrom)
        {
            windowUntil = windowFrom.AddMinutes(5);
        }

        // Resolve candidates
        var rawCandidates = BuildCandidates(job, policy);
        var candidates = new List<(string cloud, string region, string zone)>();
        var invalid = new List<(string cloud, string region)>();

        foreach (var (cloud, region) in rawCandidates)
        {
            var zone = _mapper.GetGridZones(cloud, region);
            if (string.IsNullOrWhiteSpace(zone))
                invalid.Add((cloud, region));
            else
                candidates.Add((cloud, region, zone));
        }

        if (candidates.Count == 0)
        {
            var detail = string.Join(", ", invalid.Select(x => $"{x.cloud}:{x.region}"));
            throw new ArgumentException(
                $"No valid cloud/region mappings for: {detail}. " +
                "Use exact region IDs (e.g., azure:eastus, gcp:us-east1, aws:us-east-1).");
        }

        // Branch by mode
        if (!isSchedule)
        {
            // RUN_NOW (single or batch)
            return isBatch
                ? await AdviseRunNowBatchAsync(job, policy, candidates, now, correlation, batchMinutes, ct)
                : await AdviseRunNowAsync(job, policy, candidates, now, correlation, ct);
        }
        else
        {
            // SCHEDULE_AT – pass full window [from..until]
            return isBatch
                ? await AdviseScheduleAtBatchAsync(job, policy, candidates, windowFrom, windowUntil, batchMinutes, correlation, ct)
                : await AdviseScheduleAtAsync(job, policy, candidates, windowFrom, windowUntil, correlation, ct);
        }
    }

    // ----------------------------------------------------------------
    // run_now (single-point)
    // ----------------------------------------------------------------
    private async Task<AdviceResult> AdviseRunNowAsync(
        JobSpec job,
        PolicySpec policy,
        List<(string cloud, string region, string zone)> candidates,
        DateTimeOffset now,
        CorrelationContext correlation,
        CancellationToken ct)
    {
        var scored = new List<(string cloud, string region, double? moer, CarbonSignal? sig)>();

        foreach (var (cloud, region, zone) in candidates)
        {
            var sig = await _signals.GetSignalsAsync(zone, now, marginal: true, ct);
            double? moer =
                (sig is not null && double.IsFinite(sig.IntensityGPerKwh) && sig.IntensityGPerKwh > 0)
                    ? sig.IntensityGPerKwh
                    : (double?)null;

            scored.Add((cloud, region, moer, sig));
        }

        var moerRows = scored.Where(s => s.moer.HasValue).ToList();
        if (moerRows.Count == 0)
        {
            var fb = ResolveFallback(job, policy);
            return new AdviceResult(
                Cloud: fb.Cloud,
                Region: fb.Region,
                When: now,
                Rationale: "run_now: no usable MOER for candidates; using fallback " +
                           $"{fb.Cloud}:{fb.Region}",
                EstimatedIntensityGPerKwh: null
            );
        }

        var best = PickBestWithCloudPreference(moerRows, x => x.moer, x => x.cloud, policy.CloudPreference);
        var highest = moerRows.OrderByDescending(s => s.moer!.Value).First();
        var avgMoer = moerRows.Average(s => s.moer!.Value);
        var avgSavingPct = avgMoer > 0
            ? 100.0 * (avgMoer - best.moer!.Value) / avgMoer
            : (double?)null;

        IEnumerable<AdviceCandidateRecord> candLog = moerRows.Select(m => new AdviceCandidateRecord
        {
            Cloud = m.cloud,
            Region = m.region,
            MoerAtTarget = ConvertTogperKwh(m.moer)
        });

        await _audit.LogAdviceAsync(new AdviceRecord
        {
            Mode = "run_now",
            TargetWhen = now,
            PreferredCloudsCsv = string.Join(",", job.GetEffectiveClouds()),
            PreferredRegionsCsv = policy.PreferredLocations?.Any() == true
                                ? string.Join(",", policy.PreferredLocations.Select(l => l.Region))
                                : null,

            SelectedCloud = best.cloud,
            SelectedRegion = best.region,
            SelectedWhen = now,
            SelectedMoerGPerKwh = ConvertTogperKwh(best.moer),
            Rationale = $"run_now: cleanest now is {best.cloud}:{best.region} ({ConvertTogperKwh(best.moer):F1} g/kWh, via watttime.v3.forecast@now)",

            HighestEmissionCloud = highest.cloud,
            HighestEmissionRegion = highest.region,
            HighestEmissionGPerKwh = ConvertTogperKwh(highest.moer),
            EstimatedSavingGPerKwh = ConvertTogperKwh(highest.moer - best.moer),
            EstimatedSavingPercent = highest.moer > 0
                ? 100.0 * (highest.moer.Value - best.moer.Value) / highest.moer.Value
                : (double?)null,
            AverageEmissionGPerKwh = ConvertTogperKwh(avgMoer),
            AverageEstimatedSavingPercent = avgSavingPct,

            BestWindowCloud = null,
            BestWindowRegion = null,
            BestWindowMoerGPerKwh = null,
            BestWindowWhen = null,
            CreatedUtc = now,
            RequestId = correlation.Current
        }, candLog, ct);

        return new AdviceResult(
            Cloud: best.cloud,
            Region: best.region,
            When: now,
            Rationale: $"run_now: cleanest now is {best.cloud}:{best.region} ({ConvertTogperKwh(best.moer):F1} g/kWh, via watttime.v3.forecast@now)",
            EstimatedIntensityGPerKwh: ConvertTogperKwh(best.moer),
            HighestEmissionCloud: highest.cloud,
            HighestEmissionRegion: highest.region,
            HighestEmissionGPerKwh: ConvertTogperKwh(highest.moer),
            AverageEstimatedSavingPercent: avgSavingPct
        );
    }

    // ----------------------------------------------------------------
    // schedule_at (single-point, window [from..until])
    // ----------------------------------------------------------------
    private async Task<AdviceResult> AdviseScheduleAtAsync(
    JobSpec job,
    PolicySpec policy,
    List<(string cloud, string region, string zone)> candidates,
    DateTimeOffset from,
    DateTimeOffset until,
    CorrelationContext correlation,
    CancellationToken ct)
    {
        if (_signals is not IBestWindowSignalProvider bestProv)
        {
            var fb = ResolveFallback(job, policy);
            return new AdviceResult(
                Cloud: fb.Cloud,
                Region: fb.Region,
                When: until,
                Rationale: "schedule_at: provider cannot compute best window; using fallback " +
                           $"{fb.Cloud}:{fb.Region}",
                EstimatedIntensityGPerKwh: null
            );
        }

        var perCandidate = new List<(string cloud, string region, double? bestMoer, DateTimeOffset? at)>();

        foreach (var (cloud, region, zone) in candidates)
        {
            var (bestMoer, bestAt, _h) =
                await bestProv.GetBestUntilAsync(zone, until, ct);

            perCandidate.Add((cloud, region, bestMoer, bestAt));
        }

        var usable = perCandidate
            .Where(x => x.bestMoer.HasValue)
            .ToList();

        if (usable.Count == 0)
        {
            var fb = ResolveFallback(job, policy);
            return new AdviceResult(
                Cloud: fb.Cloud,
                Region: fb.Region,
                When: until,
                Rationale: $"schedule_at: no best window found; using fallback {fb.Cloud}:{fb.Region}",
                EstimatedIntensityGPerKwh: null
            );
        }

        var best = PickBestWithCloudPreference(
            usable,
            x => x.bestMoer,
            x => x.cloud,
            policy.CloudPreference
        );

        string bwCloud = best.cloud;
        string bwRegion = best.region;
        double bwMoer = best.bestMoer!.Value;
        DateTimeOffset bwWhen = best.at!.Value;

        var highestByBest = usable
            .OrderByDescending(x => x.bestMoer!.Value)
            .Select(x => (x.cloud, x.region, x.bestMoer!.Value))
            .First();

        double avgByBest = usable.Average(x => x.bestMoer!.Value);

        double? avgSavingPct =
            avgByBest > 0
                ? 100.0 * (avgByBest - bwMoer) / avgByBest
                : (double?)null;

        IEnumerable<AdviceCandidateRecord> candLog = usable.Select(m => new AdviceCandidateRecord
        {
            Cloud = m.cloud,
            Region = m.region,
            BestMoerUntilTarget = ConvertTogperKwh(m.bestMoer),
            BestMoerAt = m.at
        });

        var localTz = TimeZoneInfo.Local;
        const string RATIONALE_FMT = "yyyy-MM-dd-HH:mm";

        string fromLocalStr = TimeZoneInfo
            .ConvertTime(from.UtcDateTime, localTz)
            .ToString(RATIONALE_FMT);

        string untilLocalStr = TimeZoneInfo
            .ConvertTime(until.UtcDateTime, localTz)
            .ToString(RATIONALE_FMT);

        string bwLocalStr = TimeZoneInfo
            .ConvertTime(bwWhen.UtcDateTime, localTz)
            .ToString(RATIONALE_FMT);

        var rationaleText =
            $"schedule until: best window within [{fromLocalStr}..{untilLocalStr}] " +
            $"is {bwCloud}:{bwRegion} @ {bwLocalStr} ({ConvertTogperKwh(bwMoer):F1} g/kWh, via watttime.v3.forecast@best-until). " +
            "Savings vs candidates' best-until-target shown below.";

        await _audit.LogAdviceAsync(new AdviceRecord
        {
            Mode = "schedule_at",
            TargetWhen = until,
            PreferredCloudsCsv = string.Join(",", job.GetEffectiveClouds()),
            PreferredRegionsCsv = policy.PreferredLocations?.Any() == true
                                ? string.Join(",", policy.PreferredLocations.Select(l => l.Region))
                                : null,

            SelectedCloud = bwCloud,
            SelectedRegion = bwRegion,
            SelectedWhen = bwWhen,
            SelectedMoerGPerKwh = ConvertTogperKwh(bwMoer),
            Rationale = rationaleText,

            HighestEmissionCloud = highestByBest.cloud,
            HighestEmissionRegion = highestByBest.region,
            HighestEmissionGPerKwh = ConvertTogperKwh(highestByBest.Value),
            EstimatedSavingGPerKwh = ConvertTogperKwh(highestByBest.Value - bwMoer),
            EstimatedSavingPercent =
                highestByBest.Value > 0
                    ? 100.0 * (highestByBest.Value - bwMoer) / highestByBest.Value
                    : (double?)null,
            AverageEmissionGPerKwh = ConvertTogperKwh(avgByBest),
            AverageEstimatedSavingPercent = avgSavingPct,

            BestWindowCloud = bwCloud,
            BestWindowRegion = bwRegion,
            BestWindowMoerGPerKwh = ConvertTogperKwh(bwMoer),
            BestWindowWhen = bwWhen,
            CreatedUtc = DateTimeOffset.UtcNow,
            RequestId = correlation.Current
        }, candLog, ct);

        return new AdviceResult(
            Cloud: bwCloud,
            Region: bwRegion,
            When: bwWhen,
            Rationale: rationaleText,
            EstimatedIntensityGPerKwh: ConvertTogperKwh(bwMoer),
            HighestEmissionCloud: highestByBest.cloud,
            HighestEmissionRegion: highestByBest.region,
            HighestEmissionGPerKwh: ConvertTogperKwh(highestByBest.Value),
            AverageEstimatedSavingPercent: avgSavingPct,
            BestWindowCloud: bwCloud,
            BestWindowRegion: bwRegion,
            BestWindowMoerGPerKwh: ConvertTogperKwh(bwMoer),
            BestWindowWhen: bwWhen
        );
    }


    // ----------------------------------------------------------------
    // run_now + batch
    // ----------------------------------------------------------------
    private async Task<AdviceResult> AdviseRunNowBatchAsync(
        JobSpec job,
        PolicySpec policy,
        List<(string Cloud, string Region, string Zone)> candidates,
        DateTimeOffset now,
        CorrelationContext correlation,
        int batchMinutes,
        CancellationToken ct)
    {
        if (_signals is not IBatchSignalProvider batchProv)
        {
            return await AdviseRunNowAsync(job, policy, candidates, now, correlation, ct);
        }

        var rows = new List<(string cloud, string region, double? avg)>();

        foreach (var (cloud, region, zone) in candidates)
        {
            var avg = await batchProv.GetBatchAverageNowAsync(zone, batchMinutes, ct);
            rows.Add((cloud, region, avg));
        }

        var usable = rows.Where(r => r.avg.HasValue).ToList();
        if (usable.Count == 0)
        {
            var fb = ResolveFallback(job, policy);
            return new AdviceResult(
                Cloud: fb.Cloud,
                Region: fb.Region,
                When: now,
                Rationale: $"run_now batch ({batchMinutes} min): no usable MOER; using fallback {fb.Cloud}:{fb.Region}",
                EstimatedIntensityGPerKwh: null
            );
        }

        var best = PickBestWithCloudPreference(usable, x => x.avg, x => x.cloud, policy.CloudPreference);
        var highest = usable.OrderByDescending(r => r.avg!.Value).First();
        var avgAll = usable.Average(r => r.avg!.Value);
        var avgSavingPct = avgAll > 0
            ? 100.0 * (avgAll - best.avg!.Value) / avgAll
            : (double?)null;

        IEnumerable<AdviceCandidateRecord> candLog = usable.Select(r => new AdviceCandidateRecord
        {
            Cloud = r.cloud,
            Region = r.region,
            MoerAtTarget = ConvertTogperKwh(r.avg)
        });

        await _audit.LogAdviceAsync(new AdviceRecord
        {
            Mode = "run_now_batch",
            TargetWhen = now,
            PreferredCloudsCsv = string.Join(",", job.GetEffectiveClouds()),
            PreferredRegionsCsv = policy.PreferredLocations?.Any() == true
                                ? string.Join(",", policy.PreferredLocations.Select(l => l.Region))
                                : null,

            SelectedCloud = best.cloud,
            SelectedRegion = best.region,
            SelectedWhen = now,
            SelectedMoerGPerKwh = ConvertTogperKwh(best.avg),
            Rationale = $"run_now batch ({batchMinutes} min): cleanest average over next {batchMinutes} minutes is {best.cloud}:{best.region} ({ConvertTogperKwh(best.avg):F1} g/kWh).",

            HighestEmissionCloud = highest.cloud,
            HighestEmissionRegion = highest.region,
            HighestEmissionGPerKwh = ConvertTogperKwh(highest.avg),
            EstimatedSavingGPerKwh = ConvertTogperKwh(highest.avg - best.avg),
            EstimatedSavingPercent = (highest.avg > 0)
                ? 100.0 * (highest.avg.Value - best.avg.Value) / highest.avg.Value
                : (double?)null,
            AverageEstimatedSavingPercent = avgSavingPct,
            AverageEmissionGPerKwh = ConvertTogperKwh(avgAll),
            CreatedUtc = now,
            RequestId = correlation.Current
        }, candLog, ct);

        return new AdviceResult(
            Cloud: best.cloud,
            Region: best.region,
            When: now,
            Rationale: $"run_now batch ({batchMinutes} min): cleanest average over next {batchMinutes} minutes is {best.cloud}:{best.region} ({ConvertTogperKwh(best.avg):F1} g/kWh).",
            EstimatedIntensityGPerKwh: ConvertTogperKwh(best.avg),
            HighestEmissionCloud: highest.cloud,
            HighestEmissionRegion: highest.region,
            HighestEmissionGPerKwh: ConvertTogperKwh(highest.avg),
            AverageEstimatedSavingPercent: avgSavingPct
        );
    }

    // ----------------------------------------------------------------
    // schedule_at + batch, window [from..until]
    // ----------------------------------------------------------------
    private async Task<AdviceResult> AdviseScheduleAtBatchAsync(
        JobSpec job,
        PolicySpec policy,
        List<(string Cloud, string Region, string Zone)> candidates,
        DateTimeOffset from,
        DateTimeOffset until,
        int batchMinutes,
        CorrelationContext correlation,
        CancellationToken ct)
    {
        if (_signals is not IBatchSignalProvider batchProv)
        {
            var fb = ResolveFallback(job, policy);
            return new AdviceResult(
                Cloud: fb.Cloud,
                Region: fb.Region,
                When: until,
                Rationale: "schedule_at(batch): provider cannot compute batch window; using fallback " +
                           $"{fb.Cloud}:{fb.Region}",
                EstimatedIntensityGPerKwh: null
            );
        }

        if (batchMinutes <= 0 || batchMinutes > 300)
            throw new ArgumentOutOfRangeException(
                nameof(batchMinutes),
                "Batch duration must be between 5 and 300 minutes.");

        var perCandidate = new List<(string Cloud, string Region, string Zone,
                                     double? BestAvg, DateTimeOffset? Start)>();

        foreach (var (cloud, region, zone) in candidates)
        {
            var (bestAvg_, start, _usedH) =
                await batchProv.GetBestBatchWindowUntilAsync(zone, from, until, batchMinutes, ct);

            perCandidate.Add((cloud, region, zone, bestAvg_, start));
        }

        var usable = perCandidate
            .Where(x => x.BestAvg.HasValue &&
                        double.IsFinite(x.BestAvg.Value) &&
                        x.BestAvg.Value > 0)
            .ToList();

        if (usable.Count == 0)
        {
            var fb = ResolveFallback(job, policy);
            return new AdviceResult(
                Cloud: fb.Cloud,
                Region: fb.Region,
                When: until,
                Rationale: $"schedule_at(batch): no usable batch windows found; using fallback {fb.Cloud}:{fb.Region}",
                EstimatedIntensityGPerKwh: null
            );
        }

        var best = PickBestWithCloudPreference(usable, x => x.BestAvg, x => x.Cloud, policy.CloudPreference);
        var highest = usable.OrderByDescending(x => x.BestAvg!.Value).First();
        var avgAll = usable.Average(x => x.BestAvg!.Value);

        var bestAvg = best.BestAvg!.Value;
        var highestAvg = highest.BestAvg!.Value;

        double? savingVsHighestG = highestAvg - bestAvg;
        double? savingVsHighestPct = highestAvg > 0
            ? 100.0 * (highestAvg - bestAvg) / highestAvg
            : (double?)null;

        double? savingVsAveragePct = avgAll > 0
            ? 100.0 * (avgAll - bestAvg) / avgAll
            : (double?)null;

        var bestStartUtc = best.Start!.Value;

        DateTimeOffset ToLocal(DateTimeOffset t) =>
            t.ToOffset(TimeZoneInfo.Local.GetUtcOffset(t));

        string Fmt(DateTimeOffset t) => t.ToString("yyyy-MM-dd-HH:mm");

        var fromLocal = ToLocal(from);
        var untilLocal = ToLocal(until);
        var bestLocal = ToLocal(bestStartUtc);

        var rationale =
            $"schedule_until_batch: best {batchMinutes} min window within [{Fmt(fromLocal)}..{Fmt(untilLocal)}] is " +
            $"{best.Cloud}:{best.Region} starting at {Fmt(bestLocal)} " +
            $"({ConvertTogperKwh(bestAvg):F1} g/kWh avg, via watttime.v3.forecast@batch-best-until). " +
            "Savings vs candidates' best batch windows shown below.";

        IEnumerable<AdviceCandidateRecord> candLog = usable.Select(c => new AdviceCandidateRecord
        {
            Cloud = c.Cloud,
            Region = c.Region,
            BestMoerUntilTarget = ConvertTogperKwh(c.BestAvg),
            BestMoerAt = c.Start
        });

        await _audit.LogAdviceAsync(new AdviceRecord
        {
            Mode = "schedule_at_batch",
            TargetWhen = until,
            PreferredCloudsCsv = string.Join(",", job.GetEffectiveClouds()),
            PreferredRegionsCsv = policy.PreferredLocations?.Any() == true
                                ? string.Join(",", policy.PreferredLocations.Select(l => l.Region))
                                : null,

            SelectedCloud = best.Cloud,
            SelectedRegion = best.Region,
            SelectedWhen = bestStartUtc,
            SelectedMoerGPerKwh = ConvertTogperKwh(bestAvg),
            Rationale = rationale,

            HighestEmissionCloud = highest.Cloud,
            HighestEmissionRegion = highest.Region,
            HighestEmissionGPerKwh = ConvertTogperKwh(highestAvg),
            EstimatedSavingGPerKwh = ConvertTogperKwh(savingVsHighestG),
            EstimatedSavingPercent = savingVsHighestPct,
            AverageEstimatedSavingPercent = savingVsAveragePct,
            AverageEmissionGPerKwh = ConvertTogperKwh(avgAll),

            BestWindowCloud = best.Cloud,
            BestWindowRegion = best.Region,
            BestWindowMoerGPerKwh = ConvertTogperKwh(bestAvg),
            BestWindowWhen = bestStartUtc,
            CreatedUtc = DateTimeOffset.UtcNow,
            RequestId = correlation.Current
        }, candLog, ct);

        return new AdviceResult(
            Cloud: best.Cloud,
            Region: best.Region,
            When: bestStartUtc,
            Rationale: rationale,
            EstimatedIntensityGPerKwh: ConvertTogperKwh(bestAvg),
            HighestEmissionCloud: highest.Cloud,
            HighestEmissionRegion: highest.Region,
            HighestEmissionGPerKwh: ConvertTogperKwh(highestAvg),
            AverageEstimatedSavingPercent: savingVsAveragePct,
            BestWindowCloud: best.Cloud,
            BestWindowRegion: best.Region,
            BestWindowMoerGPerKwh: ConvertTogperKwh(bestAvg),
            BestWindowWhen: bestStartUtc
        );
    }

    // ----------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------
    private static IReadOnlyList<(string Cloud, string Region)> BuildCandidates(
        JobSpec job,
        PolicySpec policy)
    {
        var clouds = job.GetEffectiveClouds();
        if (clouds.Count == 0) clouds = new List<string> { "gcp" };

        if (policy.PreferredLocations is { Count: > 0 })
        {
            return policy.PreferredLocations
                .Select(l => (l.Cloud, l.Region))
                .Distinct(StringTupleComparer.Instance)
                .ToList();
        }

        var regions = (policy.PreferredRegions is { Count: > 0 })
            ? policy.PreferredRegions
            : new List<string> { policy.FallbackRegion ?? "us-east1" };

        var list = new List<(string Cloud, string Region)>();
        foreach (var c in clouds)
            foreach (var r in regions)
                list.Add((c, r));

        return list.Distinct(StringTupleComparer.Instance).ToList();
    }

    private static LocationSpec ResolveFallback(JobSpec job, PolicySpec policy)
    {
        var clouds = job.GetEffectiveClouds();
        var fbCloud = (clouds.Count > 0) ? clouds[0] : "gcp";
        var fbRegion = policy.FallbackRegion
                       ?? policy.PreferredRegions?.FirstOrDefault()
                       ?? "us-east1";
        return new LocationSpec(fbCloud, fbRegion);
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string Cloud, string Region)>
    {
        public static readonly StringTupleComparer Instance = new();

        public bool Equals((string Cloud, string Region) x, (string Cloud, string Region) y) =>
            string.Equals(x.Cloud, y.Cloud, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Region, y.Region, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Cloud, string Region) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Cloud),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Region));
    }
    private static string NormalizeCloud(string? c) =>
    string.IsNullOrWhiteSpace(c) ? string.Empty : c.Trim().ToLowerInvariant();

    /// <summary>
    /// Among candidates with the lowest MOER, prefer clouds according to policy.CloudPreference.
    /// If no preference is given or it doesn’t match, falls back to the first lowest-MOER candidate.
    /// </summary>
    private static T PickBestWithCloudPreference<T>(
        IEnumerable<T> candidates,
        Func<T, double?> moerSelector,
        Func<T, string> cloudSelector,
        IReadOnlyList<string>? cloudPreference)
    {
        var list = candidates
            .Where(c =>
            {
                var m = moerSelector(c);
                return m.HasValue && double.IsFinite(m.Value);
            })
            .ToList();

        if (list.Count == 0)
            throw new InvalidOperationException("PickBestWithCloudPreference called with no usable candidates.");

        var minMoer = list.Min(c => moerSelector(c)!.Value);
        const double Eps = 1e-6;

        var winners = list
            .Where(c => Math.Abs(moerSelector(c)!.Value - minMoer) <= Eps)
            .ToList();

        // If only one winner or no preference specified -> just return first
        if (winners.Count == 1 || cloudPreference is null || cloudPreference.Count == 0)
            return winners[0];

        var prefNorm = cloudPreference.Select(NormalizeCloud).ToList();

        foreach (var prefCloud in prefNorm)
        {
            var idx = winners.FindIndex(c =>
                NormalizeCloud(cloudSelector(c)) == prefCloud);

            if (idx >= 0)
                return winners[idx];
        }

        // No winner matched any preferred cloud → just return first
        return winners[0];
    }

    private double? ConvertTogperKwh(double? lbsperMwh)
    {
        return lbsperMwh * 0.45359237;
    }

}