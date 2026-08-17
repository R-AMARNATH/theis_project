using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CarbonAware.Core;
using CarbonAware.Targets.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CarbonAware.Targets;

public sealed class AzureGithubActionsTarget : ICloudTarget
{
    private readonly HttpClient _http;
    private readonly GitHubActionsOptions _opts;
    private readonly ILogger<AzureGithubActionsTarget> _log;

    // Sensible default VM size; change if you prefer Standard_B1ls (not in all regions)
    // Matches CostSignalOptions.InstanceTypeByCloud["azure"] — updated from Standard_B2s to
    // Standard_D2alds_v6 per the finalized experiment manifest.
    private const string DefaultVmSize = "Standard_D2alds_v6";

    public AzureGithubActionsTarget(
        HttpClient http,
        IOptions<GitHubActionsOptions> opts,
        ILogger<AzureGithubActionsTarget> log)
    {
        _http = http;
        _opts = opts.Value;
        _log = log;

        // GitHub REST requirements
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CarbonAware-Scheduler", "1.0"));
        if (!string.IsNullOrWhiteSpace(_opts.Token))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _opts.Token);
        }
    }

    public async Task<string> ScheduleAsync(AdviceResult advice, JobSpec job, CorrelationContext correlation, ExperimentContext experiment, CancellationToken ct = default)
    {
        // Only act if Azure was selected; otherwise router will use other targets
        if (!"azure".Equals(advice.Cloud, StringComparison.OrdinalIgnoreCase))
            return $"skipped-non-azure-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";

        // Region comes from the advice; VM size we default.
        // cycleId is a REQUIRED workflow input -- omitting it makes workflow_dispatch fail with 422.
        var inputs = new
        {
            vmsize = DefaultVmSize,
            region = advice.Region,
            cycleId = experiment.CycleId,
            objectiveType = experiment.ObjectiveType,
            weightConfig = experiment.WeightProfile ?? ""
        };

        var body = new
        {
            @ref = _opts.Branch, // escape reserved keyword
            inputs
        };

        var baseUri = _http.BaseAddress ?? new Uri("https://api.github.com");
        var url = new Uri(baseUri, $"/repos/{_opts.Owner}/{_opts.Repo}/actions/workflows/{_opts.AzureWorkflow}/dispatches");
        var json = JsonSerializer.Serialize(body);

        // GitHub's API occasionally returns transient 502/503/429 responses (confirmed in
        // production: "No server is currently available to service your request" during a
        // real automated cycle). Without a retry, one transient blip kills the entire cycle
        // and the caller gets a bare unhandled 500 with no useful response body. Retry a
        // few times with backoff before giving up for real.
        const int maxAttempts = 3;
        HttpResponseMessage? resp = null;
        string? lastErr = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            resp = await _http.PostAsync(url, content, ct);

            if (resp.StatusCode == HttpStatusCode.NoContent)
            {
                var correlationId = $"gha-azure-dispatch-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
                _log.LogInformation("Triggered Azure VM workflow for region {Region} on {Branch}. CorrelationId={Id}",
                    advice.Region, _opts.Branch, correlationId);
                return correlationId;
            }

            var isTransient = resp.StatusCode is HttpStatusCode.ServiceUnavailable
                or HttpStatusCode.BadGateway
                or (HttpStatusCode)429;

            lastErr = await resp.Content.ReadAsStringAsync(ct);

            if (!isTransient || attempt == maxAttempts)
            {
                _log.LogError("GitHub workflow_dispatch failed (attempt {Attempt}/{Max}). Status={Status} Body={Body}",
                    attempt, maxAttempts, (int)resp.StatusCode, lastErr);
                break;
            }

            var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt)); // 2s, 4s, 8s
            _log.LogWarning("GitHub workflow_dispatch transient failure (attempt {Attempt}/{Max}), retrying in {Delay}s. Status={Status} Body={Body}",
                attempt, maxAttempts, delay.TotalSeconds, (int)resp.StatusCode, lastErr);
            await Task.Delay(delay, ct);
        }

        throw new InvalidOperationException($"GitHub workflow_dispatch failed after {maxAttempts} attempts: {(int)resp!.StatusCode} {lastErr}");
    }
}