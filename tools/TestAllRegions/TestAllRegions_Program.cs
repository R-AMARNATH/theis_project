// Full-pipeline region test: unlike CheckLatencyEndpoints (latency only) and SeedCostTable (cost
// only), this calls your ACTUAL RUNNING API's /advise-multi endpoint, once per region, and checks
// whether MultiObjectiveScoringEngine actually accepts that region as a usable candidate — i.e.
// carbon + cost + latency all resolved for it, live or fallback.
//
// PREREQUISITE: the API must already be running (dotnet run from CarbonAware.Api) before you run
// this tool in a separate terminal.
//
// Usage:
//   cd tools/TestAllRegions
//   dotnet run                                            -> assumes http://localhost:5267
//   dotnet run -- --base-url http://localhost:5267        -> custom port
//   dotnet run -- --settings ../../CarbonAware.Api/appsettings.json
//
// This makes one real request per region (136 by default), each of which may call WattTime plus a
// live cost API — expect it to take a few minutes, and be aware WattTime may have its own rate
// limits if you run this repeatedly in a short window.
//
// Output: printed pass/fail per region + region-test-results.json in this folder.

using System.Text;
using System.Text.Json;

var baseUrl = GetArg(args, "--base-url") ?? "http://localhost:5267";
var settingsPath = GetArg(args, "--settings") ?? "../../CarbonAware.Api/appsettings.json";

if (!File.Exists(settingsPath))
{
    Console.WriteLine($"Could not find {settingsPath}. Pass --settings <path-to-appsettings.json>.");
    return;
}

using var settingsDoc = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath));
var endpointByRegion = settingsDoc.RootElement.GetProperty("LatencySignal").GetProperty("EndpointByRegion");

var targets = new List<(string Cloud, string Region)>();
foreach (var cloudProp in endpointByRegion.EnumerateObject())
    foreach (var regionProp in cloudProp.Value.EnumerateObject())
        targets.Add((cloudProp.Name, regionProp.Name));

Console.WriteLine($"Testing {targets.Count} regions against {baseUrl}/advise-multi ...");
Console.WriteLine("(the API must already be running in another terminal)\n");

using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
var results = new List<RegionResult>();

// Modest concurrency — each request can hit WattTime + a live cost API; don't hammer either.
var gate = new SemaphoreSlim(5);
var tasks = targets.Select(async t =>
{
    await gate.WaitAsync();
    try { return await TestOneAsync(http, t.Cloud, t.Region); }
    finally { gate.Release(); }
});

results.AddRange(await Task.WhenAll(tasks));

foreach (var cloud in results.Select(r => r.Cloud).Distinct().OrderBy(c => c))
{
    var group = results.Where(r => r.Cloud == cloud).OrderBy(r => r.Region).ToList();
    var ok = group.Count(r => r.Usable);
    Console.WriteLine($"=== {cloud} ({ok}/{group.Count} usable) ===");
    foreach (var r in group)
    {
        var status = r.Usable
            ? $"OK    moer={r.Moer:F0} cost={r.Cost:F4}[{(r.CostLive == true ? "live" : "static")}] lat={r.Latency:F0}ms"
            : $"EXCL  {r.Reason}";
        Console.WriteLine($"  {r.Region,-24} {status}");
    }
    Console.WriteLine();
}

var totalOk = results.Count(r => r.Usable);
Console.WriteLine($"TOTAL: {totalOk}/{results.Count} regions usable, {results.Count - totalOk} excluded.");

var excluded = results.Where(r => !r.Usable).ToList();
if (excluded.Count > 0)
{
    Console.WriteLine("\nExcluded (grouped by reason):");
    foreach (var g in excluded.GroupBy(r => r.Reason))
    {
        Console.WriteLine($"  \"{g.Key}\" ({g.Count()}):");
        foreach (var r in g) Console.WriteLine($"    - {r.Cloud}:{r.Region}");
    }
}

await File.WriteAllTextAsync("region-test-results.json",
    JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("\nFull results written to region-test-results.json");

static string? GetArg(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i] == name) return args[i + 1];
    return null;
}

static async Task<RegionResult> TestOneAsync(HttpClient http, string cloud, string region)
{
    var body = new
    {
        job = new { clouds = new[] { cloud } },
        policy = new
        {
            mode = "run_now",
            scheduleFrom = DateTimeOffset.UtcNow,
            scheduleUntil = DateTimeOffset.UtcNow.AddHours(1),
            preferredLocations = new[] { new { cloud, region } }
        },
        weightProfile = "balanced"
    };

    try
    {
        using var resp = await http.PostAsync("/advise-multi",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));

        if (!resp.IsSuccessStatusCode)
            return new RegionResult(cloud, region, false, $"HTTP {(int)resp.StatusCode}", null, null, null, null);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
            return new RegionResult(cloud, region, false, "no candidates returned", null, null, null, null);

        var c = candidates[0]; // only one candidate requested, so it's the only one returned
        var excluded = c.GetProperty("excluded").GetBoolean();

        if (excluded)
        {
            var reason = c.TryGetProperty("exclusionReason", out var er) ? er.GetString() ?? "unknown" : "unknown";
            return new RegionResult(cloud, region, false, reason, null, null, null, null);
        }

        double? GetD(string name) => c.TryGetProperty(name, out var el) && el.ValueKind != JsonValueKind.Null ? el.GetDouble() : null;
        bool? costLive = c.TryGetProperty("costIsLive", out var cl) && cl.ValueKind != JsonValueKind.Null ? cl.GetBoolean() : null;

        return new RegionResult(cloud, region, true, null, GetD("moerGPerKwh"), GetD("costUsdPerHr"), GetD("latencyMs"), costLive);
    }
    catch (Exception ex)
    {
        return new RegionResult(cloud, region, false, $"request failed: {ex.Message}", null, null, null, null);
    }
}

sealed record RegionResult(string Cloud, string Region, bool Usable, string? Reason,
    double? Moer, double? Cost, double? Latency, bool? CostLive);
