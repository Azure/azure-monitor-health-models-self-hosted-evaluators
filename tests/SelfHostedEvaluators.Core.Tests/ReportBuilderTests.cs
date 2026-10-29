using System.Text;
using System.Text.Json;
using SelfHostedEvaluators.Core.Auth;
using SelfHostedEvaluators.Core.Configuration;
using SelfHostedEvaluators.Core.Evaluation;
using SelfHostedEvaluators.Core.Reporting;

namespace SelfHostedEvaluators.Core.Tests;

public class ReportBuilderTests
{
    private static EvaluatedSignal Signal(string? context = null) => new(
        new(new("https://model.test"), "entity-one", "p95-latency"), HealthState.Degraded, 742.5,
        new(new(RuleOperator.GreaterThan, 1000), new(RuleOperator.GreaterThan, 500)), 6, context);

    [Test]
    public void BodyUsesOnlyTheDataPlaneFieldsAndEffectiveTtl()
    {
        var time = PollingTestSupport.Clock();
        var report = new ReportBuilder(time, new()).Build(Signal());
        Assert.That(Encoding.UTF8.GetString(report.Body.Span), Is.EqualTo(
            """{"signalName":"p95-latency","healthState":"Degraded","value":742.5,"evaluationRules":{"unhealthyRule":{"operator":"GreaterThan","threshold":1000},"degradedRule":{"operator":"GreaterThan","threshold":500}},"expiresInMinutes":6}"""));
        Assert.That(report.Key, Is.EqualTo(Signal().Key));
        Assert.That(report.CreatedAt, Is.EqualTo(PollingTestSupport.Start));
        Assert.That(report.ExpiresAt, Is.EqualTo(PollingTestSupport.Start.AddMinutes(6)));
        Assert.That(Guid.TryParse(report.ClientRequestId, out _), Is.True);
    }

    [Test]
    public void BriefOverloadDerivesTtlAndOmitsOptionalFields()
    {
        var report = new ReportBuilder(PollingTestSupport.Clock(), new()).Build(
            Signal() with { State = HealthState.Unknown, Value = null, EvaluationRules = null },
            TimeSpan.FromSeconds(90), 3, null);
        Assert.That(Encoding.UTF8.GetString(report.Body.Span), Is.EqualTo(
            """{"signalName":"p95-latency","healthState":"Unknown","expiresInMinutes":5}"""));
    }

    [TestCase("ab")]
    [TestCase("-abc")]
    [TestCase("abc-")]
    [TestCase("abc\n")]
    [TestCase("a_b")]
    [TestCase("abc/def")]
    public void InvalidEntityOrSignalNameCannotReachTheWire(string invalid)
    {
        var builder = new ReportBuilder(PollingTestSupport.Clock(), new());
        Assert.Throws<ArgumentException>(() => builder.Build(Signal() with { Key = Signal().Key with { Name = invalid } }));
        Assert.Throws<ArgumentException>(() => builder.Build(Signal() with { Key = Signal().Key with { Entity = invalid } }));
    }

    [TestCase(3, true)]
    [TestCase(260, true)]
    [TestCase(261, false)]
    public void NamesHaveExactLengthBounds(int length, bool valid)
    {
        var builder = new ReportBuilder(PollingTestSupport.Clock(), new());
        var signal = Signal() with { Key = Signal().Key with { Name = new('a', length) } };
        if (valid) Assert.DoesNotThrow(() => builder.Build(signal));
        else Assert.Throws<ArgumentException>(() => builder.Build(signal));
    }

    [TestCase(double.NaN)]
    [TestCase(double.PositiveInfinity)]
    [TestCase(double.NegativeInfinity)]
    public void NonfiniteValuesAndThresholdsAreRejected(double value)
    {
        var builder = new ReportBuilder(PollingTestSupport.Clock(), new());
        Assert.Throws<ArgumentException>(() => builder.Build(Signal() with { Value = value }));
        Assert.Throws<ArgumentException>(() => builder.Build(Signal() with { EvaluationRules = new(new(RuleOperator.Equal, value), null) }));
        Assert.Throws<ArgumentException>(() => builder.Build(Signal() with
            { EvaluationRules = new(new(RuleOperator.Equal, 1), new(RuleOperator.Equal, value)) }));
    }

    [TestCase(0)]
    [TestCase(10081)]
    public void InvalidEffectiveTtlIsRejected(int ttl) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReportBuilder(PollingTestSupport.Clock(), new())
            .Build(Signal() with { ExpiresInMinutes = ttl }));

    [Test]
    public void InvalidStateOrDynamicLikeRuleCannotBeSerialized()
    {
        var builder = new ReportBuilder(PollingTestSupport.Clock(), new());
        Assert.Throws<ArgumentException>(() => builder.Build(Signal() with { State = (HealthState)99 }));
        Assert.Throws<ArgumentException>(() => builder.Build(Signal() with { EvaluationRules = new(new((RuleOperator)99, 1), null) }));
        Assert.Throws<ArgumentException>(() => builder.Build(Signal() with { EvaluationRules = new(null!, new(RuleOperator.Equal, 1)) }));
    }

    [Test]
    public void ContextIsRedactedBeforeTruncationAndUtf8IsBoundedWithoutBom()
    {
        var redactor = new Redactor();
        redactor.RegisterSecret(new('x', 5000));
        var report = new ReportBuilder(PollingTestSupport.Clock(), redactor)
            .Build(Signal(new string('x', 5000) + new string('\u0001', 5000)));
        using var json = JsonDocument.Parse(report.Body);
        var context = json.RootElement.GetProperty("additionalContext").GetString()!;
        Assert.That(context, Does.StartWith("[REDACTED]"));
        Assert.That(context, Has.Length.EqualTo(4096));
        Assert.That(report.Body.Length, Is.LessThanOrEqualTo(32768));
        Assert.That(report.Body.Span[0], Is.EqualTo((byte)'{'));
    }

    [Test]
    public void ContextTruncationDoesNotSplitASurrogatePair()
    {
        var report = new ReportBuilder(PollingTestSupport.Clock(), new()).Build(Signal(new string('a', 4095) + "😀"));
        using var body = JsonDocument.Parse(report.Body);
        Assert.That(body.RootElement.GetProperty("additionalContext").GetString(), Is.EqualTo(new string('a', 4095)));
    }
}
