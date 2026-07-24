using System;
using System.Collections.Concurrent;
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
///   - gcp   -> static seed table (GCP Cloud Billing Catalog API needs an API key)
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
    private static readonly TimeSpan AwsFetchTimeout = TimeSpan.FromSeconds(8);

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
                _ => null // gcp has no live path yet — falls straight through to static table below
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

        // Prefer the Linux meter (Azure lists a separate, pricier meter for Windows on the same SKU/region)
        var best = items.EnumerateArray()
            .Where(i => !(i.TryGetProperty("productName", out var pn) && pn.GetString()?.Contains("Windows") == true))
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
    // Static fallback table — sole source for GCP, fallback for AWS/Azure if the live call fails
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
