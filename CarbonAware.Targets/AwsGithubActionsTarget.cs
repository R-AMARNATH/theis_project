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

    public async Task<string> ScheduleAsync(AdviceResult advice, JobSpec job, CorrelationContext correlation, CancellationToken ct = default)
    {
        if (!"aws".Equals(advice.Cloud, StringComparison.OrdinalIgnoreCase))
            return $"skipped-non-aws-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";

        var inputs = new
        {
            region = advice.Region,
            instanceType = DefaultInstanceType
        };

        var body = new { @ref = _opts.Branch, inputs };

        var baseUri = _http.BaseAddress ?? new Uri("https://api.github.com");
        var url = new Uri(baseUri, $"/repos/{_opts.Owner}/{_opts.Repo}/actions/workflows/{_opts.AwsWorkflow}/dispatches");

        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(url, content, ct);

        if (resp.StatusCode == HttpStatusCode.NoContent)
        {
            var id = $"gha-aws-dispatch-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
            _log.LogInformation("Triggered AWS VM workflow for region {Region} on {Branch}. CorrelationId={Id}",
                advice.Region, _opts.Branch, id);
            return id;
        }

        var err = await resp.Content.ReadAsStringAsync(ct);
        _log.LogError("GitHub AWS workflow_dispatch failed. Status={Status} Body={Body}", (int)resp.StatusCode, err);
        throw new InvalidOperationException($"GitHub AWS workflow_dispatch failed: {(int)resp.StatusCode} {err}");
    }
}