using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using SelfHostedEvaluators.Core.Configuration;
using SelfHostedEvaluators.Core.Polling;
using SelfHostedEvaluators.Plugins.Abstractions;

namespace SelfHostedEvaluators.Core.Tests;

internal static class PollingTestSupport
{
    public static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public static FakeTimeProvider Clock() => new(Start);
    public static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static ConfigSnapshot Snapshot(int signals = 1, int batchSize = 3, int concurrency = 2,
        string timeout = "30s", string interval = "10s", bool secondModel = false,
        string? evaluation = null, string? context = null)
    {
        var plugin = JsonNode.Parse(EnvironmentConfigTests.Plugin)!;
        plugin["batch"] = new JsonObject { ["maxSize"] = batchSize };
        plugin["concurrency"] = concurrency;
        plugin["timeout"] = timeout;
        var model = JsonNode.Parse(EnvironmentConfigTests.Model)!;
        var list = new JsonArray();
        for (var i = 0; i < signals; i++)
        {
            var signal = JsonNode.Parse(EnvironmentConfigTests.Model)!["signals"]![0]!.DeepClone();
            signal["name"] = $"signal-{i:000}";
            signal["interval"] = interval;
            if (evaluation is not null) signal["evaluation"] = JsonNode.Parse(evaluation);
            if (context is not null) signal["context"] = context;
            list.Add(signal);
        }
        model["signals"] = list;
        var env = EnvironmentConfigTests.Environment(plugin.ToJsonString(), model.ToJsonString());
        if (secondModel)
        {
            model["name"] = "model-two";
            model["healthModel"]!["dataPlaneUri"] = "https://other.test";
            env["SHE_MODEL_TWO"] = model.ToJsonString();
        }
        return new EnvironmentConfigSource().Load(env);
    }

    public static ScheduledBatch Batch(ConfigSnapshot snapshot, int model = 0, TimeSpan? interval = null) =>
        new("sink-one", new(Start, interval ?? snapshot.Models[model].Signals[0].Interval),
            snapshot.Models[model].Signals.Select(s => new SignalQuery(
                new(snapshot.Models[model].HealthModel.DataPlaneUri, s.Entity, s.Name), s.Query)).ToArray());

    public static IReadOnlyList<SignalResult> Observations(IReadOnlyList<SignalQuery> queries) =>
        queries.Select(q => (SignalResult)new SignalResult.Observation(q.Key, 5, null, Start)).ToArray();

    public static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(1, timeout.Token);
    }
}

internal sealed class DelegateSink(
    Func<IReadOnlyList<SignalQuery>, QueryWindow, CancellationToken, Task<IReadOnlyList<SignalResult>>> query) : ISinkClient
{
    public int Disposals;
    public Task<IReadOnlyList<SignalResult>> QueryBatchAsync(
        IReadOnlyList<SignalQuery> batch, QueryWindow window, CancellationToken ct) => query(batch, window, ct);
    public ValueTask DisposeAsync() { Interlocked.Increment(ref Disposals); return ValueTask.CompletedTask; }
}
