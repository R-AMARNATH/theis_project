using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CarbonAware.Core;
using CarbonAware.Targets.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CarbonAware.Targets;

public sealed class GcpGithubActionsTarget : ICloudTarget
{
    private readonly HttpClient _http;
    private readonly GitHubActionsOptions _opts;
    private readonly ILogger<GcpGithubActionsTarget> _log;

    // Matches CostSignalOptions.InstanceTypeByCloud["gcp"] — was e2-micro, which priced differently from what was actually deployed
    private const string DefaultMachineType = "e2-medium";

    public GcpGithubActionsTarget(
        HttpClient http,
        IOptions<GitHubActionsOptions> opts,
        ILogger<GcpGithubActionsTarget> log)
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
        if (!"gcp".Equals(advice.Cloud, StringComparison.OrdinalIgnoreCase))
            return $"skipped-non-gcp-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";

        // We pass region; the workflow can derive a zone (e.g., region + "-b").
        // cycleId is a REQUIRED workflow input -- omitting it makes workflow_dispatch fail with 422.
        var inputs = new
        {
            region = advice.Region,
            machineType = DefaultMachineType,
            cycleId = experiment.CycleId,
            objectiveType = experiment.ObjectiveType,
            weightConfig = experiment.WeightProfile ?? ""
        };

        var body = new
        {
            @ref = _opts.Branch,
            inputs
        };

        var baseUri = _http.BaseAddress ?? new Uri("https://api.github.com");
        var url = new Uri(baseUri, $"/repos/{_opts.Owner}/{_opts.Repo}/actions/workflows/{_opts.GcpWorkflow}/dispatches");

        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(url, content, ct);

        if (resp.StatusCode == HttpStatusCode.NoContent)
        {
            var id = $"gha-gcp-dispatch-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
            _log.LogInformation("Triggered GCP VM workflow for region {Region} on {Branch}. CorrelationId={Id}",
                advice.Region, _opts.Branch, id);
            return id;
        }

        var err = await resp.Content.ReadAsStringAsync(ct);
        _log.LogError("GitHub GCP workflow_dispatch failed. Status={Status} Body={Body}", (int)resp.StatusCode, err);
        throw new InvalidOperationException($"GitHub GCP workflow_dispatch failed: {(int)resp.StatusCode} {err}");
    }
}