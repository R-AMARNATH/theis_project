using System;
using System.Collections.Generic;
using System.Linq;

namespace CarbonAware.Core;

/// <summary>
/// Resolves (cloud, region) candidates from a JobSpec/PolicySpec pair.
/// This is the same logic WattTimeTwoModeEngine.BuildCandidates already uses privately —
/// factored out here so MultiObjectiveScoringEngine can share it instead of duplicating it.
/// (Optional follow-up: have WattTimeTwoModeEngine call this too and delete its private copy.)
/// </summary>
public static class CandidateResolver
{
    public static IReadOnlyList<(string Cloud, string Region)> BuildCandidates(
        JobSpec job,
        PolicySpec policy)
    {
        var clouds = job.GetEffectiveClouds();
        if (clouds.Count == 0) clouds = new List<string> { "gcp" };

        if (policy.PreferredLocations is { Count: > 0 })
        {
            return policy.PreferredLocations
                .Select(l => (l.Cloud, l.Region))
                .Distinct(PairComparer.Instance)
                .ToList();
        }

        var regions = (policy.PreferredRegions is { Count: > 0 })
            ? policy.PreferredRegions
            : new List<string> { policy.FallbackRegion ?? "us-east1" };

        var list = new List<(string Cloud, string Region)>();
        foreach (var c in clouds)
            foreach (var r in regions)
                list.Add((c, r));

        return list.Distinct(PairComparer.Instance).ToList();
    }

    /// <summary>
    /// Resolves candidates and drops any pair the region mapper can't translate into a
    /// WattTime grid zone. Returns both the usable set and the ones that were dropped.
    /// </summary>
    public static (List<(string Cloud, string Region, string Zone)> Usable,
                   List<(string Cloud, string Region)> Invalid)
        ResolveWithZones(JobSpec job, PolicySpec policy, IRegionMapper mapper)
    {
        var raw = BuildCandidates(job, policy);
        var usable = new List<(string, string, string)>();
        var invalid = new List<(string, string)>();

        foreach (var (cloud, region) in raw)
        {
            var zone = mapper.GetGridZones(cloud, region);
            if (string.IsNullOrWhiteSpace(zone))
                invalid.Add((cloud, region));
            else
                usable.Add((cloud, region, zone));
        }

        return (usable, invalid);
    }

    private sealed class PairComparer : IEqualityComparer<(string Cloud, string Region)>
    {
        public static readonly PairComparer Instance = new();

        public bool Equals((string Cloud, string Region) x, (string Cloud, string Region) y) =>
            string.Equals(x.Cloud, y.Cloud, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Region, y.Region, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Cloud, string Region) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Cloud),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Region));
    }
}