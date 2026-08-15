using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CarbonAware.Core;
using CarbonAware.Providers.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using CarbonAware.Core.Auditing;

namespace CarbonAware.Providers;

public sealed class WattTimeProvider : ICarbonSignalProvider, IBestWindowSignalProvider, IBatchSignalProvider
{
    private readonly HttpClient _http;
    private readonly ILogger<WattTimeProvider> _log;
    private readonly WattTimeOptions _opts;

    private static  readonly object _tokenLock = new();
    private static  string? _bearerToken;
    private static  DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;
    private readonly IAuditSink _audit;
    private readonly ICorrelationContext _correlation;

    private static readonly SemaphoreSlim _gate = new(initialCount: 12, maxCount: 12);
    private static int _configuredMax = 12;
    public WattTimeProvider(HttpClient http, IOptions<WattTimeOptions> opts, ILogger<WattTimeProvider> log, IAuditSink audit, ICorrelationContext correlation)
    {
        _http = http;
        _opts = opts.Value;
        _log = log;
        _audit = audit;
        _correlation = correlation;
        // BaseAddress set in Program.cs
        var wanted = Math.Max(1, _opts.MaxConcurrentChecks);
        if (wanted != _configuredMax)
        {
            lock (_gate)
            {
                if (wanted != _configuredMax)
                {
                    var diff = wanted - _configuredMax;
                    if (diff > 0) _gate.Release(diff);
                    _configuredMax = wanted;
                }
            }
        }
    }

    // ======== PUBLIC API ========

    public async Task<CarbonSignal> GetSignalsAsync(
    string region,
    DateTimeOffset at,
    bool marginal,
    CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await GetSignalsCoreAsync(region, at, marginal, ct);
        }
        finally
        {
            _gate.Release();
        }
    }


    private async Task<CarbonSignal> GetSignalsCoreAsync(
    string region,
    DateTimeOffset at,
    bool marginal,
    CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var isFuture = at > now.AddMinutes(2); // small tolerance

        double? moer = isFuture
            ? await GetMoerAtAsync(region, at, ct)            // forecast near 'at'
            : await TryGetMoerForecastNowAsync(region, ct);   // earliest forecast point ~now

        if (moer.HasValue)
        {
            return new CarbonSignal(
               Zone: region,
               Timestamp: at, // or DateTimeOffset.UtcNow; either is fine for display
               IntensityGPerKwh: moer.Value,
               IsMarginal: true,
               ForecastHorizonMin: (int?)Math.Round((at - now).TotalMinutes),
               Source: isFuture ? "watttime.v3.forecast@at" : "watttime.v3.forecast@now"
           );
        }
        _log.LogDebug("WattTime: no usable signal for region {Region} at {At:o}", region, at);
        return null;
    }
    // ======== AUTH ========
    /// <summary>Ensures there is a valid Bearer token (login at /login, cache for TokenCacheMinutes).</summary>
    public async Task EnsureTokenAsync(CancellationToken ct = default)
    {
        if (_bearerToken is not null && DateTimeOffset.UtcNow < _tokenExpiresAt) return;

        lock (_tokenLock)
        {
            if (_bearerToken is not null && DateTimeOffset.UtcNow < _tokenExpiresAt) return;
        }

        var token = await LoginAsync(ct);
        var ttl = Math.Max(5, _opts.TokenCacheMinutes);

        lock (_tokenLock)
        {
            _bearerToken = token;
            _tokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(ttl);
        }

        _log.LogInformation("WattTime: token acquired; will refresh around {Exp:u}", _tokenExpiresAt);
    }

    /// <summary>Login using v3 docs flow: GET /login with Basic auth returns { token }.</summary>
    private async Task<string> LoginAsync(CancellationToken ct)
    {
        var baseUri = _http.BaseAddress ?? new Uri("https://api.watttime.org");
        var uri = new Uri(baseUri, "login");
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_opts.Username}:{_opts.Password}"));

        var sw = Stopwatch.StartNew();
        int status = 0;
        string? body = null;
        string? err = null;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

            using var resp = await _http.SendAsync(req, ct);
            status = (int)resp.StatusCode;
            resp.EnsureSuccessStatusCode();

            body = await resp.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String)
                return t.GetString()!;

            throw new InvalidOperationException("WattTime /login did not return a 'token'.");
        }
        catch (Exception ex)
        {
            err = ex.ToString();
            throw;
        }
        finally
        {
            sw.Stop();
            await _audit.LogWattTimeCallAsync(new WattTimeCallRecord
            {
                Method = "GET",
                RequestUrl = uri.ToString(),
                Region = null,
                SignalType = null,
                HorizonHours = null,
                StatusCode = status,
                Success = status >= 200 && status < 300,
                DurationMs = (int)sw.ElapsedMilliseconds,
                ResponseBody = Truncate(body, 100_000),
                Error = err,
                SourceFile = nameof(WattTimeProvider),
                SourceLine = 0,
                RequestId = _correlation.Current
            }, ct);
        }
    }


    // ======== DATA HELPERS ========
    private async Task<double?> TryGetMoerForecastNowAsync(string region, CancellationToken ct)
    {
        var uri = new Uri(_http.BaseAddress ?? new Uri("https://api.watttime.org"),
            $"/v3/forecast?region={Uri.EscapeDataString(region)}&signal_type=co2_moer&horizon_hours=1");

        var sw = Stopwatch.StartNew();
        int status = 0;
        string? body = null;
        string? err = null;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GetTokenUnsafe());

            using var resp = await _http.SendAsync(req, ct);
            status = (int)resp.StatusCode;

            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                _log.LogWarning("WattTime v3/forecast 401 for {Region}. Refreshing token…", region);
                await EnsureTokenAsync(ct);
                return await TryGetMoerForecastNowAsync(region, ct);
            }

            body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                // Often 403 if plan doesn’t include forecast
                _log.LogDebug("WattTime v3/forecast {Status} for {Region}. Body: {Body}", status, region, body);
                return null;
            }

            using var doc = JsonDocument.Parse(body);

            if (TryFirstArray(doc.RootElement, out var arr))
            {
                foreach (var p in arr.EnumerateArray())
                {
                    if (p.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number)
                        return v.GetDouble();
                }
            }

            var fallback = TryFindNumber(doc.RootElement, "value");
            if (fallback.has) return fallback.value;

            return null;
        }
        catch (Exception ex)
        {
            err = ex.ToString();
            throw;
        }
        finally
        {
            sw.Stop();
            await _audit.LogWattTimeCallAsync(new WattTimeCallRecord
            {
                Method = "GET",
                RequestUrl = uri.ToString(),
                Region = region,
                SignalType = "co2_moer",
                HorizonHours = null,
                StatusCode = status,
                Success = status >= 200 && status < 300,
                DurationMs = (int)sw.ElapsedMilliseconds,
                ResponseBody = Truncate(body, 100_000),
                Error = err,
                SourceFile = nameof(WattTimeProvider),
                SourceLine = 0,
                RequestId = _correlation.Current
            }, ct);
        }
    }


    private string GetTokenUnsafe()
    {
        lock (_tokenLock) return _bearerToken ?? string.Empty;
    }

    private static bool TryFirstArray(JsonElement root, out JsonElement arrayEl)
    {
        // Look for "data" or "forecast" array
        if (root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array) { arrayEl = d; return true; }
        if (root.TryGetProperty("forecast", out var f) && f.ValueKind == JsonValueKind.Array) { arrayEl = f; return true; }

        // Otherwise, search first array anywhere (defensive)
        if (root.ValueKind == JsonValueKind.Array) { arrayEl = root; return true; }
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in root.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.Array) { arrayEl = p.Value; return true; }
                if (TryFirstArray(p.Value, out var sub)) { arrayEl = sub; return true; }
            }
        }
        arrayEl = default;
        return false;
    }

    private static (bool has, double value) TryFindNumber(JsonElement e, string name)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in e.EnumerateObject())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind is JsonValueKind.Number)
                    return (true, p.Value.GetDouble());
                var inner = TryFindNumber(p.Value, name);
                if (inner.has) return inner;
            }
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var i in e.EnumerateArray())
            {
                var inner = TryFindNumber(i, name);
                if (inner.has) return inner;
            }
        }
        return (false, double.NaN);
    }
    /// <summary>Get current MOER (g/kWh) for a region, or null if not available on plan.</summary>
    public async Task<double?> GetMoerNowAsync(string region, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);
        return await TryGetMoerForecastNowAsync(region, ct); // first point of v3/forecast
    }

    /// <summary>
    /// Get MOER forecast (g/kWh) at/near a target time for a region, or null if not available.
    /// Auto-extends horizon_hours beyond 24h to cover 'target'.
    /// Picks the interval that covers 'target' (start<=target<end), otherwise the first interval after target.
    /// </summary>
    public async Task<double?> GetMoerAtAsync(string region, DateTimeOffset target, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var effectiveTarget = target > now ? target : now;

        // Compute horizon_hours: default 24h, extend to cover target, cap to 168h (7 days)
        var neededHours = (int)Math.Ceiling((effectiveTarget - now).TotalHours);
        var horizon = Math.Max(24, Math.Clamp(neededHours + 1, 1, 168));

        var points = await GetForecastWindowAsync(region, horizon, ct);
        if (points.Count == 0) return null;

        // Choose interval containing 'effectiveTarget', else the first interval after it
        var at = points
            .OrderBy(p => p.Start)
            .FirstOrDefault(p => p.Start <= effectiveTarget && effectiveTarget < p.End)
            ?? points.Where(p => p.Start >= effectiveTarget).OrderBy(p => p.Start).FirstOrDefault();

        return at?.MoerGPerKwh;
    }

    /// <summary>
    /// Returns the lowest MOER and its timestamp within [now .. target] for 'region'.
    /// Also returns the used horizon_hours so callers can log/debug.
    /// </summary>
    public async Task<(double? bestMoer, DateTimeOffset? at, int usedHorizonHours)> GetBestUntilAsync(
        string region, DateTimeOffset target, CancellationToken ct = default)
    {
        await EnsureTokenAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var effectiveTarget = target > now ? target : now;

        var neededHours = (int)Math.Ceiling((effectiveTarget - now).TotalHours);
        var horizon = neededHours;

        var points = await GetForecastWindowAsync(region, horizon, ct);
        if (points.Count == 0) return (null, null, horizon);

        var best = points
            .Where(p => p.Start <= effectiveTarget)
            .OrderBy(p => p.MoerGPerKwh)
            .FirstOrDefault();

        return (best?.MoerGPerKwh, best?.Start, horizon);
    }


    private sealed class ForecastPoint
    {
        public DateTimeOffset Timestamp { get; init; }
        public DateTimeOffset Start { get; init; }
        public DateTimeOffset End { get; init; }
        public double MoerGPerKwh { get; init; }
    }

    private async Task<List<ForecastPoint>> GetForecastWindowAsync(
    string region, int horizonHours, CancellationToken ct)
    {
        await EnsureTokenAsync(ct);

        var baseUri = _http.BaseAddress ?? new Uri("https://api.watttime.org");
        var uri = new Uri(baseUri,
            $"/v3/forecast?region={Uri.EscapeDataString(region)}&signal_type=co2_moer&horizon_hours={horizonHours}");

        var sw = Stopwatch.StartNew();
        int status = 0;
        string? body = null;
        string? err = null;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GetTokenUnsafe());

            using var resp = await _http.SendAsync(req, ct);
            status = (int)resp.StatusCode;

            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                _log.LogWarning("WattTime v3/forecast 401 for {Region}. Refreshing token…", region);
                await EnsureTokenAsync(ct);

                using var retry = new HttpRequestMessage(HttpMethod.Get, uri);
                retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GetTokenUnsafe());
                using var resp2 = await _http.SendAsync(retry, ct);
                status = (int)resp2.StatusCode;
                body = await resp2.Content.ReadAsStringAsync(ct);

                if (!resp2.IsSuccessStatusCode)
                {
                    _log.LogDebug("WattTime v3/forecast {Status} for {Region}. Body: {Body}", status, region, body);
                    return new List<ForecastPoint>();
                }

                return ParseForecastList(body);
            }

            body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogDebug("WattTime v3/forecast {Status} for {Region}. Body: {Body}", status, region, body);
                return new List<ForecastPoint>();
            }

            return ParseForecastList(body);
        }
        catch (Exception ex)
        {
            err = ex.ToString();
            throw;
        }
        finally
        {
            sw.Stop();
            await _audit.LogWattTimeCallAsync(new WattTimeCallRecord
            {
                Method = "GET",
                RequestUrl = uri.ToString(),
                Region = region,
                SignalType = "co2_moer",
                HorizonHours = horizonHours,
                StatusCode = status,
                Success = status >= 200 && status < 300,
                DurationMs = (int)sw.ElapsedMilliseconds,
                ResponseBody = Truncate(body, 100_000),
                Error = err,
                SourceFile = nameof(WattTimeProvider),
                SourceLine = 0,
                RequestId = _correlation.Current
            }, ct);
        }
    }

    private static List<ForecastPoint> ParseForecastList(string json)
    {
        using var doc = JsonDocument.Parse(json);

        if (!TryFirstArray(doc.RootElement, out var arr))
            return new List<ForecastPoint>();

        var list = new List<ForecastPoint>();
        foreach (var p in arr.EnumerateArray())
        {
            DateTimeOffset? start = null, end = null;
            if (p.TryGetProperty("point_time", out var s) && s.ValueKind == JsonValueKind.String)
                start = DateTimeOffset.Parse(s.GetString()!, null,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
            if (p.TryGetProperty("point_time", out var e) && e.ValueKind == JsonValueKind.String)
                end = DateTimeOffset.Parse(e.GetString()!, null,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
            if (start is null && p.TryGetProperty("point_time", out var pt) && pt.ValueKind == JsonValueKind.String)
            {
                var t = DateTimeOffset.Parse(pt.GetString()!, null,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
                start = t;
                end = t.AddMinutes(5);
            }

            if (!start.HasValue || !end.HasValue) continue;
            if (!p.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.Number) continue;

            var moer = v.GetDouble();
            if (!double.IsFinite(moer)) continue;

            list.Add(new ForecastPoint { Start = start.Value, End = end.Value, MoerGPerKwh = moer });
        }
        return list;
    }

    private async Task<List<ForecastPoint>> GetForecastPointsAsync(
    string region, int horizonHours, CancellationToken ct)
    {
        await EnsureTokenAsync(ct);

        var baseUri = _http.BaseAddress ?? new Uri("https://api.watttime.org");
        var uri = new Uri(baseUri,
            $"/v3/forecast?region={Uri.EscapeDataString(region)}&signal_type=co2_moer&horizon_hours={horizonHours}");

        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GetTokenUnsafe());

        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            _log.LogWarning("WattTime v3/forecast 401 for {Region}; refreshing token…", region);
            await EnsureTokenAsync(ct);
            return await GetForecastPointsAsync(region, horizonHours, ct);
        }

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _log.LogDebug("WattTime v3/forecast {Status} for {Region}. Body: {Body}",
                (int)resp.StatusCode, region, body);
            return new List<ForecastPoint>();
        }

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (!TryFirstArray(doc.RootElement, out var arr)) return new List<ForecastPoint>();

        var result = new List<ForecastPoint>();
        foreach (var el in arr.EnumerateArray())
        {
            if (!el.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.Number) continue;

            // Most v3 responses expose "point_time" or something similar
            DateTimeOffset ts;
            if (el.TryGetProperty("point_time", out var t) && t.ValueKind == JsonValueKind.String)
            {
                ts = DateTimeOffset.Parse(t.GetString()!, null,
                    System.Globalization.DateTimeStyles.AssumeUniversal |
                    System.Globalization.DateTimeStyles.AdjustToUniversal);
            }
            else if (el.TryGetProperty("timestamp", out var t2) && t2.ValueKind == JsonValueKind.String)
            {
                ts = DateTimeOffset.Parse(t2.GetString()!, null,
                    System.Globalization.DateTimeStyles.AssumeUniversal |
                    System.Globalization.DateTimeStyles.AdjustToUniversal);
            }
            else
            {
                continue;
            }

            var val = v.GetDouble();
            if (!double.IsFinite(val)) continue;

            result.Add(new ForecastPoint { Timestamp = ts, MoerGPerKwh = val });
        }

        // Ensure chronological
        return result.OrderBy(p => p.Timestamp).ToList();
    }
    // ======== BATCH HELPERS (for run_now / schedule_at batch) ========

    public async Task<double?> GetBatchAverageNowAsync(
        string region,
        int batchMinutes,
        CancellationToken ct = default)
    {
        if (batchMinutes <= 0)
            return null;

        // How many hours of forecast we need at minimum
        var now = DateTimeOffset.UtcNow;
        var horizonHours = Math.Max(1, (int)Math.Ceiling(batchMinutes / 60.0));

        var points = await GetForecastWindowAsync(region, horizonHours, ct);
        if (points.Count == 0)
            return null;

        var windowEnd = now.AddMinutes(batchMinutes);

        // Take all forecast points overlapping [now, now+batchMinutes]
        var slice = points
            .Where(p => p.Start < windowEnd && p.End > now)
            .ToList();

        if (slice.Count == 0)
            return null;

        return slice.Average(p => p.MoerGPerKwh);
    }

    public async Task<(double? bestAvgMoer, DateTimeOffset? startAt, int usedHorizonHours)>
    GetBestBatchWindowUntilAsync(
        string region,
        DateTimeOffset scheduleFrom,
        DateTimeOffset scheduleUntil,
        int batchMinutes,
        CancellationToken ct = default)
    {
        if (batchMinutes <= 0)
            return (null, null, 0);

        var now = DateTimeOffset.UtcNow;

        // Effective window:
        // scheduleFrom cannot be earlier than now (no past windows).
        var from = scheduleFrom > now ? scheduleFrom : now;
        var until = scheduleUntil > from ? scheduleUntil : from.AddMinutes(5);

        // Determine horizon hours needed.
        var horizonHours = Math.Max(1,
            (int)Math.Ceiling((until - now).TotalHours));

        // Fetch all forecast points up to the largest needed horizon.
        var points = await GetForecastWindowAsync(region, horizonHours, ct);
        if (points.Count == 0)
            return (null, null, horizonHours);

        // We ONLY care about forecast points that begin AFTER scheduleFrom.
        var ordered = points
            .Where(p => p.Start >= from)
            .OrderBy(p => p.Start)
            .ToList();

        if (ordered.Count == 0)
            return (null, null, horizonHours);

        // How many forecast points fit in one batch window?
        int batchSlots = Math.Max(1, batchMinutes / 5);

        double? bestAvg = null;
        DateTimeOffset? bestStart = null;

        // Evaluate windowed averages within [from .. until]
        for (int i = 0; i + batchSlots - 1 < ordered.Count; i++)
        {
            var start = ordered[i].Start;
            var end = start.AddMinutes(batchMinutes);

            // MUST fit entirely inside [from .. until]
            if (start < from || end > until)
                continue;

            // Compute average of this batch window
            var slice = ordered.Skip(i).Take(batchSlots).ToList();
            var avg = slice.Average(p => p.MoerGPerKwh);

            if (!bestAvg.HasValue || avg < bestAvg.Value)
            {
                bestAvg = avg;
                bestStart = start;
            }
        }

        return (bestAvg, bestStart, horizonHours);
    }

    private static string? Truncate(string? s, int max)
    => s is null ? null : (s.Length <= max ? s : s.Substring(0, max));
}
