namespace Checkout.Demo.Tests;

[Parallelizable(ParallelScope.All)]
public class TrafficOptionsTests
{
    [Test]
    public void MissingConfigurationUsesFiveSecondsAndLocalApi()
    {
        var options = TrafficOptions.Parse(null, null);
        Assert.Multiple(() =>
        {
            Assert.That(options.Interval, Is.EqualTo(TimeSpan.FromSeconds(5)));
            Assert.That(options.ApiUrl, Is.EqualTo(new Uri("http://127.0.0.1:8080")));
        });
    }

    [TestCase("")]
    [TestCase("0")]
    [TestCase("-1")]
    [TestCase("0.5")]
    [TestCase("1.5")]
    [TestCase("oops")]
    [TestCase("2147483648")]
    [TestCase("4294968")]
    public void InvalidIntervalConfigurationFailsBeforeStarting(string value)
    {
        Assert.That(() => TrafficOptions.Parse(value, null), Throws.InstanceOf<ArgumentException>());
    }

    [TestCase("ftp://localhost")]
    [TestCase("file:///somewhere")]
    [TestCase("not-a-url")]
    [TestCase("")]
    [TestCase("http://user:password@localhost")]
    [TestCase("http://localhost?query=yes")]
    [TestCase("http://localhost#fragment")]
    public void InvalidApiUrlFailsAtConfigurationBoundary(string value)
    {
        Assert.That(() => TrafficOptions.Parse(null, value), Throws.InstanceOf<ArgumentException>());
    }

    [TestCase("http://127.0.0.1:9999/")]
    [TestCase("https://checkout.internal/")]
    public void ExplicitHttpApiAndMinimumIntervalAreAccepted(string url)
    {
        var options = TrafficOptions.Parse("1", url);
        Assert.Multiple(() =>
        {
            Assert.That(options.Interval, Is.EqualTo(TimeSpan.FromSeconds(1)));
            Assert.That(options.ApiUrl, Is.EqualTo(new Uri(url)));
        });
    }
}
