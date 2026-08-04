using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CarbonAware.Core;
using CarbonAware.Providers.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CarbonAware.Providers;

/// <summary>
/// ICostSignalProvider implementation:
///   - azure -> live call to the Azure Retail Prices API (prices.azure.com, keyless, small JSON response)
///   - aws   -> live call to the AWS Price List Bulk API region index, with a tight timeout;
///              falls back to the static table on any failure (timeout, connection reset, parse error).
///              The per-region file can be tens of MB, so this is attempted but never trusted alone.
///   - gcp   -> live call to the Cloud Billing Catalog API (needs CostSignal:GcpApiKey); falls back to
///              the static table if no key is configured, the SKU catalog fetch fails, or a matching
///              Core/Ram SKU can't be found for that machine type + region.
///
/// Results are cached in-memory per (cloud,region) for CostSignalOptions.CacheTtlHours so an
/// unattended multi-week experiment isn't dependent on a live API responding on every cycle —
/// each region is only actually fetched live once per cache window, not once per scheduling call.
/// </summary>
public sealed class CostSignalProvider : ICostSignalProvider
{
    private readonly HttpClient _http;
    private readonly CostSignalOptions _opts;
    private readonly ILogger<CostSignalProvider> _log;

    // Keep the AWS bulk-file fetch from hanging a whole scheduling cycle if it stalls mid-download.
    // Was 8s — the real /advise-multi test run showed this caused EVERY AWS region to fall back to
    // static (0% live), while the standalone SeedCostTable tool (30s HttpClient timeout, no per-call
    // cap) successfully fetched 36/39 regions live. 8s wasn't enough to download+parse AWS's per-region
    // price-list file (tens of MB for large regions like us-east-1/eu-west-1). 20s balances giving the
    // live call a real chance against not blocking a scheduling cycle too long if AWS is genuinely slow.
    private static readonly TimeSpan AwsFetchTimeout = TimeSpan.FromSeconds(20);

    private static readonly ConcurrentDictionary<string, (CostSignal Signal, DateTimeOffset ExpiresAt)> _cache = new();

    public CostSignalProvider(HttpClient http, IOptions<CostSignalOptions> opts, ILogger<CostSignalProvider> log)
    {
        _http = http;
        _opts = opts.Value;
        _log = log;
    }

    public async Task<CostSignal?> GetCostAsync(string cloud, string region, CancellationToken ct = default)
    {
        var cacheKey = $"{cloud}:{region}".ToLowerInvariant();

        if (_cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            return cached.Signal;

        var instanceType = _opts.InstanceTypeByCloud.TryGetValue(cloud.ToLowerInvariant(), out var it)
            ? it : "unknown";

        CostSignal? signal = null;
        try
        {
            signal = cloud.ToLowerInvariant() switch
            {
                "azure" => await GetAzureCostAsync(region, instanceType, ct),
                "aws" => await GetAwsCostAsync(region, instanceType, ct),
                "gcp" => await GetGcpCostAsync(region, instanceType, ct),
                _ => null
            };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Live cost lookup failed for {Cloud}:{Region}; falling back to static table.", cloud, region);
        }

        // Fall back to the static table if the live call failed, timed out, or returned nothing
        signal ??= GetStaticCost(cloud, region, instanceType);

        if (signal is not null)
            _cache[cacheKey] = (signal, DateTimeOffset.UtcNow.AddHours(Math.Max(1, _opts.CacheTtlHours)));

        return signal;
    }

    // ------------------------------------------------------------------
    // Azure Retail Prices API — https://prices.azure.com/api/retail/prices
    // ------------------------------------------------------------------
    private async Task<CostSignal?> GetAzureCostAsync(string region, string skuName, CancellationToken ct)
    {
        var filter = $"armRegionName eq '{region}' and armSkuName eq '{skuName}' " +
                     "and priceType eq 'Consumption' and serviceName eq 'Virtual Machines'";
        var url = $"https://prices.azure.com/api/retail/prices?api-version=2023-01-01-preview&$filter={Uri.EscapeDataString(filter)}";

        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (!doc.RootElement.TryGetProperty("Items", out var items) || items.GetArrayLength() == 0)
            return null;

        // Prefer the Linux, pay-as-you-go meter: exclude Windows (separate, pricier meter for the
        // same SKU/region), and exclude Spot/Low Priority (a different, much cheaper, non-guaranteed
        // meter that was slipping through the old Windows-only filter and winning the Min() below —
        // e.g. italynorth/westus3 returning ~$0.01/hr instead of the real ~$0.05/hr on-demand rate).
        string[] exclude = { "windows", "spot", "low priority" };
        var best = items.EnumerateArray()
            .Where(i => !(i.TryGetProperty("productName", out var pn) &&
                          exclude.Any(x => pn.GetString()?.Contains(x, StringComparison.OrdinalIgnoreCase) == true)))
            .Where(i => !(i.TryGetProperty("meterName", out var mn) &&
                          exclude.Any(x => mn.GetString()?.Contains(x, StringComparison.OrdinalIgnoreCase) == true)))
            .Select(i => i.GetProperty("retailPrice").GetDouble())
            .DefaultIfEmpty(-1)
            .Min();

        if (best < 0) return null;

        return new CostSignal("azure", region, skuName, best, DateTimeOffset.UtcNow, "prices.azure.com/api/retail/prices");
    }

    // ------------------------------------------------------------------
    // AWS Price List Bulk API — per-region EC2 offer file (can be tens of MB; tight timeout + fallback)
    // https://pricing.us-east-1.amazonaws.com/offers/v1.0/aws/AmazonEC2/current/{region}/index.json
    // ------------------------------------------------------------------
    private async Task<CostSignal?> GetAwsCostAsync(string region, string instanceType, CancellationToken ct)
    {
        var url = $"https://pricing.us-east-1.amazonaws.com/offers/v1.0/aws/AmazonEC2/current/{region}/index.json";

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(AwsFetchTimeout);

        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
        if (!resp.IsSuccessStatusCode) return null;

        await using var stream = await resp.Content.ReadAsStreamAsync(timeoutCts.Token);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: timeoutCts.Token);

        var products = doc.RootElement.GetProperty("products");

        // Find the on-demand, shared-tenancy, Linux, no-pre-installed-software SKU for this instance type
        string? matchedSku = null;
        foreach (var prop in products.EnumerateObject())
        {
            var attrs = prop.Value.GetProperty("attributes");
            if (!TryGetString(attrs, "instanceType", out var t) || t != instanceType) continue;
            if (!TryGetString(attrs, "operatingSystem", out var os) || os != "Linux") continue;
            if (!TryGetString(attrs, "tenancy", out var tenancy) || tenancy != "Shared") continue;
            if (TryGetString(attrs, "preInstalledSw", out var sw) && sw != "NA") continue;
            if (TryGetString(attrs, "capacitystatus", out var cap) && cap != "Used") continue;

            matchedSku = prop.Name;
            break;
        }

        if (matchedSku is null) return null;

        var onDemand = doc.RootElement.GetProperty("terms").GetProperty("OnDemand");
        if (!onDemand.TryGetProperty(matchedSku, out var termsForSku)) return null;

        foreach (var term in termsForSku.EnumerateObject())
        {
            var dims = term.Value.GetProperty("priceDimensions");
            foreach (var dim in dims.EnumerateObject())
            {
                var priceStr = dim.Value.GetProperty("pricePerUnit").GetProperty("USD").GetString();
                if (double.TryParse(priceStr, out var price) && price > 0)
                    return new CostSignal("aws", region, instanceType, price, DateTimeOffset.UtcNow,
                        "pricing.us-east-1.amazonaws.com (Price List Bulk API)");
            }
        }

        return null;
    }

    // ------------------------------------------------------------------
    // GCP Cloud Billing Catalog API — https://cloudbilling.googleapis.com/v1/services/{computeEngine}/skus
    // Needs only an API key (no IAM role, no billing account link) — it's public list pricing.
    // Unlike AWS/Azure, GCP has no single "price this instance type" SKU: vCPU and RAM are billed as
    // two separate line items, so we sum vcpu*coreHourlyRate + ramGb*ramHourlyRate using GcpMachineSpecs.
    // The full Compute Engine SKU catalog (thousands of rows, all regions/families) is fetched once and
    // cached for CacheTtlHours — re-fetching it on every scheduling cycle would be wasteful and slow.
    // ------------------------------------------------------------------
    private static readonly TimeSpan GcpFetchTimeout = TimeSpan.FromSeconds(15);

    private sealed record GcpSkuLite(string Description, IReadOnlyList<string> ServiceRegions, double UnitPriceUsd);

    private static readonly ConcurrentDictionary<string, (List<GcpSkuLite> Skus, DateTimeOffset ExpiresAt)> _gcpCatalogCache = new();

    private async Task<CostSignal?> GetGcpCostAsync(string region, string machineType, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_opts.GcpApiKey))
        {
            _log.LogWarning("GCP live pricing skipped: no CostSignal:GcpApiKey configured. Falling back to static table.");
            return null;
        }

        if (!_opts.GcpMachineSpecs.TryGetValue(machineType, out var spec))
        {
            _log.LogWarning(
                "No vCPU/RAM spec configured for GCP machine type {MachineType}. Add one under CostSignal:GcpMachineSpecs:{MachineType}.",
                machineType, machineType);
            return null;
        }

        var skus = await GetComputeSkuCatalogAsync(ct);

        // e.g. "e2-medium" -> "E2" — GCP SKU descriptions read "E2 Instance Core running in ...".
        var family = machineType.Split('-')[0].ToUpperInvariant();
        string[] exclude = { "custom", "sole tenancy", "premium", "reserved", "commitment", "spot", "preemptible" };

        bool Matches(GcpSkuLite s, string resourceWord) =>
            s.ServiceRegions.Any(r => string.Equals(r, region, StringComparison.OrdinalIgnoreCase)) &&
            s.Description.StartsWith(family, StringComparison.OrdinalIgnoreCase) &&
            s.Description.Contains(resourceWord, StringComparison.OrdinalIgnoreCase) &&
            !exclude.Any(x => s.Description.Contains(x, StringComparison.OrdinalIgnoreCase));

        var coreSku = skus.FirstOrDefault(s => Matches(s, "Core"));
        var ramSku = skus.FirstOrDefault(s => Matches(s, "Ram"));

        if (coreSku is null || ramSku is null)
        {
            _log.LogWarning(
                "Could not find both Core and Ram SKUs for {MachineType} in {Region} (core found: {Core}, ram found: {Ram}).",
                machineType, region, coreSku is not null, ramSku is not null);
            return null;
        }

        var hourly = spec.VCpu * coreSku.UnitPriceUsd + spec.RamGb * ramSku.UnitPriceUsd;
        if (hourly <= 0) return null;

        return new CostSignal("gcp", region, machineType, hourly, DateTimeOffset.UtcNow,
            "cloudbilling.googleapis.com/v1 (Cloud Billing Catalog API, Compute Engine SKUs)");
    }

    private async Task<List<GcpSkuLite>> GetComputeSkuCatalogAsync(CancellationToken ct)
    {
        const string cacheKey = "gcp-compute-skus";
        if (_gcpCatalogCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            return cached.Skus;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(GcpFetchTimeout);

        var result = new List<GcpSkuLite>();
        string? pageToken = null;

        do
        {
            var url = $"https://cloudbilling.googleapis.com/v1/{_opts.GcpComputeEngineServiceId}/skus" +
                      $"?key={Uri.EscapeDataString(_opts.GcpApiKey!)}&pageSize=5000" +
                      (pageToken is null ? "" : $"&pageToken={Uri.EscapeDataString(pageToken)}");

            using var resp = await _http.GetAsync(url, timeoutCts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("GCP Cloud Billing Catalog API returned {Status} while fetching SKUs.", resp.StatusCode);
                break;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(timeoutCts.Token);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: timeoutCts.Token);

            if (doc.RootElement.TryGetProperty("skus", out var skusEl))
            {
                foreach (var sku in skusEl.EnumerateArray())
                {
                    if (!sku.TryGetProperty("category", out var cat)) continue;
                    if (!TryGetString(cat, "resourceFamily", out var family) || family != "Compute") continue;
                    if (!TryGetString(cat, "usageType", out var usageType) || usageType != "OnDemand") continue;
                    if (!TryGetString(sku, "description", out var description)) continue;

                    var regions = sku.TryGetProperty("serviceRegions", out var regionsEl)
                        ? regionsEl.EnumerateArray().Select(r => r.GetString() ?? "").ToList()
                        : new List<string>();

                    if (!sku.TryGetProperty("pricingInfo", out var pricingInfoEl) || pricingInfoEl.GetArrayLength() == 0)
                        continue;

                    var pricingExpr = pricingInfoEl[0].GetProperty("pricingExpression");
                    var tieredRates = pricingExpr.GetProperty("tieredRates");
                    if (tieredRates.GetArrayLength() == 0) continue;

                    var unitPriceEl = tieredRates[0].GetProperty("unitPrice");
                    if (!TryGetString(unitPriceEl, "currencyCode", out var currency) || currency != "USD") continue;

                    var units = unitPriceEl.TryGetProperty("units", out var unitsEl)
                        ? (unitsEl.ValueKind == JsonValueKind.String ? double.Parse(unitsEl.GetString()!) : unitsEl.GetDouble())
                        : 0;
                    var nanos = unitPriceEl.TryGetProperty("nanos", out var nanosEl) ? nanosEl.GetDouble() : 0;
                    var price = units + nanos / 1_000_000_000.0;

                    result.Add(new GcpSkuLite(description, regions, price));
                }
            }

            pageToken = doc.RootElement.TryGetProperty("nextPageToken", out var tokenEl)
                ? tokenEl.GetString()
                : null;
        }
        while (!string.IsNullOrEmpty(pageToken));

        _gcpCatalogCache[cacheKey] = (result, DateTimeOffset.UtcNow.AddHours(Math.Max(1, _opts.CacheTtlHours)));
        return result;
    }

    private static bool TryGetString(JsonElement obj, string name, out string value)
    {
        if (obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
        {
            value = el.GetString()!;
            return true;
        }
        value = string.Empty;
        return false;
    }

    // ------------------------------------------------------------------
    // Static fallback table — used for GCP when no API key is set (or the live lookup fails),
    // and as a fallback for AWS/Azure if their live calls fail
    // ------------------------------------------------------------------
    private CostSignal? GetStaticCost(string cloud, string region, string instanceType)
    {
        var cloudKey = cloud.ToLowerInvariant();
        var regionKey = region.ToLowerInvariant();

        if (_opts.StaticFallbackUsdPerHr.TryGetValue(cloudKey, out var byRegion)
            && byRegion.TryGetValue(regionKey, out var price))
        {
            return new CostSignal(cloud, region, instanceType, price, DateTimeOffset.UtcNow,
                "static-fallback (appsettings CostSignal:StaticFallbackUsdPerHr)");
        }

        _log.LogWarning(
            "No static fallback price configured for {Cloud}:{Region}. Add one under CostSignal:StaticFallbackUsdPerHr:{Cloud}:{Region}.",
            cloud, region, cloud, region);
        return null;
    }
}