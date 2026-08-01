// One-time connectivity check: HEAD-requests every endpoint configured under
// LatencySignal:EndpointByRegion in appsettings.json and reports which ones actually respond.
//
// Uses the exact same reachability rule as CarbonAware.Providers.LatencySignalProvider:
// any HTTP status code counts as reachable (timing the round trip, not checking for 200);
// only a timeout or connection-level failure (DNS, refused connection, TLS handshake) counts
// as unreachable. Run this FROM YOUR OWN MACHINE — this exercises real network calls, which
// the sandbox used to write this code can't do.
//
// Usage:
//   cd tools/CheckLatencyEndpoints
//   dotnet run                                          -> reads ../../CarbonAware.Api/appsettings.json
//   dotnet run -- --settings /path/to/appsettings.json   -> custom path
//
// Output: printed pass/fail table + latency-endpoint-check-results.json in this folder.

using System.Text.Json;

var settingsPath = GetArg(args, "--settings") ?? "../../CarbonAware.Api/appsettings.json";
var timeoutMs = 5000; // generous one-off check; the live provider uses LatencySignal:TimeoutMs (default 3000) per scheduling cycle

if (!File.Exists(settingsPath))
{
    Console.WriteLine($"Could not find {settingsPath}. Pass --settings <path-to-appsettings.json>.");
    return;
}

using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath));
var endpointByRegion = doc.RootElement.GetProperty("LatencySignal").GetProperty("EndpointByRegion");

var targets = new List<(string Cloud, string Region, string Endpoint)>();
foreach (var cloudProp in endpointByRegion.EnumerateObject())
{
    foreach (var regionProp in cloudProp.Value.EnumerateObject())
    {
        targets.Add((cloudProp.Name, regionProp.Name, regionProp.Value.GetString()!));
    }
}

Console.WriteLine($"Checking {targets.Count} endpoints ({timeoutMs} ms timeout each, 20 at a time)...\n");

using var http = new HttpClient();
var results = new List<CheckResult>();
var gate = new SemaphoreSlim(20);

var tasks = targets.Select(async t =>
{
    await gate.WaitAsync();
    try
    {
        var (reachable, detail, ms) = await CheckOneAsync(http, t.Endpoint, timeoutMs);
        return new CheckResult(t.Cloud, t.Region, t.Endpoint, reachable, detail, ms);
    }
    finally
    {
        gate.Release();
    }
});

results.AddRange(await Task.WhenAll(tasks));

foreach (var cloud in results.Select(r => r.Cloud).Distinct().OrderBy(c => c))
{
    var group = results.Where(r => r.Cloud == cloud).OrderBy(r => r.Region).ToList();
    var ok = group.Count(r => r.Reachable);
    Console.WriteLine($"=== {cloud} ({ok}/{group.Count} reachable) ===");
    foreach (var r in group)
    {
        var status = r.Reachable ? $"OK  {r.RoundTripMs:F0} ms" : $"FAIL  {r.Detail}";
        Console.WriteLine($"  {r.Region,-24} {status}");
    }
    Console.WriteLine();
}

var totalOk = results.Count(r => r.Reachable);
Console.WriteLine($"TOTAL: {totalOk}/{results.Count} reachable, {results.Count - totalOk} failed.");

var failedList = results.Where(r => !r.Reachable).Select(r => $"{r.Cloud}:{r.Region} ({r.Detail})").ToList();
if (failedList.Count > 0)
{
    Console.WriteLine("\nFailed:");
    foreach (var f in failedList) Console.WriteLine($"  - {f}");
}

var reportJson = JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true });
await File.WriteAllTextAsync("latency-endpoint-check-results.json", reportJson);
Console.WriteLine("\nFull results written to latency-endpoint-check-results.json");

static string? GetArg(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i] == name) return args[i + 1];
    return null;
}

static async Task<(bool Reachable, string Detail, double? Ms)> CheckOneAsync(HttpClient http, string endpoint, int timeoutMs)
{
    try
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var req = new HttpRequestMessage(HttpMethod.Head, endpoint);
        using var resp = await http.SendAsync(req, cts.Token);
        sw.Stop();
        return (true, $"status {(int)resp.StatusCode}", sw.Elapsed.TotalMilliseconds);
    }
    catch (OperationCanceledException)
    {
        return (false, "timeout", null);
    }
    catch (HttpRequestException ex)
    {
        return (false, ex.Message, null);
    }
}

sealed record CheckResult(string Cloud, string Region, string Endpoint, bool Reachable, string Detail, double? RoundTripMs);
