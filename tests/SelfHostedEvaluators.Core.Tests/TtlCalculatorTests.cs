using SelfHostedEvaluators.Core.Configuration;
using SelfHostedEvaluators.Core.Evaluation;

namespace SelfHostedEvaluators.Core.Tests;

public class TtlCalculatorTests
{
    [Test]
    public void TwoMinuteIntervalWithDefaultMultiplierHasSixMinuteTtl() =>
        Assert.That(TtlCalculator.Calculate(TimeSpan.FromMinutes(2), 3, null), Is.EqualTo(6));

    [TestCase(10, 3, null, 1)]
    [TestCase(61, 3, null, 4)]
    [TestCase(120, 2.5, null, 5)]
    [TestCase(120, 3.0000000000000004, null, 7)]
    [TestCase(120, 2.9999999999999996, null, 6)]
    [TestCase(200, 2.1, null, 7)]
    [TestCase(200, 2.7, null, 9)]
    [TestCase(100, 4.2, null, 7)]
    [TestCase(86400, 100, null, 10080)]
    [TestCase(120, 3, 4, 4)]
    [TestCase(10, 0.1, null, 1)]
    [TestCase(10, double.Epsilon, null, 1)]
    [TestCase(10, double.MaxValue, null, 10080)]
    [SetCulture("fr-FR")]
    public void RoundsClampsAndHonorsExplicitOverride(int seconds, double multiplier, int? minutes, int expected) =>
        Assert.That(TtlCalculator.Calculate(TimeSpan.FromSeconds(seconds), multiplier, minutes), Is.EqualTo(expected));

    [TestCase(31, 1, null)]
    [TestCase(61, 3, 2)]
    [TestCase(120, 3, 0)]
    [TestCase(120, 3, 10081)]
    [TestCase(120, 0, null)]
    [TestCase(120, double.NaN, null)]
    [TestCase(120, double.PositiveInfinity, null)]
    [TestCase(0, 3, null)]
    [TestCase(-1, 3, null)]
    [TestCase(60, double.Epsilon, null)]
    public void RejectsInvalidOrShorterThanTwiceInterval(int seconds, double multiplier, int? minutes) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TtlCalculator.Calculate(TimeSpan.FromSeconds(seconds), multiplier, minutes));

    [Test]
    public void ParserAndRuntimeShareExactTickBoundaryValidation()
    {
        Assert.That(TtlCalculator.Calculate(TimeSpan.FromTicks(TimeSpan.TicksPerMinute / 2), 2, 1), Is.EqualTo(1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TtlCalculator.Calculate(TimeSpan.FromTicks(TimeSpan.TicksPerMinute / 2 + 1), 2, 1));
        var env = EnvironmentConfigTests.Environment(model:
            EnvironmentConfigTests.Model.Replace("\"query\":{}", "\"interval\":\"61s\",\"expiresInMinutes\":2,\"query\":{}"));
        Assert.Throws<ConfigurationException>(() => new EnvironmentConfigSource().Load(env));
    }

    [Test]
    public void FractionalMultiplierDoesNotRoundAnExactSevenMinutesUpToEight()
    {
        var env = EnvironmentConfigTests.Environment(model: EnvironmentConfigTests.Model.Replace(
            "\"query\":{}", "\"interval\":\"200s\",\"ttlMultiplier\":2.1,\"query\":{}"));
        Assert.That(new EnvironmentConfigSource().Load(env).Models[0].Signals[0].ExpiresInMinutes, Is.EqualTo(7));
    }

    [TestCase("2m", "3.0000000000000004", 7)]
    [TestCase("2m", "2.9999999999999996", 6)]
    [TestCase("10s", "5e-324", 1)]
    [TestCase("1m", "1.7976931348623157e308", 10080)]
    [SetCulture("fr-FR")]
    public void ParserPreservesRoundTripMultiplierSemantics(string interval, string multiplier, int expected)
    {
        var env = EnvironmentConfigTests.Environment(model: EnvironmentConfigTests.Model.Replace(
            "\"query\":{}", $"\"interval\":\"{interval}\",\"ttlMultiplier\":{multiplier},\"query\":{{}}"));
        Assert.That(new EnvironmentConfigSource().Load(env).Models[0].Signals[0].ExpiresInMinutes, Is.EqualTo(expected));
    }

    [Test]
    public void ExtremeFiniteIntervalsAndMultipliersAreClampedOrSafelyRejected()
    {
        Assert.That(TtlCalculator.Calculate(TimeSpan.FromTicks(1), double.MaxValue, null), Is.EqualTo(10080));
        Assert.That(TtlCalculator.Calculate(TimeSpan.FromTicks(1), double.Epsilon, null), Is.EqualTo(1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TtlCalculator.Calculate(TimeSpan.MaxValue, double.MaxValue, null));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TtlCalculator.Calculate(TimeSpan.MaxValue, double.Epsilon, null));
    }
}
