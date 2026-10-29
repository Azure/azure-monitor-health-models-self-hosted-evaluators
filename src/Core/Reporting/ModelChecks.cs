using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SelfHostedEvaluators.Core.Auth;
using SelfHostedEvaluators.Core.Configuration;
using SelfHostedEvaluators.Core.Diagnostics;

namespace SelfHostedEvaluators.Core.Reporting;

/// <summary>Runs advisory model and entity checks without disabling reporting when remote checks fail.</summary>
public sealed class ModelChecks
{
    private readonly IReadOnlyDictionary<Uri, ModelClient> models;
    private readonly TimeProvider time;
    private readonly RuntimeDiagnostics diagnostics;
    private readonly ILogger logger;

    /// <summary>Creates checks using the same model clients and request budgets as reporting.</summary>
    /// <param name="models">The externally owned clients for configured model destinations.</param>
    /// <param name="time">The clock for the recurring check cadence.</param>
    /// <param name="redactor">The shared redactor for warnings.</param>
    /// <param name="diagnostics">Optional warning metrics.</param>
    /// <param name="logger">An optional destination for redacted warnings.</param>
    public ModelChecks(IEnumerable<ModelClient> models, TimeProvider time, Redactor redactor,
        RuntimeDiagnostics? diagnostics = null, ILogger? logger = null)
    {
        this.models = models.ToDictionary(m => m.Configuration.DataPlaneUri);
        this.time = time;
        this.diagnostics = diagnostics ?? RuntimeDiagnostics.Shared;
        this.logger = redactor.Wrap(logger ?? NullLogger.Instance);
    }

    /// <summary>Checks each model and its distinct entities immediately and then every 30 minutes.</summary>
    /// <param name="snapshot">The startup model and entity configuration.</param>
    /// <param name="ct">Stops the recurring checks and cancels pending requests.</param>
    /// <returns>The combined lifetime of all model-check loops.</returns>
    public Task RunAsync(ConfigSnapshot snapshot, CancellationToken ct) =>
        Task.WhenAll(snapshot.Models.Select(config => RunModelAsync(config, ct)));

    private async Task RunModelAsync(ModelConfiguration config, CancellationToken ct)
    {
        using var cadence = new PeriodicTimer(TimeSpan.FromMinutes(30), time);
        try
        {
            do
            {
                ct.ThrowIfCancellationRequested();
                await CheckModelAsync(config, ct).ConfigureAwait(false);
            } while (await cadence.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task CheckModelAsync(ModelConfiguration config, CancellationToken ct)
    {
        var model = models[config.HealthModel.DataPlaneUri];
        await CheckAsync(model, "/api", false, ct).ConfigureAwait(false);
        foreach (var entity in config.Signals.Select(s => s.Entity).Distinct(StringComparer.Ordinal))
            await CheckAsync(model, "/api/entities/" + Uri.EscapeDataString(entity), true, ct).ConfigureAwait(false);
    }

    private async Task CheckAsync(ModelClient model, string path, bool entity, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, model.Route(path));
            using var response = await model.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.TooManyRequests) model.RetryDelay(response, 0);
                Warn(model, "http", (int)response.StatusCode);
                return;
            }
            if (!entity) return;
            await response.Content.LoadIntoBufferAsync(65536, ct).ConfigureAwait(false);
            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false),
                new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { Warn(model, "shape"); return; }
            if (!root.TryGetProperty("signalInputs", out var signals))
            {
                if (root.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
                    root = properties;
                if (!root.TryGetProperty("signals", out signals)) { Warn(model, "shape"); return; }
            }
            if (signals.ValueKind != JsonValueKind.Array)
            { Warn(model, "shape"); return; }
            if (signals.GetArrayLength() >= 50) Warn(model, "signal_cap");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or AuthenticationFailureException or JsonException or OperationCanceledException)
        { Warn(model, error is JsonException ? "shape" : "transport"); }
    }

    private void Warn(ModelClient model, string outcome, int? status = null)
    {
        diagnostics.CheckWarning(model.Name, outcome);
        logger.LogWarning("Remote model/entity check warning for model {Model}: {Outcome} (HTTP {Status}). Reporting remains enabled.",
            model.Name, outcome, status);
    }
}
