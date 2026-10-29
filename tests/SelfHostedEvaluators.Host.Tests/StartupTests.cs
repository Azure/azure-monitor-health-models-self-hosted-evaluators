namespace SelfHostedEvaluators.Host.Tests;

[NonParallelizable]
public class StartupTests
{
    [Test]
    public async Task ValidateAcceptsBundledSnapshotWithoutIdentityOrNetwork()
    {
        using var fixture = new HostFixture();
        var result = await fixture.Cli("validate");
        Assert.That(result.Code, Is.Zero, result.Output);
    }

    [Test]
    public async Task ValidateRejectsMalformedEnvironmentWithoutEchoingIt()
    {
        using var fixture = new HostFixture();
        fixture.Environment["SHE_PLUGIN_STATIC"] = "{sensitive-payload";
        var result = await fixture.Run("validate");
        Assert.Multiple(() =>
        {
            Assert.That(result.Code, Is.Not.Zero);
            Assert.That(result.Output, Does.Not.Contain("sensitive-payload"));
            Assert.That(result.Output, Does.Contain("SHE_PLUGIN_STATIC:$"));
        });
    }

    [TestCase("validate")]
    [TestCase("run")]
    public async Task CliReportsEachConfigurationLocationToStderrWithoutPayload(string command)
    {
        using var fixture = new HostFixture();
        fixture.Environment["SHE_PLUGIN_STATIC"] = fixture.Environment["SHE_PLUGIN_STATIC"]
            .Replace("\"type\":\"none\"", "\"type\":\"bearer\",\"token\":\"sensitive-payload\"");
        fixture.Environment["SHE_MODEL_ONE"] = fixture.Environment["SHE_MODEL_ONE"]
            .Replace("\"schemaVersion\":1", "\"schemaVersion\":99");
        var result = await fixture.Run(command);
        Assert.Multiple(() =>
        {
            Assert.That(result.Code, Is.Not.Zero);
            Assert.That(result.Error, Does.Contain("SHE_PLUGIN_STATIC:$.auth.token"));
            Assert.That(result.Error, Does.Contain("SHE_MODEL_ONE:$.schemaVersion"));
            Assert.That(result.Output, Does.Not.Contain("sensitive-payload"));
        });
    }

    [TestCase("validate")]
    [TestCase("run")]
    public async Task CliDoesNotLogUntrustedUnknownPropertyNamesOrControlCharacters(string command)
    {
        using var fixture = new HostFixture();
        fixture.Environment["SHE_PLUGIN_STATIC"] = """{"private-payload\nforged-log":42}""";
        var result = await fixture.Run(command);
        Assert.That(result.Code, Is.Not.Zero);
        Assert.That(result.Error, Does.Contain("SHE_PLUGIN_STATIC:$").And.Not.Contain("private-payload").And.Not.Contain("forged-log"));
    }

    [Test]
    public async Task InvalidSnapshotNeverStartsWorker()
    {
        using var fixture = new HostFixture();
        fixture.Environment["SHE_PLUGIN_STATIC"] = "{}";
        var result = await fixture.Run("run");
        Assert.Multiple(() =>
        {
            Assert.That(result.Code, Is.Not.Zero);
            Assert.That(result.Output, Does.Not.Contain("state changed"));
            Assert.That(result.Output, Does.Not.Contain("NotSupportedException"));
        });
    }

    [Test]
    public async Task ValidateRejectsKeyOnlyTelemetryWithoutConstructingCredentials()
    {
        using var fixture = new HostFixture();
        using var offline = new OfflineRuntime();
        fixture.Environment["APPLICATIONINSIGHTS_CONNECTION_STRING"] =
            "InstrumentationKey=22222222-2222-2222-2222-222222222222;IngestionEndpoint=https://monitor.test/";
        var code = await new HostRunner(offline.Dependencies(fixture)).RunAsync(["validate"], CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(code, Is.Not.Zero);
            Assert.That(offline.Credentials.Constructions, Is.Zero);
            Assert.That(offline.Transport.Requests, Is.Empty);
        });
    }

    [Test]
    public async Task ValidateDoesNotConstructAuthSecretsTransportsOrTelemetry()
    {
        using var fixture = new HostFixture();
        using var offline = new OfflineRuntime();
        fixture.Environment["SHE_PLUGIN_STATIC"] = fixture.Environment["SHE_PLUGIN_STATIC"]
            .Replace("\"type\":\"none\"", "\"type\":\"bearer\",\"token\":\"kv://vault-one/token\"");
        var code = await new HostRunner(offline.Dependencies(fixture)).RunAsync(["validate"], CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(code, Is.Zero);
            Assert.That(offline.SecretSources, Is.Zero);
            Assert.That(offline.Credentials.Constructions, Is.Zero);
            Assert.That(offline.Credentials.Tokens, Is.Zero);
            Assert.That(offline.Transport.Requests, Is.Empty);
            Assert.That(offline.Traces.Activities, Is.Empty);
        });
    }

    [TestCase("{}")]
    [TestCase("""{"kind":"unrecognized"}""")]
    public async Task InvalidSnapshotOrQueryStartsNoClients(string query)
    {
        using var fixture = new HostFixture();
        using var offline = new OfflineRuntime();
        fixture.Environment["SHE_MODEL_ONE"] = fixture.Environment["SHE_MODEL_ONE"].Replace("""{"kind":"observation","value":5}""", query);
        var code = await new HostRunner(offline.Dependencies(fixture)).RunAsync([], CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(code, Is.Not.Zero);
            Assert.That(offline.SecretSources, Is.Zero);
            Assert.That(offline.Credentials.Tokens, Is.Zero);
            Assert.That(offline.Transport.Requests, Is.Empty);
        });
    }

    [Test]
    public async Task RequiredClientFailureStartsNoPollsOrChecks()
    {
        using var fixture = new HostFixture();
        using var offline = new OfflineRuntime();
        fixture.Environment["SHE_PLUGIN_STATIC"] = fixture.Environment["SHE_PLUGIN_STATIC"]
            .Replace("\"type\":\"none\"", "\"type\":\"bearer\",\"token\":\"kv://vault-one/token\"");
        var code = await new HostRunner(offline.Dependencies(fixture)).RunAsync(["run"], CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(code, Is.Not.Zero);
            Assert.That(offline.Transport.Requests, Is.Empty);
            Assert.That(offline.Logs.Entries.Any(e => e.Message.Contains("Signal state changed")), Is.False);
        });
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ComposedPipelinePreservesFullKeysAndRuntimeMissingPluginReportsUnknown(bool missing)
    {
        using var fixture = new HostFixture();
        using var offline = new OfflineRuntime();
        fixture.Environment["SHE_MODEL_TWO"] = fixture.Environment["SHE_MODEL_ONE"]
            .Replace("model-one", "model-two").Replace("model.test", "second.test")
            .Replace("\"threshold\":10", "\"threshold\":1");
        if (missing)
            fixture.Environment["SHE_MODEL_TWO"] = fixture.Environment["SHE_MODEL_TWO"].Replace("\"plugin\":\"static-one\"", "\"plugin\":\"absent\"");
        using var stop = new CancellationTokenSource();
        var run = new HostRunner(offline.Dependencies(fixture)).RunAsync([], stop.Token);
        try
        {
            await OfflineRuntime.Ready();
            await OfflineRuntime.Until(() => offline.Transport.Requests.Count(r => r.Method == "POST") >= 2);
            Assert.Multiple(() =>
            {
                var first = offline.Transport.Requests.First(r => r.Method == "POST" && r.Host == "model.test");
                var second = offline.Transport.Requests.First(r => r.Method == "POST" && r.Host == "second.test");
                Assert.That(first.Body, Does.Contain("\"healthState\":\"Healthy\""));
                Assert.That(second.Body, Does.Contain(missing ? "\"healthState\":\"Unknown\"" : "\"healthState\":\"Unhealthy\""));
                Assert.That(offline.Transport.Requests.Count(r => r.Method == "GET"), Is.GreaterThanOrEqualTo(4));
                Assert.That(offline.Credentials.Constructions, Is.EqualTo(2));
                Assert.That(offline.Traces.Activities.Select(a => a.OperationName), Does.Contain("report.publish"));
            });
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.That(await run, Is.Zero);
        Assert.That(await new ProbeCommand().ExecuteAsync("ready", CancellationToken.None), Is.Not.Zero);
    }

    [Test]
    public async Task FailedRemoteChecksOnlyWarnAndLeaveReadinessAndPublishingEnabled()
    {
        using var fixture = new HostFixture();
        using var offline = new OfflineRuntime();
        offline.Transport.Send = (request, _) => Task.FromResult(new System.Net.Http.HttpResponseMessage(
            request.Method == System.Net.Http.HttpMethod.Get ? System.Net.HttpStatusCode.Forbidden : System.Net.HttpStatusCode.Accepted));
        using var stop = new CancellationTokenSource();
        var run = new HostRunner(offline.Dependencies(fixture)).RunAsync([], stop.Token);
        try
        {
            await OfflineRuntime.Ready();
            await OfflineRuntime.Until(() => offline.Transport.Requests.Any(r => r.Method == "POST") &&
                offline.Logs.Entries.Any(e => e.Message.Contains("Remote model/entity check warning", StringComparison.Ordinal)));
            Assert.Multiple(() =>
            {
                Assert.That(offline.Transport.Requests.First(r => r.Method == "POST").Body, Does.Contain("\"healthState\":\"Healthy\""));
                Assert.That(offline.Logs.Entries.Where(e => e.Message.Contains("Remote model/entity check warning", StringComparison.Ordinal))
                    .All(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning), Is.True);
            });
            Assert.That(await new ProbeCommand().ExecuteAsync("ready", CancellationToken.None), Is.Zero);
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Test]
    public async Task PackageAuthRegistrationIsSharedByValidationAndComposedRuntime()
    {
        using var fixture = new HostFixture();
        using var offline = new OfflineRuntime();
        fixture.Environment["SHE_PACKAGES_DIR"] = HostFixture.OfflinePackagesRoot;
        fixture.Environment["SHE_LOG_LEVEL"] = "Debug";
        fixture.Environment["SHE_PLUGIN_STATIC"] = fixture.Environment["SHE_PLUGIN_STATIC"]
            .Replace("\"package\":\"static\"", "\"package\":\"offline\"")
            .Replace("\"type\":\"none\"", "\"type\":\"fixtureLocal\"");
        fixture.Environment["SHE_MODEL_ONE"] = fixture.Environment["SHE_MODEL_ONE"]
            .Replace("\"dataPlaneUri\":\"https://model.test\"", "\"dataPlaneUri\":\"https://model.test\",\"auth\":{\"type\":\"fixtureLocal\"}");
        Assert.That(await new HostRunner(offline.Dependencies(fixture)).RunAsync(["validate"], CancellationToken.None), Is.Zero);
        Assert.That(offline.Logs.Entries, Is.Empty, "Validation must not construct auth contexts or sink clients.");
        using var stop = new CancellationTokenSource();
        var run = new HostRunner(offline.Dependencies(fixture)).RunAsync([], stop.Token);
        try
        {
            await OfflineRuntime.Ready();
            await OfflineRuntime.Until(() => offline.Transport.Requests.Any(r => r.Method == "POST"));
            Assert.Multiple(() =>
            {
                Assert.That(System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
                    .GetActiveTcpListeners().Where(e => e.Port == 8080).Select(e => e.Address),
                    Is.EqualTo(new[] { System.Net.IPAddress.Loopback }));
                Assert.That(offline.Credentials.Constructions, Is.Zero);
                Assert.That(offline.Transport.Requests.All(r => r.Authorization == "Bearer local-custom-auth"), Is.True);
                var created = offline.Logs.Entries.Where(e => e.Message.StartsWith("Fixture client created", StringComparison.Ordinal)).ToArray();
                var polled = offline.Logs.Entries.First(e => e.Message.StartsWith("Fixture client polled", StringComparison.Ordinal));
                Assert.That(created, Has.Length.EqualTo(1));
                Assert.That(polled.Attributes.Single(a => a.Key == "Client").Value,
                    Is.EqualTo(created.Single().Attributes.Single(a => a.Key == "Client").Value));
            });
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(5)); }
    }
}
