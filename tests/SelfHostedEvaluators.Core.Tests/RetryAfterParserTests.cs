using SelfHostedEvaluators.Core.Reporting;

namespace SelfHostedEvaluators.Core.Tests;

public class RetryAfterParserTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-30T12:00:00Z");

    [Test]
    public void RetryAfterDeltaSecondsIsHonored() =>
        Assert.That(RetryAfterParser.Parse("10", Now), Is.EqualTo(TimeSpan.FromSeconds(10)));

    [TestCase("Wed, 30 Sep 2026 12:00:10 GMT", 10)]
    [TestCase("Wed, 30 Sep 2026 11:59:00 GMT", 0)]
    [TestCase("0", 0)]
    [TestCase(" 120 ", 120)]
    public void HttpDateAndNonnegativeDeltaAreSupported(string value, int seconds) =>
        Assert.That(RetryAfterParser.Parse(value, Now), Is.EqualTo(TimeSpan.FromSeconds(seconds)));

    [TestCase("-1")]
    [TestCase("1.5")]
    [TestCase("NaN")]
    [TestCase("")]
    [TestCase("tomorrow")]
    [TestCase("999999999999999999999999")]
    [TestCase("2026-09-30T12:00:10Z")]
    public void MalformedHeadersAreNotMistakenForAValidWait(string value) =>
        Assert.That(RetryAfterParser.Parse(value, Now), Is.Null);
}
