// One-time utility: pulls REAL current prices from the three cloud providers' own pricing
// APIs for every region configured in CarbonAware.Api/appsettings.json (LatencySignal:EndpointByRegion
// — reused here purely as "the list of regions you actually care about", same source of truth as
// CheckLatencyEndpoints and TestAllRegions use), and writes a ready-to-paste "StaticFallbackUsdPerHr"
// JSON block.
//
// Why this exists: the sandbox used to draft the app-level code can't reach prices.azure.com /
// pricing.*.amazonaws.com / cloudbilling.googleapis.com, so instead of guessing region prices,
// run this FROM YOUR OWN MACHINE (which has normal internet access) and it will fetch the actual
// numbers.
//
// Usage:
//   cd tools/SeedCostTable
//   dotnet run                                          -> AWS + Azure only (no GCP key needed)
//   dotnet run -- --gcp-key YOUR_API_KEY                -> AWS + Azure + GCP
//   dotnet run -- --settings /path/to/appsettings.json  -> custom path (default: ../../CarbonAware.Api/appsettings.json)
//
// Output: seeded-static-fallback.json in this folder. Paste its contents into
// CarbonAware.Api/appsettings.json under CostSignal:StaticFallbackUsdPerHr.
//
// Takes a few minutes for AWS if you have many regions configured (a multi-MB price file each) —
// that's exactly why CostSignalProvider treats AWS's live call as "attempt with a timeout, fall
// back to this table" rather than something to hit on every scheduling cycle.

using System.Text;
using System.Text.Json;

var gcpKey = GetArg(args, "--gcp-key");
var settingsPath = GetArg(args, "--settings") ?? "../../CarbonAware.Api/appsettings.json";
var only = GetArg(args, "--only")?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
           .Select(c => c.ToLowerInvariant()).ToHashSet();

if (!File.Exists(settingsPath))
{
    Console.WriteLine($"Could not find {settingsPath}. Pass --settings <path-to-appsettings.json>.");
    return;
}

using var settingsDoc = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath));
var endpointByRegion = settingsDoc.RootElement.GetProperty("LatencySignal").GetProperty("EndpointByRegion");

string[] RegionsFor(string cloud) =>
    (only is null || only.Contains(cloud)) && endpointByRegion.TryGetProperty(cloud, out var el)
        ? el.EnumerateObject().Select(p => p.Name).OrderBy(r => r).ToArray()
        : Array.Empty<string>();

var awsRegions = RegionsFor("aws");
var azureRegions = RegionsFor("azure");
var gcpRegions = RegionsFor("gcp");

Console.WriteLine($"Loaded {awsRegions.Length} AWS, {azureRegions.Length} Azure, {gcpRegions.Length} GCP regions from {settingsPath}" +
                   (only is not null ? $" (filtered to: {string.Join(",", only)})" : "") + "\n");

const string awsInstanceType = "t3.medium";
const string azureSkuName = "Standard_D2alds_v6"; // was Standard_B2s — updated per the finalized experiment manifest
const string gcpMachineType = "e2-medium";
const int gcpVCpu = 2;
const int gcpRamGb = 4;

using var http = new HttpClient();
http.Timeout = TimeSpan.FromSeconds(30);

var result = new Dictionary<string, Dictionary<string, double>>
{
    ["aws"] = new(),
    ["azure"] = new(),
    ["gcp"] = new()
};

Console.WriteLine($"=== Azure ({azureRegions.Length} regions) ===");
foreach (var region in azureRegions)
{
    var price = await GetAzurePriceAsync(http, region, azureSkuName);
    if (price is not null)
    {
        result["azure"][region] = Math.Round(price.Value, 4);
        Console.WriteLine($"  {region,-22} ${price:F4}/hr");
    }
    else
    {
        Console.WriteLine($"  {region,-22} (no price found — skipped)");
    }
}

Console.WriteLine();
Console.WriteLine($"=== AWS ({awsRegions.Length} regions — this is the slow part) ===");
foreach (var region in awsRegions)
{
    var price = await GetAwsPriceAsync(http, region, awsInstanceType);
    if (price is not null)
    {
        result["aws"][region] = Math.Round(price.Value, 4);
        Console.WriteLine($"  {region,-22} ${price:F4}/hr");
    }
    else
    {
        Console.WriteLine($"  {region,-22} (no price found — skipped)");
    }
}

if (!string.IsNullOrWhiteSpace(gcpKey))
{
    Console.WriteLine();
    Console.WriteLine($"=== GCP ({gcpRegions.Length} regions) ===");
    var skus = await GetGcpComputeSkusAsync(http, gcpKey);
    Console.WriteLine($"  fetched {skus.Count} Compute Engine on-demand SKUs from the catalog");

    foreach (var region in gcpRegions)
    {
        var price = PriceGcpFromCatalog(skus, region, gcpMachineType, gcpVCpu, gcpRamGb);
        if (price is not null)
        {
            result["gcp"][region] = Math.Round(price.Value, 4);
            Console.WriteLine($"  {region,-22} ${price:F4}/hr");
        }
        else
        {
            Console.WriteLine($"  {region,-22} (no matching Core/Ram SKU — skipped)");
        }
    }
}
else
{
    Console.WriteLine();
    Console.WriteLine("=== GCP skipped (no --gcp-key passed) ===");
}

var options = new JsonSerializerOptions { WriteIndented = true };
var outJson = JsonSerializer.Serialize(new { StaticFallbackUsdPerHr = result }, options);
await File.WriteAllTextAsync("seeded-static-fallback.json", outJson);

Console.WriteLine();
Console.WriteLine($"Done. {result["aws"].Count} AWS, {result["azure"].Count} Azure, {result["gcp"].Count} GCP prices written to seeded-static-fallback.json");
Console.WriteLine("Paste the \"StaticFallbackUsdPerHr\" value into CarbonAware.Api/appsettings.json under CostSignal.");

// ---------------------------------------------------------------------

static string? GetArg(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i] == name) return args[i + 1];
    return null;
}

static async Task<double?> GetAzurePriceAsync(HttpClient http, string region, string skuName)
{
    try
    {
        var filter = $"armRegionName eq '{region}' and armSkuName eq '{skuName}' " +
                     "and priceType eq 'Consumption' and serviceName eq 'Virtual Machines'";
        var url = $"https://prices.azure.com/api/retail/prices?api-version=2023-01-01-preview&$filter={Uri.EscapeDataString(filter)}";

        using var resp = await http.GetAsync(url);
        if (!resp.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("Items", out var items) || items.GetArrayLength() == 0)
            return null;

        // Prefer the Linux, pay-as-you-go meter: exclude Windows (separate, pricier meter), and
        // exclude Spot/Low Priority (a much cheaper, non-guaranteed meter that was winning the
        // Min() below — e.g. italynorth/westus3 coming back at ~$0.01/hr instead of ~$0.05/hr).
        string[] exclude = { "windows", "spot", "low priority" };
        var best = items.EnumerateArray()
            .Where(i => !(i.TryGetProperty("productName", out var pn) &&
                          exclude.Any(x => pn.GetString()?.Contains(x, StringComparison.OrdinalIgnoreCase) == true)))
            .Where(i => !(i.TryGetProperty("meterName", out var mn) &&
                          exclude.Any(x => mn.GetString()?.Contains(x, StringComparison.OrdinalIgnoreCase) == true)))
            .Select(i => i.GetProperty("retailPrice").GetDouble())
            .DefaultIfEmpty(-1)
            .Min();

        return best < 0 ? null : best;
    }
    catch
    {
        return null;
    }
}

static async Task<double?> GetAwsPriceAsync(HttpClient http, string region, string instanceType)
{
    try
    {
        var url = $"https://pricing.us-east-1.amazonaws.com/offers/v1.0/aws/AmazonEC2/current/{region}/index.json";
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        if (!resp.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync());
        var products = doc.RootElement.GetProperty("products");

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
            foreach (var dim in term.Value.GetProperty("priceDimensions").EnumerateObject())
            {
                var priceStr = dim.Value.GetProperty("pricePerUnit").GetProperty("USD").GetString();
                if (double.TryParse(priceStr, out var price) && price > 0) return price;
            }
        }
        return null;
    }
    catch
    {
        return null;
    }
}

static bool TryGetString(JsonElement obj, string name, out string value)
{
    if (obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
    {
        value = el.GetString()!;
        return true;
    }
    value = string.Empty;
    return false;
}

static async Task<List<GcpSku>> GetGcpComputeSkusAsync(HttpClient http, string apiKey)
{
    const string computeEngineServiceId = "services/6F81-5844-456A";
    var result = new List<GcpSku>();
    string? pageToken = null;

    do
    {
        var url = $"https://cloudbilling.googleapis.com/v1/{computeEngineServiceId}/skus" +
                  $"?key={Uri.EscapeDataString(apiKey)}&pageSize=5000" +
                  (pageToken is null ? "" : $"&pageToken={Uri.EscapeDataString(pageToken)}");

        HttpResponseMessage? resp = null;
        Exception? lastError = null;

        // A single transient DNS blip shouldn't take down the whole run — retry a couple of times
        // before giving up on the catalog fetch entirely.
        for (var attempt = 1; attempt <= 3 && resp is null; attempt++)
        {
            try
            {
                resp = await http.GetAsync(url);
            }
            catch (HttpRequestException ex)
            {
                lastError = ex;
                Console.WriteLine($"  GCP catalog fetch attempt {attempt}/3 failed: {ex.Message}");
                if (attempt < 3) await Task.Delay(1500 * attempt);
            }
        }

        if (resp is null)
        {
            Console.WriteLine($"  GCP catalog fetch failed after 3 attempts: {lastError?.Message}");
            Console.WriteLine("  Skipping GCP entirely for this run — check that cloudbilling.googleapis.com is reachable" +
                               " (try 'nslookup cloudbilling.googleapis.com' or opening it in a browser).");
            return result; // empty — every region will just report "no matching Core/Ram SKU"
        }

        using var _ = resp;
        if (!resp.IsSuccessStatusCode)
        {
            Console.WriteLine($"  GCP catalog fetch failed: {resp.StatusCode}");
            break;
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
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

                if (!sku.TryGetProperty("pricingInfo", out var pricingInfoEl) || pricingInfoEl.GetArrayLength() == 0) continue;
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

                result.Add(new GcpSku(description, regions, price));
            }
        }

        pageToken = doc.RootElement.TryGetProperty("nextPageToken", out var tokenEl) ? tokenEl.GetString() : null;
    }
    while (!string.IsNullOrEmpty(pageToken));

    return result;
}

static double? PriceGcpFromCatalog(List<GcpSku> skus, string region, string machineType, int vCpu, int ramGb)
{
    var family = machineType.Split('-')[0].ToUpperInvariant();
    string[] exclude = { "custom", "sole tenancy", "premium", "reserved", "commitment", "spot", "preemptible" };

    bool Matches(GcpSku s, string word) =>
        s.ServiceRegions.Any(r => string.Equals(r, region, StringComparison.OrdinalIgnoreCase)) &&
        s.Description.StartsWith(family, StringComparison.OrdinalIgnoreCase) &&
        s.Description.Contains(word, StringComparison.OrdinalIgnoreCase) &&
        !exclude.Any(x => s.Description.Contains(x, StringComparison.OrdinalIgnoreCase));

    var core = skus.FirstOrDefault(s => Matches(s, "Core"));
    var ram = skus.FirstOrDefault(s => Matches(s, "Ram"));
    if (core is null || ram is null) return null;

    var hourly = vCpu * core.UnitPriceUsd + ramGb * ram.UnitPriceUsd;
    return hourly > 0 ? hourly : null;
}

sealed record GcpSku(string Description, List<string> ServiceRegions, double UnitPriceUsd);