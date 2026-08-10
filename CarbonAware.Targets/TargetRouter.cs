using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CarbonAware.Core;
using Microsoft.Extensions.Logging;

namespace CarbonAware.Targets;

public sealed class TargetRouter : ICloudTarget
{
    private readonly ILogger<TargetRouter> _log;
    private readonly IDictionary<string, ICloudTarget> _byCloud;

    public TargetRouter(ILogger<TargetRouter> log, IDictionary<string, ICloudTarget> byCloud)
    {
        _log = log;
        _byCloud = new Dictionary<string, ICloudTarget>(byCloud, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<string> ScheduleAsync(AdviceResult advice, JobSpec job, CorrelationContext correlation, ExperimentContext experiment, CancellationToken ct = default)
    {
        if (!_byCloud.TryGetValue(advice.Cloud, out var target))
        {
            _log.LogWarning("No target registered for cloud {Cloud}. Returning stub id.", advice.Cloud);
            return $"no-target-{advice.Cloud}-{Guid.NewGuid()}";
        }
        return await target.ScheduleAsync(advice, job, correlation, experiment, ct);
    }
}
