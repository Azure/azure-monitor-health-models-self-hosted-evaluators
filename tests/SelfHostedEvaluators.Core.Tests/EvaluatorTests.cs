using System.Globalization;
using SelfHostedEvaluators.Core.Auth;
using SelfHostedEvaluators.Core.Configuration;
using SelfHostedEvaluators.Core.Evaluation;
using SelfHostedEvaluators.Plugins.Abstractions;

namespace SelfHostedEvaluators.Core.Tests;

public class EvaluatorTests
{
    [TestCase("LessThan", 9, HealthState.Unhealthy)]
    [TestCase("LessThan", 10, HealthState.Healthy)]
    [TestCase("LessThan", 11, HealthState.Healthy)]
    [TestCase("LessThanOrEqual", 9, HealthState.Unhealthy)]
    [TestCase("LessThanOrEqual", 10, HealthState.Unhealthy)]
    [TestCase("LessThanOrEqual", 11, HealthState.Healthy)]
    [TestCase("GreaterThan", 9, HealthState.Healthy)]
    [TestCase("GreaterThan", 10, HealthState.Healthy)]
    [TestCase("GreaterThan", 11, HealthState.Unhealthy)]
    [TestCase("GreaterThanOrEqual", 9, HealthState.Healthy)]
    [TestCase("GreaterThanOrEqual", 10, HealthState.Unhealthy)]
    [TestCase("GreaterThanOrEqual", 11, HealthState.Unhealthy)]
    [TestCase("Equal", 9, HealthState.Healthy)]
    [TestCase("Equal", 10, HealthState.Unhealthy)]
    [TestCase("Equal", 11, HealthState.Healthy)]
    [TestCase("NotEqual", 9, HealthState.Unhealthy)]
    [TestCase("NotEqual", 10, HealthState.Healthy)]
    [TestCase("NotEqual", 11, HealthState.Unhealthy)]
    public void EveryThresholdOperatorHonorsItsBoundary(string op, double value, HealthState expected)
    {
        var config = PollingTestSupport.Snapshot(evaluation:
            $$$"""{"unhealthy":{"operator":"{{{op}}}","threshold":10}}""");
        var key = PollingTestSupport.Batch(config).Queries[0].Key;
        var result = new Evaluator(new Redactor()).Evaluate(key, config.Models[0].Signals[0],
            new SignalResult.Observation(key, value, null, PollingTestSupport.Start));
        Assert.That(result.State, Is.EqualTo(expected));
        Assert.That(result.Value, Is.EqualTo(value));
        Assert.That(result.EvaluationRules!.UnhealthyRule.Operator.ToString(), Is.EqualTo(op));
        Assert.That(result.ExpiresInMinutes, Is.EqualTo(1));
    }

    [TestCase(4, HealthState.Healthy)]
    [TestCase(5, HealthState.Degraded)]
    [TestCase(10, HealthState.Unhealthy)]
    public void UnhealthyTakesPrecedenceOverDegraded(double value, HealthState expected)
    {
        var config = PollingTestSupport.Snapshot(evaluation:
            """{"degraded":{"operator":"GreaterThanOrEqual","threshold":5},"unhealthy":{"operator":"GreaterThanOrEqual","threshold":10}}""");
        Assert.That(Evaluate(config, value).State, Is.EqualTo(expected));
        Assert.That(Evaluate(config, value).EvaluationRules!.DegradedRule!.Threshold, Is.EqualTo(5));
    }

    [TestCase("oK", HealthState.Healthy)]
    [TestCase("WARN", HealthState.Degraded)]
    [TestCase("alert", HealthState.Unhealthy)]
    [TestCase("No Data", HealthState.Unknown)]
    [TestCase("unmapped", HealthState.Unknown)]
    public void NativeMapsAreCaseInsensitive(string native, HealthState expected)
    {
        var config = PollingTestSupport.Snapshot(evaluation:
            """{"stateMap":{"OK":"Healthy","Warn":"Degraded","Alert":"Unhealthy","No Data":"Unknown"}}""");
        var result = Evaluate(config, null, native);
        Assert.That(result.State, Is.EqualTo(expected));
        Assert.That(result.EvaluationRules, Is.Null);
    }

    [TestCase(null, HealthState.Unknown)]
    [TestCase("Healthy", HealthState.Healthy)]
    [TestCase("Degraded", HealthState.Degraded)]
    [TestCase("Unhealthy", HealthState.Unhealthy)]
    [TestCase("Unknown", HealthState.Unknown)]
    public void NoDataUsesOnlyConfiguredNoDataState(string? noData, HealthState expected)
    {
        var config = PollingTestSupport.Snapshot(evaluation: noData is null
            ? """{"unhealthy":{"operator":"GreaterThan","threshold":10}}"""
            : $$$"""{"noData":"{{{noData}}}","unhealthy":{"operator":"GreaterThan","threshold":10}}""");
        var key = PollingTestSupport.Batch(config).Queries[0].Key;
        var result = new Evaluator(new Redactor()).Evaluate(key, config.Models[0].Signals[0],
            new SignalResult.NoData(key, "no samples"));
        Assert.That(result.State, Is.EqualTo(expected));
        Assert.That(result.Value, Is.Null);
        Assert.That(result.AdditionalContext, Is.EqualTo("no samples"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void MissingRequiredObservationInputIsFailureNotNoData(bool stateMap)
    {
        var config = PollingTestSupport.Snapshot(evaluation: stateMap
            ? """{"noData":"Healthy","stateMap":{"OK":"Healthy"}}"""
            : """{"noData":"Healthy","unhealthy":{"operator":"GreaterThan","threshold":10}}""");
        var result = Evaluate(config, stateMap ? 5 : null);
        Assert.That(result.State, Is.EqualTo(HealthState.Unknown));
        Assert.That(result.AdditionalContext, Does.StartWith("Poll failed: missing_"));
    }

    [TestCase(double.NaN)]
    [TestCase(double.PositiveInfinity)]
    [TestCase(double.NegativeInfinity)]
    public void NonfiniteValueFailsEvenForMappedState(double value)
    {
        var config = PollingTestSupport.Snapshot(evaluation: """{"stateMap":{"OK":"Healthy"}}""");
        var result = Evaluate(config, value, "OK");
        Assert.That(result.State, Is.EqualTo(HealthState.Unknown));
        Assert.That(result.Value, Is.Null);
    }

    [Test]
    public void TemplatesUseInvariantSinglePassSubstitutionAndObservationTime()
    {
        using var culture = new CultureScope("fr-FR");
        var config = PollingTestSupport.Snapshot(context:
            "{{value}}|{{state}}|{{healthState}}|{{detail}}|{{observedAt}}");
        var result = Evaluate(config, 1.5, "OK", "{{value}}");
        Assert.That(result.AdditionalContext,
            Is.EqualTo("1.5|OK|Healthy|{{value}}|2026-01-01T00:00:00.0000000+00:00"));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void FailureAndSuccessfulContextRedactBeforeTruncation(bool failure)
    {
        var secret = new string('s', 5000);
        var redactor = new Redactor();
        redactor.RegisterSecret(secret);
        var config = PollingTestSupport.Snapshot(context: "{{detail}}");
        var key = PollingTestSupport.Batch(config).Queries[0].Key;
        var text = secret + " Authorization: Bearer dangerous\nhttps://sink.test/path?token=hidden " + new string('x', 5000);
        SignalResult raw = failure ? new SignalResult.Failure(key, "sink_error", text)
            : new SignalResult.Observation(key, 1, null, PollingTestSupport.Start, text);
        var result = new Evaluator(redactor).Evaluate(key, config.Models[0].Signals[0], raw);
        Assert.That(result.AdditionalContext, Has.Length.EqualTo(4096));
        Assert.That(result.AdditionalContext, Does.Contain("[REDACTED]").And.Not.Contain("sss")
            .And.Not.Contain("dangerous").And.Not.Contain("hidden"));
        if (failure)
        {
            Assert.That(result.State, Is.EqualTo(HealthState.Unknown));
            Assert.That(result.AdditionalContext, Does.StartWith("Poll failed: sink_error: "));
            Assert.That(result.Value, Is.Null);
        }
    }

    [Test]
    public void DifferentFullKeyOrNullResultCannotEvaluateAsHealthy()
    {
        var config = PollingTestSupport.Snapshot();
        var key = PollingTestSupport.Batch(config).Queries[0].Key;
        var evaluator = new Evaluator(new Redactor());
        Assert.That(evaluator.Evaluate(key, config.Models[0].Signals[0], null!).State, Is.EqualTo(HealthState.Unknown));
        Assert.That(evaluator.Evaluate(key, config.Models[0].Signals[0],
            new SignalResult.Observation(key with { ModelUri = new("https://foreign.test") }, 1, null, PollingTestSupport.Start)).State,
            Is.EqualTo(HealthState.Unknown));
    }

    private static EvaluatedSignal Evaluate(ConfigSnapshot config, double? value, string? state = null, string? detail = null)
    {
        var key = PollingTestSupport.Batch(config).Queries[0].Key;
        return new Evaluator(new Redactor()).Evaluate(key, config.Models[0].Signals[0],
            new SignalResult.Observation(key, value, state, PollingTestSupport.Start, detail));
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo original = CultureInfo.CurrentCulture;
        public CultureScope(string name) => CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
        public void Dispose() => CultureInfo.CurrentCulture = original;
    }
}
