using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CarbonAware.Core;
using CarbonAware.Targets.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CarbonAware.Targets;

public sealed class AwsGithubActionsTarget : ICloudTarget
{
    private readonly HttpClient _http;
    private readonly GitHubActionsOptions _opts;
    private readonly ILogger<AwsGithubActionsTarget> _log;

    private const string DefaultInstanceType = "t3.medium";  // matches CostSignalOptions.InstanceTypeByCloud["aws"] — was t3.micro, which priced differently from what was actually deployed

    public AwsGithubActionsTarget(
        HttpClient http,
        IOptions<GitHubActionsOptions> opts,
        ILogger<AwsGithubActionsTarget> log)
    {
        _http = http;
        _opts = opts.Value;
        _log = log;

        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CarbonAware-Scheduler", "1.0"));
        if (!string.IsNullOrWhiteSpace(_opts.Token))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _opts.Token);
        }
    }

    public async Task<string> ScheduleAsync(AdviceResult advice, JobSpec job, CorrelationContext correlation, ExperimentContext experiment, CancellationToken ct = default)
    {
        if (!"aws".Equals(advice.Cloud, StringComparison.OrdinalIgnoreCase))
            return $"skipped-non-aws-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";

        // cycleId is a REQUIRED workflow input -- omitting it makes workflow_dispatch fail with 422.
        var inputs = new
        {
            region = advice.Region,
            instanceType = DefaultInstanceType,
            cycleId = experiment.CycleId,
            objectiveType = experiment.ObjectiveType,
            weightConfig = experiment.WeightProfile ?? ""
        };

        var body = new { @ref = _opts.Branch, inputs };

        var baseUri = _http.BaseAddress ?? new Uri("https://api.github.com");
        var url = new Uri(baseUri, $"/repos/{_opts.Owner}/{_opts.Repo}/actions/workflows/{_opts.AwsWorkflow}/dispatches");
        var json = JsonSerializer.Serialize(body);

        // Same retry pattern as AzureGithubActionsTarget -- GitHub's API occasionally returns
        // transient 502/503/429 (confirmed in production during a real automated cycle),
        // which otherwise kills the whole cycle for a blip that's entirely outside this app.
        const int maxAttempts = 3;
        HttpResponseMessage? resp = null;
        string? lastErr = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            resp = await _http.PostAsync(url, content, ct);

            if (resp.StatusCode == HttpStatusCode.NoContent)
            {
                var id = $"gha-aws-dispatch-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
                _log.LogInformation("Triggered AWS VM workflow for region {Region} on {Branch}. CorrelationId={Id}",
                    advice.Region, _opts.Branch, id);
                return id;
            }

            var isTransient = resp.StatusCode is HttpStatusCode.ServiceUnavailable
                or HttpStatusCode.BadGateway
                or (HttpStatusCode)429;

            lastErr = await resp.Content.ReadAsStringAsync(ct);

            if (!isTransient || attempt == maxAttempts)
            {
                _log.LogError("GitHub AWS workflow_dispatch failed (attempt {Attempt}/{Max}). Status={Status} Body={Body}",
                    attempt, maxAttempts, (int)resp.StatusCode, lastErr);
                break;
            }

            var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
            _log.LogWarning("GitHub AWS workflow_dispatch transient failure (attempt {Attempt}/{Max}), retrying in {Delay}s. Status={Status} Body={Body}",
                attempt, maxAttempts, delay.TotalSeconds, (int)resp.StatusCode, lastErr);
            await Task.Delay(delay, ct);
        }

        throw new InvalidOperationException($"GitHub AWS workflow_dispatch failed after {maxAttempts} attempts: {(int)resp!.StatusCode} {lastErr}");
    }
}