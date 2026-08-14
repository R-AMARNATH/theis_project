using System;
using System.Collections.Generic;
using CarbonAware.Core;
using CarbonAware.Providers.Options;
using Microsoft.Extensions.Options;

namespace CarbonAware.Providers;

/// <summary>
/// IRegionAllowlist backed by LatencySignalOptions:EndpointByRegion — deliberately reuses that
/// config section rather than introducing a separate "AllowedRegions" list, since it's already
/// restricted to exactly the experiment's approved regions (see appsettings.json) and having a
/// second copy would just be one more place for the region set to drift out of sync.
/// </summary>
public sealed class ConfigRegionAllowlist : IRegionAllowlist
{
    private readonly Dictionary<string, HashSet<string>> _allowed;

    public ConfigRegionAllowlist(IOptions<LatencySignalOptions> opts)
    {
        _allowed = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (cloud, regions) in opts.Value.EndpointByRegion)
        {
            _allowed[cloud] = new HashSet<string>(regions.Keys, StringComparer.OrdinalIgnoreCase);
        }
    }

    public bool IsAllowed(string cloud, string region) =>
        _allowed.TryGetValue(cloud, out var regions) && regions.Contains(region);
}
