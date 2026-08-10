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
    private const string DefaultVmSize = "Standard_B2s";

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
            // GitHub requires a ref (branch or tag) on which the workflow will run
            // Ensure your workflow file exists on this branch
            // e.g. "main"
            // If you supply a filename in AzureWorkflow, GitHub allows filename or numeric ID
            // POST /repos/{owner}/{repo}/actions/workflows/{workflow_id}/dispatches
            // returns 204 No Content on success
            // We’ll return a synthetic request id (timestamp) since dispatch has no body
            // Optionally, you can follow up by listing runs to get the actual run id.
            // For now: trigger & return a synthetic correlation id
            // https://docs.github.com/en/rest/actions/workflows#create-a-workflow-dispatch-event
            // payload below
            @ref = _opts.Branch, // escape reserved keyword
            inputs
        };

        var baseUri = _http.BaseAddress ?? new Uri("https://api.github.com");
        var url = new Uri(baseUri, $"/repos/{_opts.Owner}/{_opts.Repo}/actions/workflows/{_opts.AzureWorkflow}/dispatches");
        var json = JsonSerializer.Serialize(body);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var resp = await _http.PostAsync(url, content, ct);
        if (resp.StatusCode == HttpStatusCode.NoContent)
        {
            var correlationId = $"gha-azure-dispatch-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
            _log.LogInformation("Triggered Azure VM workflow for region {Region} on {Branch}. CorrelationId={Id}",
                advice.Region, _opts.Branch, correlationId);
            return correlationId;
        }

        var err = await resp.Content.ReadAsStringAsync(ct);
        _log.LogError("GitHub workflow_dispatch failed. Status={Status} Body={Body}", (int)resp.StatusCode, err);
        throw new InvalidOperationException($"GitHub workflow_dispatch failed: {(int)resp.StatusCode} {err}");
    }
}
