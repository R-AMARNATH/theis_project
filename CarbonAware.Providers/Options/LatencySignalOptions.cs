using System.Collections.Generic;

namespace CarbonAware.Providers.Options;

/// <summary>
/// Bind to config section "LatencySignal".
/// AWS has a documented, reliable per-region convention (service.{region}.amazonaws.com) so
/// it's pre-filled for you below in appsettings. Azure/GCP don't have one universal public
/// per-region endpoint without a deployed resource — add entries here as you validate them
/// against each provider's own docs, rather than trusting a guessed hostname.
///
/// NOTE: nested by cloud -> region, NOT a single "cloud:region" key. A flat key containing a
/// colon collides with .NET configuration's own ":" hierarchy separator and silently fails to
/// bind (see https://github.com/dotnet/extensions/issues/782). Nest instead.
/// </summary>
public sealed class LatencySignalOptions
{
    public Dictionary<string, Dictionary<string, string>> EndpointByRegion { get; set; } = new()
    {
        ["aws"] = new()
        {
            ["eu-west-1"] = "https://ec2.eu-west-1.amazonaws.com",
            ["us-east-1"] = "https://ec2.us-east-1.amazonaws.com",
            ["eu-west-2"] = "https://ec2.eu-west-2.amazonaws.com",
            ["us-west-2"] = "https://ec2.us-west-2.amazonaws.com"
        }
    };

    public int TimeoutMs { get; set; } = 3000;
    public int SamplesPerRegion { get; set; } = 3; // measured latency = median of N samples

    public int MaxConcurrentChecks { get; set; } = 12;
}
