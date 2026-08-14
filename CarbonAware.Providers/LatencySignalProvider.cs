using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CarbonAware.Core;
using CarbonAware.Providers.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CarbonAware.Providers;

/// <summary>
/// Measures HTTP round-trip time from wherever this API is deployed (your EC2 instance in
/// eu-west-1/Dublin — that IS the "Dublin-based reference server" from the proposal) to a
/// per-region endpoint. Endpoint per (cloud,region) comes from LatencySignalOptions.EndpointByRegion
/// in appsettings — fill these in against each provider's documented regional endpoints.
///
/// Any HTTP status code counts as "reachable" (we're timing the TCP+TLS+HTTP round trip, not
/// checking for a 200 — a 403 from a region-locked endpoint still proves the round trip happened).
/// </summary>
public sealed class LatencySignalProvider : ILatencySignalProvider
{
    private readonly HttpClient _http;
    private readonly LatencySignalOptions _opts;
    private readonly ILogger<LatencySignalProvider> _log;

    // Shared across all candidates in a single scoring pass (and across concurrent requests,
    // since this provider is registered once) — caps how many region checks run at once
    // regardless of how many candidates the caller fans out. See MaxConcurrentChecks doc
    // comment in LatencySignalOptions for why this exists.
    private static readonly SemaphoreSlim _gate = new(initialCount: 12, maxCount: 12);
    private static int _configuredMax = 12;

    public LatencySignalProvider(HttpClient http, IOptions<LatencySignalOptions> opts, ILogger<LatencySignalProvider> log)
    {
        _http = http;
        _opts = opts.Value;
        _log = log;

        // Semaphore capacity is fixed at construction of the static field above; if config
        // specifies a different value, adjust the static gate once (cheap, rare — options
        // don't change per-request).
        var wanted = Math.Max(1, _opts.MaxConcurrentChecks);
        if (wanted != _configuredMax)
        {
            lock (_gate)
            {
                if (wanted != _configuredMax)
                {
                    var diff = wanted - _configuredMax;
                    if (diff > 0) _gate.Release(diff);
                    // Note: SemaphoreSlim doesn't support shrinking capacity cleanly; in practice
                    // MaxConcurrentChecks is set once in config and doesn't change at runtime, so
                    // growing is the only path exercised. If you need dynamic shrink, rebuild the
                    // semaphore behind a lock instead.
                    _configuredMax = wanted;
                }
            }
        }
    }

    public async Task<LatencySignal> GetLatencyAsync(string cloud, string region, CancellationToken ct = default)
    {
        var cloudKey = cloud.ToLowerInvariant();
        var regionKey = region.ToLowerInvariant();
        var logKey = $"{cloud}:{region}";
        var now = DateTimeOffset.UtcNow;

        if (!_opts.EndpointByRegion.TryGetValue(cloudKey, out var byRegion)
            || !byRegion.TryGetValue(regionKey, out var endpoint)
            || string.IsNullOrWhiteSpace(endpoint))
        {
            _log.LogWarning(
                "No latency endpoint configured for {Key}. Add one under LatencySignal:EndpointByRegion:{Cloud}:{Region}.",
                logKey, cloud, region);
            return new LatencySignal(cloud, region, null, now, false, "unconfigured");
        }

        // Throttle: wait for a free slot before doing this region's samples. With 81 regions
        // all calling GetLatencyAsync concurrently, this keeps actual simultaneous outbound
        // connections capped at MaxConcurrentChecks instead of all 81 firing at once.
        await _gate.WaitAsync(ct);
        try
        {
            return await MeasureAsync(cloud, region, endpoint, logKey, now, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<LatencySignal> MeasureAsync(
        string cloud, string region, string endpoint, string logKey, DateTimeOffset now, CancellationToken ct)
    {
        var samples = new List<double>();
        for (int i = 0; i < Math.Max(1, _opts.SamplesPerRegion); i++)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(_opts.TimeoutMs);

                var sw = Stopwatch.StartNew();
                using var req = new HttpRequestMessage(HttpMethod.Head, endpoint);
                using var resp = await _http.SendAsync(req, cts.Token);
                sw.Stop();

                samples.Add(sw.Elapsed.TotalMilliseconds);
                _log.LogInformation(
                    "Latency sample {Sample}/{Total} for {Key} via {Endpoint}: {Ms:F0} ms (status {Status})",
                    i + 1, _opts.SamplesPerRegion, logKey, endpoint, sw.Elapsed.TotalMilliseconds, (int)resp.StatusCode);
            }
            catch (OperationCanceledException)
            {
                _log.LogWarning(
                    "Latency sample {Sample}/{Total} for {Key} via {Endpoint} TIMED OUT after {TimeoutMs} ms.",
                    i + 1, _opts.SamplesPerRegion, logKey, endpoint, _opts.TimeoutMs);
            }
            catch (HttpRequestException ex)
            {
                _log.LogWarning(ex,
                    "Latency sample {Sample}/{Total} for {Key} via {Endpoint} failed: {Message}",
                    i + 1, _opts.SamplesPerRegion, logKey, endpoint, ex.Message);
            }
        }

        if (samples.Count == 0)
        {
            _log.LogWarning(
                "All {Total} latency samples failed for {Key} via {Endpoint} — check the warnings above for the cause (timeout vs. connection error).",
                _opts.SamplesPerRegion, logKey, endpoint);
            return new LatencySignal(cloud, region, null, now, false, endpoint);
        }

        samples.Sort();
        var median = samples[samples.Count / 2];
        return new LatencySignal(cloud, region, median, now, true, endpoint);
    }
}