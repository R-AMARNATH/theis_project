// Real scheduling comparison: calls /schedule (single-objective, carbon-aware) and
// /schedule-multi[balanced] (multi-objective) with ALL configured regions as candidates in one
// request each — same pattern as TestCompetitiveSelection, but these two endpoints don't just
// score candidates, they call ICloudTarget.ScheduleAsync on the winner, which dispatches a REAL
// GitHub Actions workflow that provisions a REAL VM in a REAL cloud region and costs REAL money.
//
// SAFETY: by default this only PRINTS what it would send (dry run) and does NOT call the API.
// You must pass --confirm to actually trigger the two real deployments.
//
// PREREQUISITE: the API must already be running (dotnet run from CarbonAware.Api).
//
// Usage:
//   cd tools/TestSchedulingComparison
//   dotnet run                                            -> dry run (safe, no deployment)
//   dotnet run -- --confirm                               -> ACTUALLY deploys, once per endpoint
//   dotnet run -- --confirm --only schedule                -> single-objective only
//   dotnet run -- --confirm --only schedule-multi          -> multi-objective (balanced) only
//   dotnet run -- --confirm --cycle-id my-cycle-001        -> custom cycle id (else auto-generated)
//   dotnet run -- --base-url http://localhost:5267
//   dotnet run -- --settings ../../CarbonAware.Api/appsettings.json

using System.Text;
using System.Text.Json;

var baseUrl = GetArg(args, "--base-url") ?? "http://localhost:5267";
var settingsPath = GetArg(args, "--settings") ?? "../../CarbonAware.Api/appsettings.json";
var confirmed = args.Contains("--confirm");
var only = GetArg(args, "--only"); // "schedule", "schedule-multi", or null = both
var cycleId = GetArg(args, "--cycle-id") ?? $"cmp-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
var testSingle = only is null || only == "schedule";
var testMulti = only is null || only == "schedule-multi";

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

Console.WriteLine($"Target: {baseUrl}   Regions: {preferredLocations.Count}   CycleId: {cycleId}");
Console.WriteLine(testSingle ? "  will call: /schedule (single-objective)" : "");
Console.WriteLine(testMulti ? "  will call: /schedule-multi [balanced]" : "");
Console.WriteLine();

if (!confirmed)
{
    Console.WriteLine("################################################################");
    Console.WriteLine("# DRY RUN — no request will be sent, nothing will be deployed.  #");
    Console.WriteLine("# /schedule and /schedule-multi trigger REAL GitHub Actions      #");
    Console.WriteLine("# deployments to REAL cloud VMs that cost REAL money.            #");
    Console.WriteLine("# Re-run with --confirm once you're sure you want that to happen.#");
    Console.WriteLine("################################################################");
    Console.WriteLine();
    Console.WriteLine("Request body that WOULD be sent (same for both endpoints, minus weightProfile):");
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        job = new { clouds = allClouds },
        policy = new
        {
            mode = "run_now",
            scheduleFrom = DateTimeOffset.UtcNow,
            scheduleUntil = DateTimeOffset.UtcNow.AddHours(1),
            preferredLocations = preferredLocations.Take(3).Append((object)"... (+ the rest)")
        },
        cycleId
    }, new JsonSerializerOptions { WriteIndented = true }));
    return;
}

using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(3) };
var scheduleFrom = DateTimeOffset.UtcNow;
var scheduleUntil = scheduleFrom.AddHours(1);

// Fire both at once (not one-after-the-other) so they're evaluated against the same real-world
// moment — carbon intensity genuinely shifts minute-to-minute, so sequencing these would mean
// each scheduler sees slightly different grid conditions, weakening the comparison's fairness.
var tasks = new List<(string Label, Task<string> Work)>();

if (testSingle)
{
    var body = new
    {
        job = new { clouds = allClouds },
        policy = new { mode = "run_now", scheduleFrom, scheduleUntil, preferredLocations },
        cycleId = $"{cycleId}-single"
    };
    tasks.Add(("/schedule (single-objective, carbon-aware)", PostAndFormat(http, "/schedule", body)));
}

if (testMulti)
{
    var body = new
    {
        job = new { clouds = allClouds },
        policy = new { mode = "run_now", scheduleFrom, scheduleUntil, preferredLocations },
        weightProfile = "balanced",
        cycleId = $"{cycleId}-multi"
    };
    tasks.Add(("/schedule-multi [balanced]", PostAndFormat(http, "/schedule-multi", body)));
}

Console.WriteLine($"Firing {tasks.Count} request(s) concurrently at {DateTimeOffset.UtcNow:O} ...\n");
await Task.WhenAll(tasks.Select(t => t.Work));

foreach (var (label, work) in tasks)
{
    Console.WriteLine($"########## POST {label} (REAL deployment) ##########");
    Console.WriteLine(await work);
    Console.WriteLine();
}

static async Task<string> PostAndFormat(HttpClient http, string path, object body)
{
    var sb = new StringBuilder();
    try
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var resp = await http.PostAsync(path,
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
        sw.Stop();

        var respBody = await resp.Content.ReadAsStringAsync();
        sb.AppendLine($"HTTP {(int)resp.StatusCode} in {sw.ElapsedMilliseconds}ms");

        if (!resp.IsSuccessStatusCode)
        {
            sb.AppendLine(respBody.Length < 800 ? respBody : respBody[..800] + "...");
            return sb.ToString();
        }

        using var doc = JsonDocument.Parse(respBody);
        var root = doc.RootElement;
        var advice = root.GetProperty("advice");

        string cloud = advice.TryGetProperty("cloud", out var c) ? c.GetString() ?? "-" : "-";
        string region = advice.TryGetProperty("region", out var r) ? r.GetString() ?? "-" : "-";
        string scheduledId = root.TryGetProperty("scheduledId", out var s) ? s.ToString() : "-";
        string returnedCycleId = root.TryGetProperty("cycleId", out var cy) ? cy.GetString() ?? "-" : "-";

        sb.AppendLine($"DEPLOYED TO: {cloud}:{region}");
        sb.AppendLine($"  scheduledId: {scheduledId}");
        sb.AppendLine($"  cycleId: {returnedCycleId}");
        sb.AppendLine("  >> Check your cloud provider's console / GitHub Actions run history to confirm the VM actually came up.");
    }
    catch (Exception ex)
    {
        sb.AppendLine($"FAILED: {ex.Message}");
    }
    return sb.ToString();
}

static string? GetArg(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i] == name) return args[i + 1];
    return null;
}
