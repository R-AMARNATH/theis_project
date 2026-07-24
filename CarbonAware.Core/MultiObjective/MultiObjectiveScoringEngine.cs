using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CarbonAware.Core.Auditing;

namespace CarbonAware.Core;

/// <summary>
/// Implements Score(r) = w1*norm(MOER) + w2*norm(Cost) + w3*norm(Latency)  (proposal eq. 2)
/// with norm(x) = (xmax - x) / (xmax - xmin)                                (proposal eq. 1)
/// Runs alongside WattTimeTwoModeEngine (single-objective) rather than replacing it, so
/// /advise and /schedule keep working exactly as before.
/// </summary>
public sealed class MultiObjectiveScoringEngine : IMultiObjectivePolicyEngine
{
    private readonly IRegionMapper _mapper;
    private readonly ICarbonSignalProvider _carbon;
    private readonly ICostSignalProvider _cost;
    private readonly ILatencySignalProvider _latency;
    private readonly IAuditSink _audit;
    private const double Eps = 1e-9;

    public MultiObjectiveScoringEngine(
        IRegionMapper mapper,
        ICarbonSignalProvider carbon,
        ICostSignalProvider cost,
        ILatencySignalProvider latency,
        IAuditSink audit)
    {
        _mapper = mapper;
        _carbon = carbon;
        _cost = cost;
        _latency = latency;
        _audit = audit;
    }

    public async Task<MultiObjectiveAdviceResult> AdviseMultiAsync(
        JobSpec job,
        PolicySpec policy,
        ObjectiveWeights weights,
        CorrelationContext correlation,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        weights = weights.Normalized();

        var (usable, invalid) = CandidateResolver.ResolveWithZones(job, policy, _mapper);
        if (usable.Count == 0)
        {
            var detail = string.Join(", ", invalid.Select(x => $"{x.Item1}:{x.Item2}"));
            throw new ArgumentException(
                $"No valid cloud/region mappings for: {detail}. " +
                "Use exact region IDs (e.g., azure:eastus, gcp:us-east1, aws:us-east-1).");
        }

        // Fetch all three signals for every candidate concurrently
        var rows = await Task.WhenAll(usable.Select(async c =>
        {
            var carbonTask = _carbon.GetSignalsAsync(c.Zone, now, marginal: true, ct);
            var costTask = _cost.GetCostAsync(c.Cloud, c.Region, ct);
            var latTask = _latency.GetLatencyAsync(c.Cloud, c.Region, ct);
            await Task.WhenAll(carbonTask, costTask, latTask);

            var sig = carbonTask.Result;
            double? moer = (sig is not null && double.IsFinite(sig.IntensityGPerKwh) && sig.IntensityGPerKwh > 0)
                ? ConvertToGPerKwh(sig.IntensityGPerKwh) : null;

            var costSig = costTask.Result;
            var latSig = latTask.Result;

            return new
            {
                c.Cloud,
                c.Region,
                Moer = moer,
                Cost = costSig?.HourlyUsd,
                Lat = (latSig.Reachable ? latSig.RoundTripMs : null)
            };
        }));

        // Only candidates with all three signals participate in the composite score.
        // Missing-signal candidates are still returned (Excluded=true) so you can see
        // why a region dropped out — useful for the paper's methodology/limitations section.
        var complete = rows.Where(r => r.Moer.HasValue && r.Cost.HasValue && r.Lat.HasValue).ToList();

        var scored = new List<RegionCandidateScore>();

        if (complete.Count == 0)
        {
            foreach (var r in rows)
            {
                var missing = new List<string>();
                if (!r.Moer.HasValue) missing.Add("carbon");
                if (!r.Cost.HasValue) missing.Add("cost");
                if (!r.Lat.HasValue) missing.Add("latency");
                scored.Add(new RegionCandidateScore(r.Cloud, r.Region, r.Moer, r.Cost, r.Lat,
                    null, null, null, null, true, $"missing signal(s): {string.Join(",", missing)}"));
            }

            return new MultiObjectiveAdviceResult(
                null, null, now,
                "multi-objective: no candidate had complete carbon+cost+latency signals.",
                weights, WeightProfileLabel(weights), scored, null, null, false);
        }

        double moerMin = complete.Min(r => r.Moer!.Value), moerMax = complete.Max(r => r.Moer!.Value);
        double costMin = complete.Min(r => r.Cost!.Value), costMax = complete.Max(r => r.Cost!.Value);
        double latMin = complete.Min(r => r.Lat!.Value), latMax = complete.Max(r => r.Lat!.Value);

        double Norm(double x, double min, double max) =>
            (max - min) > Eps ? (max - x) / (max - min) : 1.0; // all-equal candidates score 1 on that axis

        foreach (var r in rows)
        {
            if (!(r.Moer.HasValue && r.Cost.HasValue && r.Lat.HasValue))
            {
                var missing = new List<string>();
                if (!r.Moer.HasValue) missing.Add("carbon");
                if (!r.Cost.HasValue) missing.Add("cost");
                if (!r.Lat.HasValue) missing.Add("latency");
                scored.Add(new RegionCandidateScore(r.Cloud, r.Region, r.Moer, r.Cost, r.Lat,
                    null, null, null, null, true, $"missing signal(s): {string.Join(",", missing)}"));
                continue;
            }

            var nMoer = Norm(r.Moer!.Value, moerMin, moerMax);
            var nCost = Norm(r.Cost!.Value, costMin, costMax);
            var nLat = Norm(r.Lat!.Value, latMin, latMax);
            var composite = weights.Carbon * nMoer + weights.Cost * nCost + weights.Latency * nLat;

            scored.Add(new RegionCandidateScore(r.Cloud, r.Region, r.Moer, r.Cost, r.Lat,
                nMoer, nCost, nLat, composite, false, null));
        }

        var best = scored.Where(s => !s.Excluded).OrderByDescending(s => s.CompositeScore!.Value).First();
        var singleObjectiveBest = complete.OrderBy(r => r.Moer!.Value).First();

        var regionsDiffer = !(string.Equals(best.Cloud, singleObjectiveBest.Cloud, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(best.Region, singleObjectiveBest.Region, StringComparison.OrdinalIgnoreCase));

        var rationale =
            $"multi-objective ({WeightProfileLabel(weights)}, w=[C:{weights.Carbon:F2} Co:{weights.Cost:F2} L:{weights.Latency:F2}]): " +
            $"selected {best.Cloud}:{best.Region} (score {best.CompositeScore:F3}; " +
            $"{best.MoerGPerKwh:F1} g/kWh, ${best.CostUsdPerHr:F3}/hr, {best.LatencyMs:F0} ms). " +
            $"Single-objective (carbon-only) pick was {singleObjectiveBest.Cloud}:{singleObjectiveBest.Region}" +
            (regionsDiffer ? " — DIFFERENT region." : " — same region.");

        await _audit.LogAdviceAsync(new AdviceRecord
        {
            Mode = $"multi_objective_{WeightProfileLabel(weights)}",
            TargetWhen = now,
            PreferredCloudsCsv = string.Join(",", job.GetEffectiveClouds()),
            SelectedCloud = best.Cloud,
            SelectedRegion = best.Region,
            SelectedWhen = now,
            SelectedMoerGPerKwh = best.MoerGPerKwh,
            Rationale = rationale,
            CreatedUtc = now,
            RequestId = correlation.Current
        }, Enumerable.Empty<AdviceCandidateRecord>(), ct);

        return new MultiObjectiveAdviceResult(
            best.Cloud, best.Region, now, rationale, weights, WeightProfileLabel(weights),
            scored, singleObjectiveBest.Cloud, singleObjectiveBest.Region, regionsDiffer);
    }

    private static string WeightProfileLabel(ObjectiveWeights w)
    {
        if (Math.Abs(w.Carbon - ObjectiveWeights.CarbonPrioritised.Carbon) < 0.01) return "carbon-prioritised";
        if (Math.Abs(w.Cost - ObjectiveWeights.CostPrioritised.Cost) < 0.01) return "cost-prioritised";
        return "balanced";
    }

    // WattTime returns lbs/MWh; the rest of the codebase converts to g/kWh this way (see WattTimeTwoModeEngine).
    private static double ConvertToGPerKwh(double lbsPerMwh) => lbsPerMwh * 0.45359237;
}
