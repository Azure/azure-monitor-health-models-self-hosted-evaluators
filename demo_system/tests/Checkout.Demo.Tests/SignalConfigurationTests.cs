using System.Text;
using System.Text.Json;
using SelfHostedEvaluators.Core.Auth;
using SelfHostedEvaluators.Core.Configuration;
using SelfHostedEvaluators.Core.Evaluation;
using SelfHostedEvaluators.Plugins.Abstractions;
using SelfHostedEvaluators.Plugins.AzureMonitor;
using static Checkout.Demo.Tests.InfrastructureTools;

namespace Checkout.Demo.Tests;

[TestFixture]
public sealed class SignalConfigurationTests
{
    internal static IReadOnlyList<SignalConfiguration> LoadSignals()
    {
        var file = Path.Combine(DemoDirectory, "config", "signals.json");
        Assert.That(File.Exists(file), Is.True, "Missing authoritative evaluator signals.");
        using var signals = JsonDocument.Parse(File.ReadAllText(file));
        Assert.That(signals.RootElement.ValueKind, Is.EqualTo(JsonValueKind.Array));
        var environment = new Dictionary<string, string>
        {
            ["SHE_PLUGIN_DEMO"] = """
                {"schemaVersion":1,"name":"azure-monitor-demo","package":"azure-monitor",
                 "endpoints":{"default":"https://example.prometheus.monitor.azure.com"},
                 "auth":{"type":"entraId","azure":{"type":"managedIdentity"},"scope":"https://prometheus.monitor.azure.com/.default"}}
                """,
            ["SHE_MODEL_DEMO"] = $$"""
                {"schemaVersion":1,"name":"checkout-demo",
                 "healthModel":{"dataPlaneUri":"https://example.data.cloudhealth.azure.com","apiVersion":"2026-10-01-preview"},
                 "signals":{{signals.RootElement.GetRawText()}}}
                """
        };
        return new EnvironmentConfigSource().Load(environment).Models.Single().Signals;
    }

    [TestCase("p95-latency", 500, HealthState.Healthy)]
    [TestCase("p95-latency", 501, HealthState.Degraded)]
    [TestCase("p95-latency", 1000, HealthState.Degraded)]
    [TestCase("p95-latency", 1001, HealthState.Unhealthy)]
    [TestCase("checkout-error-rate", 5, HealthState.Healthy)]
    [TestCase("checkout-error-rate", 5.1, HealthState.Degraded)]
    [TestCase("checkout-error-rate", 10, HealthState.Degraded)]
    [TestCase("checkout-error-rate", 10.1, HealthState.Unhealthy)]
    [TestCase("availability", 1, HealthState.Healthy)]
    [TestCase("availability", 0, HealthState.Unhealthy)]
    [TestCase("traffic-freshness", 30, HealthState.Healthy)]
    [TestCase("traffic-freshness", 31, HealthState.Degraded)]
    [TestCase("traffic-freshness", 60, HealthState.Degraded)]
    [TestCase("traffic-freshness", 61, HealthState.Unhealthy)]
    public void RealEvaluatorAppliesStrictThresholdBoundaries(string name, double value, HealthState expected)
    {
        var signal = LoadSignals().Single(s => s.Name == name);
        var key = new SignalKey(new Uri("https://example.data.cloudhealth.azure.com"), signal.Entity, signal.Name);
        var result = new Evaluator(new Redactor()).Evaluate(key, signal,
            new SignalResult.Observation(key, value, null, DateTimeOffset.UnixEpoch));
        Assert.That(result.State, Is.EqualTo(expected));
    }

    [TestCase("p95-latency")]
    [TestCase("checkout-error-rate")]
    [TestCase("availability")]
    [TestCase("traffic-freshness")]
    public void MissingAndNonfiniteObservationsNeverBecomeHealthy(string name)
    {
        var signal = LoadSignals().Single(s => s.Name == name);
        var key = new SignalKey(new Uri("https://example.data.cloudhealth.azure.com"), signal.Entity, signal.Name);
        var evaluator = new Evaluator(new Redactor());
        Assert.That(evaluator.Evaluate(key, signal, new SignalResult.NoData(key, "No matching telemetry.")).State, Is.EqualTo(HealthState.Unknown));
        Assert.That(evaluator.Evaluate(key, signal,
            new SignalResult.Observation(key, double.NaN, null, DateTimeOffset.UnixEpoch)).State, Is.EqualTo(HealthState.Unknown));
    }
}
