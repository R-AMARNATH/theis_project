// Competitive selection test: unlike TestAllRegions (which sends 89 separate single-region
// requests to check per-region coverage), this sends ALL configured regions as candidates in
// ONE request — exactly what happens during a real scheduling cycle — and reports which single
// region actually won, its score, and how many candidates were excluded within that one pass.
//
// PREREQUISITE: the API must already be running (dotnet run from CarbonAware.Api) before you run
// this tool in a separate terminal.
//
// Usage:
//   cd tools/TestCompetitiveSelection
//   dotnet run                                            -> tests /advise + /advise-multi (all 3 weight profiles)
//   dotnet run -- --base-url http://localhost:5267        -> custom port
//   dotnet run -- --settings ../../CarbonAware.Api/appsettings.json
//
// A single request with ~89 candidates makes the API fan out ~89 concurrent carbon+cost+latency
// lookups internally — expect this to take a bit longer than a single normal call, but it's still
// just 4 total HTTP requests from this tool (1x /advise + 3x /advise-multi), not 89.

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

var allClouds = new List<string>();
var preferredLocations = new List<object>();
foreach (var cloudProp in endpointByRegion.EnumerateObject())
{
    allClouds.Add(cloudProp.Name);
    foreach (var regionProp in cloudProp.Value.EnumerateObject())
        preferredLocations.Add(new { cloud = cloudProp.Name, region = regionProp.Name });
}

Console.WriteLine($"Sending all {preferredLocations.Count} regions as candidates in a single request to {baseUrl} ...\n");

using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(3) };

var scheduleFrom = DateTimeOffset.UtcNow;
var scheduleUntil = scheduleFrom.AddHours(1);

// ---------------- /advise (single-objective, carbon-only) ----------------
{
    var body = new
    {
        job = new { clouds = allClouds },
        policy = new
        {
            mode = "run_now",
            scheduleFrom,
            scheduleUntil,
            preferredLocations
        }
    };

    Console.WriteLine("########## /advise (single-objective, all 89 candidates) ##########");
    try
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var resp = await http.PostAsync("/advise",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
        sw.Stop();

        var respBody = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            Console.WriteLine($"FAILED: HTTP {(int)resp.StatusCode} in {sw.ElapsedMilliseconds}ms");
            Console.WriteLine(respBody.Length < 500 ? respBody : respBody[..500] + "...");
        }
        else
        {
            using var doc = JsonDocument.Parse(respBody);
            var root = doc.RootElement;
            string Get(string name) => root.TryGetProperty(name, out var el) && el.ValueKind != JsonValueKind.Null ? el.ToString() : "-";

            Console.WriteLine($"Responded in {sw.ElapsedMilliseconds}ms");
            Console.WriteLine($"WINNER: {Get("cloud")}:{Get("region")}");
            Console.WriteLine($"  estimatedIntensityGPerKwh: {Get("estimatedIntensityGPerKwh")}");
            Console.WriteLine($"  highestEmission:           {Get("highestEmissionCloud")}:{Get("highestEmissionRegion")} ({Get("highestEmissionGPerKwh")} g/kWh)");
            Console.WriteLine($"  averageEstimatedSavingPct: {Get("averageEstimatedSavingPercent")}");
            Console.WriteLine($"  rationale: {Get("rationale")}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAILED: {ex.Message}");
    }
    Console.WriteLine();
}

// ---------------- /advise-multi (all 3 weight profiles) ----------------
foreach (var profile in new[] { "carbon", "balanced", "cost" })
{
    var body = new
    {
        job = new { clouds = allClouds },
        policy = new
        {
            mode = "run_now",
            scheduleFrom,
            scheduleUntil,
            preferredLocations
        },
        weightProfile = profile
    };

    Console.WriteLine($"########## /advise-multi [{profile}] (all 89 candidates) ##########");
    try
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var resp = await http.PostAsync("/advise-multi",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
        sw.Stop();

        var respBody = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            Console.WriteLine($"FAILED: HTTP {(int)resp.StatusCode} in {sw.ElapsedMilliseconds}ms");
            Console.WriteLine(respBody.Length < 500 ? respBody : respBody[..500] + "...");
            Console.WriteLine();
            continue;
        }

        using var doc = JsonDocument.Parse(respBody);
        var root = doc.RootElement;
        string Get(string name) => root.TryGetProperty(name, out var el) && el.ValueKind != JsonValueKind.Null ? el.ToString() : "-";

        var candidates = root.GetProperty("candidates");
        var total = candidates.GetArrayLength();
        var excludedCount = 0;
        var scoredList = new List<(string Cloud, string Region, double Score)>();

        foreach (var c in candidates.EnumerateArray())
        {
            var excluded = c.GetProperty("excluded").GetBoolean();
            if (excluded) { excludedCount++; continue; }
            var cloud = c.GetProperty("cloud").GetString()!;
            var region = c.GetProperty("region").GetString()!;
            var score = c.TryGetProperty("compositeScore", out var s) && s.ValueKind != JsonValueKind.Null ? s.GetDouble() : 0;
            scoredList.Add((cloud, region, score));
        }

        Console.WriteLine($"Responded in {sw.ElapsedMilliseconds}ms");
        Console.WriteLine($"WINNER: {Get("cloud")}:{Get("region")}");
        Console.WriteLine($"  {total - excludedCount}/{total} candidates scored, {excludedCount} excluded within this single pass");
        Console.WriteLine($"  singleObjectivePick: {Get("singleObjectiveCloud")}:{Get("singleObjectiveRegion")}  (regionsDiffer: {Get("regionsDiffer")})");
        Console.WriteLine($"  rationale: {Get("rationale")}");

        Console.WriteLine("  Top 5 ranked candidates:");
        foreach (var (cloud, region, score) in scoredList.OrderByDescending(x => x.Score).Take(5))
            Console.WriteLine($"    {cloud}:{region,-24} score={score:F4}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAILED: {ex.Message}");
    }
    Console.WriteLine();
}

static string? GetArg(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i] == name) return args[i + 1];
    return null;
}
