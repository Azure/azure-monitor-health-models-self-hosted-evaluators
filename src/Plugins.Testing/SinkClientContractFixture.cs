using System.Reflection;
using System.Text.Json;
using NUnit.Framework;
using SelfHostedEvaluators.Plugins.Abstractions;

namespace SelfHostedEvaluators.Plugins.Testing;

/// <summary>Selects the controlled source behavior used by a real-client contract scenario.</summary>
public enum ContractOutcome
{
    /// <summary>Returns a numeric observation with value 42.</summary>
    Observation,
    /// <summary>Returns a successful response without usable data.</summary>
    NoData,
    /// <summary>Returns a failure with a seven-second retry delay.</summary>
    Failure,
    /// <summary>Delays the response beyond the client's timeout.</summary>
    Timeout,
    /// <summary>Keeps a query pending so the test can cancel active work.</summary>
    Delayed
}

/// <summary>A real client plus a controlled sink, not a replacement ISinkClient.</summary>
public abstract class SinkClientScenario : IAsyncDisposable
{
    /// <summary>Gets the actual sink client under test, not a replacement implementation.</summary>
    public abstract ISinkClient Client { get; }
    /// <summary>Creates a valid source query and configures the requested controlled outcome.</summary>
    /// <param name="key">The complete identity the client must preserve.</param>
    /// <param name="outcome">The behavior to arrange at the controlled sink.</param>
    /// <returns>A query for the real client to execute.</returns>
    public abstract SignalQuery Query(SignalKey key, ContractOutcome outcome);
    /// <summary>Waits until the controlled source has received an active request.</summary>
    public abstract Task WaitForRequestAsync();
    /// <summary>Releases the scenario's client and controlled-source resources.</summary>
    public abstract ValueTask DisposeAsync();
}

/// <summary>Provides reusable NUnit assertions for package shape and real sink-client batch behavior.</summary>
public abstract class SinkClientContractFixture
{
    /// <summary>Creates a fresh real client and controlled source for one inherited contract test.</summary>
    /// <returns>A scenario owned and disposed by that test.</returns>
    protected abstract ValueTask<SinkClientScenario> CreateScenarioAsync();
    /// <summary>Gets the fixed historical tick and two-minute interval used by contract queries.</summary>
    public static QueryWindow Window { get; } = new(DateTimeOffset.FromUnixTimeSeconds(1700000000), TimeSpan.FromMinutes(2));
    /// <summary>Parses and clones JSON so the returned element outlives the temporary document.</summary>
    /// <param name="json">The JSON to parse.</param>
    /// <returns>An independently owned JSON element.</returns>
    public static JsonElement Json(string json) { using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); }
    /// <summary>Creates a test key with a repeated signal name to exercise full-key identity.</summary>
    /// <param name="model">The synthetic model's host label.</param>
    /// <param name="entity">The entity name within the synthetic model.</param>
    /// <returns>A complete key whose signal name is always same-name.</returns>
    public static SignalKey Key(string model = "one", string entity = "entity") => new(new Uri($"https://{model}.model.test"), entity, "same-name");

    /// <summary>Loads a first-party package by naming convention and activates its registered sink plugin.</summary>
    /// <param name="package">The assembly and type suffix, such as Datadog or Static.</param>
    /// <returns>The package's sink plugin instance.</returns>
    public static ISinkPlugin Plugin(string package)
    {
        var assembly = Assembly.Load($"SelfHostedEvaluators.Plugins.{package}");
        var type = assembly.GetType($"SelfHostedEvaluators.Plugins.{package}.{package}Package");
        Assert.That(type, Is.Not.Null, "The package entry type is missing.");
        var registrar = new PluginRegistrar();
        ((IPluginPackage)Activator.CreateInstance(type!)!).Register(registrar);
        return (ISinkPlugin)Activator.CreateInstance(registrar.Build().SinkPluginType)!;
    }

    /// <summary>Asserts the first-party manifest, registration, dependency boundary and endpoint contract.</summary>
    /// <param name="package">The package entry point under test.</param>
    /// <param name="id">The expected manifest package identifier.</param>
    /// <param name="endpoints">The expected required endpoint names, in order.</param>
    public static void AssertPackage(IPluginPackage package, string id, params string[] endpoints)
    {
        var registrar = new PluginRegistrar();
        package.Register(registrar);
        var sink = (ISinkPlugin)Activator.CreateInstance(registrar.Build().SinkPluginType)!;
        var assembly = package.GetType().Assembly;
        Assert.That(assembly.GetTypes().Count(t => !t.IsAbstract && typeof(IPluginPackage).IsAssignableFrom(t)), Is.EqualTo(1));
        Assert.That(assembly.GetTypes().Count(t => !t.IsAbstract && typeof(ISinkPlugin).IsAssignableFrom(t)), Is.EqualTo(1));
        Assert.That(assembly.GetReferencedAssemblies().Any(a =>
            a.Name!.Contains(".Core", StringComparison.Ordinal) || a.Name.Contains(".Host", StringComparison.Ordinal) ||
            a.Name.StartsWith("Azure.", StringComparison.Ordinal)), Is.False);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(assembly.Location)!, "plugin.json")));
        var json = manifest.RootElement;
        Assert.That(json.GetProperty("id").GetString(), Is.EqualTo(id));
        Assert.That(json.GetProperty("version").GetString(), Is.EqualTo("1.0.0"));
        Assert.That(json.GetProperty("abstractionsVersion").GetString(), Is.EqualTo("1.0.0"));
        Assert.That(json.GetProperty("assembly").GetString(), Is.EqualTo(Path.GetFileName(assembly.Location)));
        Assert.That(typeof(IPluginPackage).IsAssignableFrom(assembly.GetType(json.GetProperty("entryType").GetString()!)), Is.True);
        Assert.That(json.GetProperty("entryType").GetString(), Is.EqualTo(package.GetType().FullName));
        Assert.That(sink.Capabilities.RequiredEndpoints, Is.EqualTo(endpoints));
    }

    /// <summary>Verifies input ordering, duplicate full keys, outcomes and explicit retry metadata.</summary>
    [Test]
    public async Task SinkClientContract_BatchPreservesFullKeysAndOutcomes()
    {
        await using var rig = await CreateScenarioAsync();
        SignalQuery[] queries =
        [
            rig.Query(Key(), ContractOutcome.Observation),
            rig.Query(Key("two"), ContractOutcome.NoData),
            rig.Query(Key(entity: "another"), ContractOutcome.Failure),
            rig.Query(Key("three"), ContractOutcome.Observation),
            rig.Query(Key(), ContractOutcome.Observation)
        ];
        var results = await rig.Client.QueryBatchAsync(queries, Window, default);
        Assert.That(results.Select(r => r.Key), Is.EqualTo(queries.Select(q => q.Key)));
        Assert.That(results[0], Is.TypeOf<SignalResult.Observation>());
        Assert.That(((SignalResult.Observation)results[0]).Value, Is.EqualTo(42));
        Assert.That(results[1], Is.TypeOf<SignalResult.NoData>());
        Assert.That(results[2], Is.TypeOf<SignalResult.Failure>());
        Assert.That(((SignalResult.Failure)results[2]).RetryAfter, Is.EqualTo(TimeSpan.FromSeconds(7)));
        Assert.That(results[3], Is.TypeOf<SignalResult.Observation>());
    }

    /// <summary>Verifies empty batches and propagation of pre-canceled caller tokens.</summary>
    [Test]
    public async Task SinkClientContract_EmptyBatchAndPreCancellation()
    {
        await using var rig = await CreateScenarioAsync();
        Assert.That(await rig.Client.QueryBatchAsync([], Window, default), Is.Empty);
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.ThrowsAsync<OperationCanceledException>(async () => await rig.Client.QueryBatchAsync([], Window, source.Token));
    }

    /// <summary>Verifies caller cancellation reaches a genuinely active real-client query.</summary>
    [Test]
    public async Task SinkClientContract_ActiveCancellationPropagates()
    {
        await using var rig = await CreateScenarioAsync();
        using var source = new CancellationTokenSource();
        var pending = rig.Client.QueryBatchAsync([rig.Query(Key(), ContractOutcome.Delayed)], Window, source.Token);
        await rig.WaitForRequestAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(pending.IsCompleted, Is.False, "Cancellation must reach a genuinely pending real-client query.");
        source.Cancel();
        Assert.CatchAsync<OperationCanceledException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    /// <summary>Verifies client timeouts produce a failure with the original full key and safe timeout code.</summary>
    [Test]
    public async Task SinkClientContract_TimeoutIsKeyedFailure()
    {
        await using var rig = await CreateScenarioAsync();
        var results = await rig.Client.QueryBatchAsync([rig.Query(Key(), ContractOutcome.Timeout)], Window, default);
        Assert.That(results.Single(), Is.TypeOf<SignalResult.Failure>());
        var failure = (SignalResult.Failure)results.Single();
        Assert.That(failure.Key, Is.EqualTo(Key()));
        Assert.That(failure.Code, Is.EqualTo("timeout"));
        Assert.That(failure.Message, Does.Not.Contain("secret"));
    }
}
