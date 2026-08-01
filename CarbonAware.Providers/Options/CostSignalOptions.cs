using System.Collections.Generic;

namespace CarbonAware.Providers.Options;

/// <summary>
/// Bind to config section "CostSignal". Standardised instance = 2 vCPU / 4GB RAM per the proposal.
/// </summary>
public sealed class CostSignalOptions
{
    // Instance type per cloud, e.g. aws: "t3.medium", azure: "Standard_B2s", gcp: "e2-medium"
    public Dictionary<string, string> InstanceTypeByCloud { get; set; } = new()
    {
        { "aws", "t3.medium" },
        { "azure", "Standard_B2s" },
        { "gcp", "e2-medium" }
    };

    public int CacheTtlHours { get; set; } = 24;

    // Google Cloud API key with the Cloud Billing API enabled (Console → APIs & Services → Credentials).
    // No IAM role or billing account link needed — the Catalog API's SKU list is public list-pricing data,
    // just gated behind a key. Leave blank to keep GCP on the static fallback table.
    public string? GcpApiKey { get; set; }

    // Compute Engine's fixed Cloud Billing Catalog service ID (documented constant, not project-specific).
    public string GcpComputeEngineServiceId { get; set; } = "services/6F81-5844-456A";

    // GCP prices vCPU and RAM as two separate SKUs rather than one per-instance-type price (unlike AWS/Azure),
    // so we need to know how many of each a given machine type has to reconstruct an hourly instance price.
    public Dictionary<string, GcpMachineSpec> GcpMachineSpecs { get; set; } = new()
    {
        ["e2-medium"] = new GcpMachineSpec { VCpu = 2, RamGb = 4 },
        ["e2-small"] = new GcpMachineSpec { VCpu = 2, RamGb = 2 },
        ["e2-micro"] = new GcpMachineSpec { VCpu = 2, RamGb = 1 },
        ["e2-standard-2"] = new GcpMachineSpec { VCpu = 2, RamGb = 8 }
    };

    // Static seed prices (USD/hr) used as: (a) the sole source for AWS and GCP (see CostSignalProvider
    // for why — the AWS live price file is tens of MB and unreliable to fetch on every scheduling
    // cycle; GCP's live API needs an API key), and (b) a fallback if Azure's live call fails.
    //
    // NOTE: nested by cloud -> region, NOT a single "cloud:region" key. A flat key containing a colon
    // collides with .NET configuration's own ":" hierarchy separator and silently fails to bind
    // (see https://github.com/dotnet/extensions/issues/782). Nest instead.
    public Dictionary<string, Dictionary<string, double>> StaticFallbackUsdPerHr { get; set; } = new()
    {
        ["aws"] = new()
        {
            ["eu-west-1"] = 0.0416,
            ["us-east-1"] = 0.0416,
            ["eu-west-2"] = 0.0446
        },
        ["gcp"] = new()
        {
            ["europe-west1"] = 0.034,
            ["europe-west2"] = 0.040,
            ["us-east1"] = 0.034,
            ["us-central1"] = 0.034
        }
    };
}

public sealed class GcpMachineSpec
{
    public int VCpu { get; set; }
    public double RamGb { get; set; }
}
