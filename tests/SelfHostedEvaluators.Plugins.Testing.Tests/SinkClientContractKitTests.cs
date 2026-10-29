using SelfHostedEvaluators.Plugins.Testing;

namespace SelfHostedEvaluators.Plugins.Testing.Tests;

public class SinkClientContractKitTests
{
    [Test]
    public async Task ContractClientRejectsAnotherFixturesHttpsCertificate()
    {
        await using var first = new PluginTestContext();
        await using var second = new PluginTestContext();
        using var client = first.Http(first.Api, "Datadog");
        PluginTestContext.Stub(second.Api, "/probe", "not this fixture");
        Assert.ThrowsAsync<HttpRequestException>(async () => await client.GetAsync(second.Api.Url + "/probe"));
    }

    [Test]
    public async Task SinkClientContractTestContextHasIsolatedHttpsServersAndPreauthenticatedClients()
    {
        await using var rig = new PluginTestContext();
        using var api = rig.Http(rig.Api, "Datadog");
        using var stream = rig.Http(rig.Stream, "Splunk", "us");
        PluginTestContext.Stub(rig.Api, "/probe", "one");
        PluginTestContext.Stub(rig.Stream, "/probe", "two", 429);
        using var first = await api.GetAsync("probe");
        using var second = await stream.GetAsync("probe");
        Assert.That(await first.Content.ReadAsStringAsync(), Is.EqualTo("one"));
        Assert.That((int)second.StatusCode, Is.EqualTo(429));
        Assert.That(second.Headers.RetryAfter!.Delta, Is.EqualTo(TimeSpan.FromSeconds(7)));
        Assert.That(api.BaseAddress!.Scheme, Is.EqualTo("https"));
        Assert.That(stream.BaseAddress!.Scheme, Is.EqualTo("https"));
        Assert.That(api.BaseAddress, Is.Not.EqualTo(stream.BaseAddress));
        Assert.That(rig.Api.LogEntries.Single().RequestMessage!.Headers!["DD-API-KEY"].Single(), Is.EqualTo("eu-api"));
        Assert.That(rig.Stream.LogEntries.Single().RequestMessage!.Headers!["X-SF-Token"].Single(), Is.EqualTo("us-token"));
    }

    [Test]
    public async Task SinkClientContractTestContextFailsIfSinkTriesToReauthenticateOrDisposeAuth()
    {
        await using var rig = new PluginTestContext();
        var context = rig.Context([]);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://localhost");
        Assert.ThrowsAsync<InvalidOperationException>(async () => await context.Auth.AuthenticateAsync(request, default));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await context.Auth.DisposeAsync());
    }
}
