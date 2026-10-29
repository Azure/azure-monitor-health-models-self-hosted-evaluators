using System.Runtime.Loader;
using System.Text.Json;
using SelfHostedEvaluators.Core.Configuration;
using SelfHostedEvaluators.Core.Packages;
using SelfHostedEvaluators.Plugins.Abstractions;

namespace SelfHostedEvaluators.Core.Tests;

public class PluginManagerTests
{
    [Test]
    public async Task PackagesKeepPrivateDependenciesAndEachEntryGetsItsOwnClient()
    {
        using var fixture = new PackageFixture();
        fixture.Add();
        fixture.Add(id: "two", fixture: "Two");
        using var catalog = PackageCatalog.Load(fixture.Root);
        var env = EnvironmentConfigTests.Environment();
        env.Add("SHE_PLUGIN_TWO", EnvironmentConfigTests.Plugin.Replace("sink-one", "sink-two").Replace("\"one\"", "\"two\""));
        var config = new EnvironmentConfigSource().Load(env);
        ConfigurationValidator.Validate(config, catalog, ConfigurationValidationMode.Deployment);
        var manager = new PluginManager(catalog, fixture.Context);
        await using var first = await manager.CreateClientAsync(config.Plugins[0], default);
        await using var second = await manager.CreateClientAsync(config.Plugins[0], default);
        await using var other = await manager.CreateClientAsync(config.Plugins[1], default);
        using var json = JsonDocument.Parse("{}");
        var query = new SignalQuery(new(new Uri("https://model.test"), "entity-one", "signal-one"), json.RootElement);
        var window = new QueryWindow(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        var a = (SignalResult.Observation)(await first.QueryBatchAsync([query], window, default))[0];
        var b = (SignalResult.Observation)(await second.QueryBatchAsync([query], window, default))[0];
        var c = (SignalResult.Observation)(await other.QueryBatchAsync([query], window, default))[0];
        Assert.Multiple(() =>
        {
            Assert.That(a.Value, Is.EqualTo(11));
            Assert.That(c.Value, Is.EqualTo(22));
            Assert.That(a.Detail, Is.Not.EqualTo(b.Detail));
            Assert.That(a.Detail, Does.EndWith(":ConcurrencyLimiter"));
            Assert.That(a.NativeState, Is.EqualTo("sink-one"));
            Assert.That(c.NativeState, Is.EqualTo("sink-two"));
        });
    }

    [Test]
    public async Task MissingPrivateDependencyDoesNotUseHostCopy()
    {
        using var fixture = new PackageFixture();
        var one = fixture.Add();
        var two = fixture.Add(id: "two", fixture: "Two");
        using (var stream = File.OpenRead(Path.Combine(two, "Fixture.PrivateDependency.dll")))
            AssemblyLoadContext.Default.LoadFromStream(stream);
        File.Delete(Path.Combine(one, "Fixture.PrivateDependency.dll"));
        using var catalog = PackageCatalog.Load(fixture.Root);
        var configuration = new EnvironmentConfigSource().Load(EnvironmentConfigTests.Environment(
            EnvironmentConfigTests.Plugin)).Plugins[0];
        await using var client = await new PluginManager(catalog, fixture.Context).CreateClientAsync(configuration, default);
        using var json = JsonDocument.Parse("{}");
        var query = new SignalQuery(new(new Uri("https://model.test"), "entity-one", "signal-one"), json.RootElement);
        Assert.ThrowsAsync<FileNotFoundException>(async () =>
            await client.QueryBatchAsync([query], new(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1)), default));
    }

    [Test]
    public void ContextFactoryFailureAndMissingPackagesAreNotSilent()
    {
        using var fixture = new PackageFixture();
        fixture.Add();
        using var catalog = PackageCatalog.Load(fixture.Root);
        var manager = new PluginManager(catalog, (_, _) => throw new InvalidOperationException("sensitive"));
        var configuration = new EnvironmentConfigSource().Load(EnvironmentConfigTests.Environment()).Plugins[0];
        var error = Assert.ThrowsAsync<ConfigurationException>(async () => await manager.CreateClientAsync(configuration, default));
        Assert.That(error!.Message, Does.Contain("SHE_PLUGIN_ONE").And.Not.Contain("sensitive"));
        using var empty = PackageCatalog.Load(PackageFixture.EmptyDirectory());
        Assert.ThrowsAsync<ConfigurationException>(async () =>
            await new PluginManager(empty, fixture.Context).CreateClientAsync(configuration, default));
    }

    [Test]
    public void ActivationErrorsAreRedactedAndCancellationPropagates()
    {
        using var fixture = new PackageFixture();
        fixture.Add();
        using var catalog = PackageCatalog.Load(fixture.Root);
        var configuration = new EnvironmentConfigSource().Load(EnvironmentConfigTests.Environment(
            EnvironmentConfigTests.Plugin.Replace("\"package\":", "\"settings\":{\"createThrows\":true},\"package\":"))).Plugins[0];
        var manager = new PluginManager(catalog, fixture.Context);
        var error = Assert.ThrowsAsync<ConfigurationException>(async () => await manager.CreateClientAsync(configuration, default));
        Assert.That(error!.Message, Does.Not.Contain("sensitive"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAsync<OperationCanceledException>(async () => await manager.CreateClientAsync(configuration, cts.Token));
    }
}
