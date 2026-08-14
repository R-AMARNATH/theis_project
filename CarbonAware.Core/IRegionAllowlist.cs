namespace CarbonAware.Core;

/// <summary>
/// Hard gate on which (cloud, region) pairs the scheduler is allowed to consider at all —
/// independent of whether carbon/cost/latency signals happen to resolve for that region.
///
/// This exists because signal availability alone isn't a safe proxy for "usable": a region can
/// have working latency/cost/carbon signals and still be wrong to schedule to, e.g. because the
/// batch pipeline has no pre-staged storage bucket there (see CandidateResolver.ResolveWithZones,
/// which checks this before even attempting zone mapping). Anything outside the allowlist is
/// rejected up front with an explicit reason, rather than relying on some other signal failing
/// as an indirect, easy-to-accidentally-widen way of keeping candidates in scope.
/// </summary>
public interface IRegionAllowlist
{
    bool IsAllowed(string cloud, string region);
}
