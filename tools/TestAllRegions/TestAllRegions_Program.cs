// Full-pipeline region test: unlike CheckLatencyEndpoints (latency only) and SeedCostTable (cost
// only), this calls your ACTUAL RUNNING API's /advise AND /advise-multi endpoints, once per region
// each, and checks whether each engine actually accepts that region as a usable candidate — i.e.
// carbon (+ cost + latency for /advise-multi) all resolved for it, live or fallback.
//
// PREREQUISITE: the API must already be running (dotnet run from CarbonAware.Api) before you run
// this tool in a separate terminal.
//
// Usage:
//   cd tools/TestAllRegions
//   dotnet run                                            -> tests both /advise and /advise-multi
//   dotnet run -- --only advise                           -> single-objective only
//   dotnet run -- --only advise-multi                     -> multi-objective only
//   dotnet run -- --base-url http://localhost:5267        -> custom port
//   dotnet run -- --settings ../../CarbonAware.Api/appsettings.json
//
// This makes up to two real requests per region, each of which may call WattTime plus a live cost
// API — expect it to take a few minutes, and be aware WattTime may have its own rate limits if you
// run this repeatedly in a short window.
//
// Output: printed pass/fail per region per endpoint + region-test-results.json in this folder.

using System.Text;
using System.Text.Json;

var baseUrl = GetArg(args, "--base-url") ?? "http://localhost:5267";
var settingsPath = GetArg(args, "--settings") ?? "../../CarbonAware.Api/appsettings.json";
var only = GetArg(args, "--only"); // "advise", "advise-multi", or null = both
var testAdvise = only is null || only == "advise";
var testAdviseMulti = only is null || only == "advise-multi";

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

Console.WriteLine($"Testing {targets.Count} regions against {baseUrl} " +
                   $"({(testAdvise ? "/advise " : "")}{(testAdviseMulti ? "/advise-multi" : "")}) ...");
Console.WriteLine("(the API must already be running in another terminal)\n");

using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
var gate = new SemaphoreSlim(5); // modest concurrency — each request can hit WattTime + a live cost API

async Task<T> Throttled<T>(Func<Task<T>> work)
{
    await gate.WaitAsync();
    try { return await work(); }
    finally { gate.Release(); }
}

void PrintAndSave<T>(string label, List<T> results, Func<T, string> cloud, Func<T, string> region,
    Func<T, bool> usable, Func<T, string> detail, string filename)
{
    Console.WriteLine($"\n########## {label} ##########");
    foreach (var c in results.Select(cloud).Distinct().OrderBy(x => x))
    {
        var group = results.Where(r => cloud(r) == c).OrderBy(region).ToList();
        var ok = group.Count(usable);
        Console.WriteLine($"=== {c} ({ok}/{group.Count} usable) ===");
        foreach (var r in group)
            Console.WriteLine($"  {region(r),-24} {(usable(r) ? "OK    " : "EXCL  ")}{detail(r)}");
        Console.WriteLine();
    }

    var totalOk = results.Count(usable);
    Console.WriteLine($"{label} TOTAL: {totalOk}/{results.Count} usable, {results.Count - totalOk} excluded.");

    var excluded = results.Where(r => !usable(r)).ToList();
    if (excluded.Count > 0)
    {
        Console.WriteLine("Excluded (grouped by reason):");
        foreach (var g in excluded.GroupBy(detail))
        {
            Console.WriteLine($"  \"{g.Key}\" ({g.Count()}):");
            foreach (var r in g) Console.WriteLine($"    - {cloud(r)}:{region(r)}");
        }
    }

    File.WriteAllText(filename, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Full results written to {filename}");
}

if (testAdviseMulti)
{
    var multiResults = (await Task.WhenAll(targets.Select(t => Throttled(() => TestOneMultiAsync(http, t.Cloud, t.Region))))).ToList();
    PrintAndSave("/advise-multi", multiResults, r => r.Cloud, r => r.Region, r => r.Usable,
        r => r.Usable ? $"moer={r.Moer:F0} cost={r.Cost:F4}[{(r.CostLive == true ? "live" : "static")}] lat={r.Latency:F0}ms" : r.Reason ?? "unknown",
        "region-test-results.json");
}

if (testAdvise)
{
    var adviseResults = (await Task.WhenAll(targets.Select(t => Throttled(() => TestOneAdviseAsync(http, t.Cloud, t.Region))))).ToList();
    PrintAndSave("/advise", adviseResults, r => r.Cloud, r => r.Region, r => r.Usable,
        r => r.Usable ? $"moer={r.Moer:F0}g/kWh {r.Rationale}" : r.Reason ?? "unknown",
        "region-test-results-advise.json");
}

static string? GetArg(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i] == name) return args[i + 1];
    return null;
}

static async Task<RegionResult> TestOneMultiAsync(HttpClient http, string cloud, string region)
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

// /advise (single-objective) has no "candidates" array or exclusion flag in its response shape —
// it either returns 200 with the single winning AdviceResult, or a non-2xx status if the one
// candidate we asked about couldn't be resolved (no zone mapping / not allowed / no carbon signal).
static async Task<AdviseResult> TestOneAdviseAsync(HttpClient http, string cloud, string region)
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
        }
    };

    try
    {
        using var resp = await http.PostAsync("/advise",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));

        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync();
            var reason = errBody.Length > 0 && errBody.Length < 200 ? errBody : $"HTTP {(int)resp.StatusCode}";
            return new AdviseResult(cloud, region, false, reason, null, null);
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        double? moer = root.TryGetProperty("estimatedIntensityGPerKwh", out var m) && m.ValueKind != JsonValueKind.Null
            ? m.GetDouble() : null;
        string? rationale = root.TryGetProperty("rationale", out var r) ? r.GetString() : null;

        return new AdviseResult(cloud, region, true, null, moer, rationale);
    }
    catch (Exception ex)
    {
        return new AdviseResult(cloud, region, false, $"request failed: {ex.Message}", null, null);
    }
}

sealed record RegionResult(string Cloud, string Region, bool Usable, string? Reason,
    double? Moer, double? Cost, double? Latency, bool? CostLive);

sealed record AdviseResult(string Cloud, string Region, bool Usable, string? Reason,
    double? Moer, string? Rationale);