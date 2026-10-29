using System.Text.Json;
using System.Text.Json.Nodes;
using SelfHostedEvaluators.Core.Configuration;
using SelfHostedEvaluators.Core.Packages;

namespace SelfHostedEvaluators.Core.Tests;

public class EnvironmentConfigTests
{
    internal const string Plugin = """{"schemaVersion":1,"name":"sink-one","package":"one","endpoints":{"default":"https://sink.test"},"auth":{"type":"none"}}""";
    internal const string Model = """{"schemaVersion":1,"name":"model-one","healthModel":{"dataPlaneUri":"https://model.test"},"signals":[{"entity":"entity-one","name":"signal-one","plugin":"sink-one","query":{},"evaluation":{"unhealthy":{"operator":"GreaterThan","threshold":10}}}]}""";

    internal static Dictionary<string, string> Environment(string? plugin = null, string? model = null) =>
        new() { ["SHE_PLUGIN_ONE"] = plugin ?? Plugin, ["SHE_MODEL_ONE"] = model ?? Model };

    [Test]
    public void InvalidJsonIdentifiesVariableWithoutEchoingPayload()
    {
        var error = Assert.Throws<ConfigurationException>(() =>
            new EnvironmentConfigSource().Load(new Dictionary<string, string> { ["SHE_PLUGIN_BAD"] = "{sensitive-payload" }));
        Assert.That(error!.Message, Does.Contain("SHE_PLUGIN_BAD").And.Not.Contain("sensitive-payload"));
    }

    [TestCase("\"sensitive-payload\"")]
    [TestCase("[]")]
    [TestCase("null")]
    [TestCase("{\"schemaVersion\":1,\"schemaVersion\":1}")]
    public void OnlyStrictUniquePropertyObjectsAreAccepted(string json) =>
        Assert.Throws<ConfigurationException>(() => new EnvironmentConfigSource().Load(Environment(json)));

    [TestCase("\"name\":\"sink-one\"", "\"name\":\"sink-\\uD800\"")]
    [TestCase("\"auth\":{\"type\":\"none\"}", "\"auth\":{\"type\":\"none\"},\"settings\":{\"value\":\"\\uD800\"}")]
    [TestCase("\"auth\":{\"type\":\"none\"}", "\"auth\":{\"type\":\"none\"},\"settings\":{\"\\uD800\":1}")]
    public void InvalidUnicodeAnywhereInJsonIsRedacted(string oldValue, string newValue)
    {
        var error = Assert.Throws<ConfigurationException>(() =>
            new EnvironmentConfigSource().Load(Environment(Plugin.Replace(oldValue, newValue))));
        Assert.That(error!.Diagnostics.Any(d => d.Variable == "SHE_PLUGIN_ONE"), Is.True);
    }

    [TestCase("SHE_typo", "1")]
    [TestCase("SHE_PLUGIN_lower", "{}")]
    [TestCase("SHE_MODEL_1BAD", "{}")]
    [TestCase("SHE_MODEL_GOOD\n", "{}")]
    [TestCase("SHE_PLUGIN_", "{}")]
    [TestCase("SHE_DEFAULT_INTERVAL", "9s")]
    [TestCase("SHE_DEFAULT_INTERVAL", "86401s")]
    [TestCase("SHE_DEFAULT_INTERVAL", "1.5m")]
    [TestCase("SHE_DEFAULT_TIMEOUT", "0s")]
    [TestCase("SHE_DEFAULT_TIMEOUT", "999999999999999999d")]
    [TestCase("SHE_DEFAULT_TTL_MULTIPLIER", "NaN")]
    [TestCase("SHE_DEFAULT_TTL_MULTIPLIER", "Infinity")]
    [TestCase("SHE_DEFAULT_TTL_MULTIPLIER", "3,5")]
    [TestCase("SHE_DEFAULT_TTL_MULTIPLIER", "1")]
    [TestCase("SHE_REPORTS_PER_MINUTE", "6000")]
    [TestCase("SHE_REPORTS_PER_MINUTE", "0")]
    [TestCase("SHE_REPORTS_PER_MINUTE", "5,000")]
    [TestCase("SHE_SECRET_CACHE_TTL", "0m")]
    [TestCase("SHE_SHUTDOWN_TIMEOUT", "-1s")]
    [TestCase("SHE_LOG_LEVEL", "information")]
    [TestCase("SHE_PACKAGES_DIR", "https://packages.test")]
    [TestCase("SHE_PACKAGES_DIR", "")]
    [TestCase("AZURE_CLIENT_ID", "invalid")]
    [TestCase("APPLICATIONINSIGHTS_CONNECTION_STRING", "")]
    public void InvalidReservedSettingsNeverFallBack(string key, string value)
    {
        var env = Environment();
        env[key] = value;
        Assert.Throws<ConfigurationException>(() => new EnvironmentConfigSource().Load(env));
    }

    [TestCase("SHE_PLUGIN_lower")]
    [TestCase("SHE_PLUGIN_1BAD")]
    [TestCase("SHE_PLUGIN_")]
    [TestCase("SHE_PLUGIN_GOOD\n")]
    [TestCase("SHE_MODEL_lower")]
    [TestCase("SHE_MODEL_")]
    public void InvalidSuffixIsRejectedEvenWhenPayloadIsOtherwiseValid(string key)
    {
        var env = Environment();
        env[key] = key.StartsWith("SHE_PLUGIN_", StringComparison.Ordinal)
            ? Plugin.Replace("sink-one", "sink-two")
            : Model.Replace("model-one", "model-two").Replace("model.test", "other.test");
        var error = Assert.Throws<ConfigurationException>(() => new EnvironmentConfigSource().Load(env));
        Assert.That(error!.Diagnostics.Any(d => d.Variable == key && d.PropertyPath == "$"), Is.True);
    }

    [Test]
    public void ValidHostOverridesRemainIndependentOfProcessEnvironment()
    {
        var env = Environment();
        env["SHE_DEFAULT_INTERVAL"] = "2m";
        env["SHE_DEFAULT_TIMEOUT"] = "15s";
        env["SHE_DEFAULT_TTL_MULTIPLIER"] = "2.5";
        env["SHE_REPORTS_PER_MINUTE"] = "5999";
        env["SHE_SECRET_CACHE_TTL"] = "7m";
        env["SHE_SHUTDOWN_TIMEOUT"] = "9s";
        env["SHE_LOG_LEVEL"] = "None";
        var snapshot = new EnvironmentConfigSource().Load(env);
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Models[0].Signals[0].Interval, Is.EqualTo(TimeSpan.FromMinutes(2)));
            Assert.That(snapshot.Models[0].Signals[0].ExpiresInMinutes, Is.EqualTo(5));
            Assert.That(snapshot.Plugins[0].Timeout, Is.EqualTo(TimeSpan.FromSeconds(15)));
            Assert.That(snapshot.Models[0].HealthModel.ReportsPerMinute, Is.EqualTo(5999));
            Assert.That(snapshot.Host.SecretCacheTtl, Is.EqualTo(TimeSpan.FromMinutes(7)));
            Assert.That(snapshot.Host.ShutdownTimeout, Is.EqualTo(TimeSpan.FromSeconds(9)));
            Assert.That(snapshot.Host.LogLevel, Is.EqualTo("None"));
        });
    }

    [Test]
    public void DefaultsAndOwnedJsonRemainUsableAfterParsing()
    {
        var env = Environment();
        var snapshot = new EnvironmentConfigSource().Load(env);
        env.Clear();
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Host.PackagesDirectory, Is.EqualTo("/app/packages"));
            Assert.That(snapshot.Host.DefaultInterval, Is.EqualTo(TimeSpan.FromMinutes(1)));
            Assert.That(snapshot.Host.DefaultTimeout, Is.EqualTo(TimeSpan.FromSeconds(30)));
            Assert.That(snapshot.Host.DefaultTtlMultiplier, Is.EqualTo(3));
            Assert.That(snapshot.Host.ReportsPerMinute, Is.EqualTo(5000));
            Assert.That(snapshot.Host.SecretCacheTtl, Is.EqualTo(TimeSpan.FromMinutes(5)));
            Assert.That(snapshot.Host.ShutdownTimeout, Is.EqualTo(TimeSpan.FromSeconds(5)));
            Assert.That(snapshot.Host.LogLevel, Is.EqualTo("Information"));
            Assert.That(snapshot.Models[0].HealthModel.ApiVersion, Is.EqualTo("2026-06-01-preview"));
            Assert.That(snapshot.Models[0].HealthModel.TokenScope, Is.EqualTo("https://data.cloudhealth.azure.com/.default"));
            Assert.That(snapshot.Models[0].HealthModel.Auth.Type, Is.EqualTo("managedIdentity"));
            Assert.That(snapshot.Models[0].Signals[0].ExpiresInMinutes, Is.EqualTo(3));
            Assert.That(snapshot.Plugins[0].Settings.ValueKind, Is.EqualTo(JsonValueKind.Object));
            Assert.That(snapshot.Models[0].Signals[0].Query.GetRawText(), Is.EqualTo("{}"));
        });
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, Uri>)snapshot.Plugins[0].Endpoints).Add("bad", new Uri("https://bad.test")));
    }

    [TestCase("{}", true)]
    [TestCase("{}", false)]
    public void PluginAndModelAreBothRequired(string unused, bool removePlugin)
    {
        var env = Environment();
        env.Remove(removePlugin ? "SHE_PLUGIN_ONE" : "SHE_MODEL_ONE");
        Assert.Throws<ConfigurationException>(() => new EnvironmentConfigSource().Load(env));
    }

    [TestCase("\"schemaVersion\":1", "\"schemaVersion\":2")]
    [TestCase("\"name\":\"sink-one\"", "\"name\":\"x\"")]
    [TestCase("\"name\":\"sink-one\"", "\"name\":\"sink-one\\n\"")]
    [TestCase("\"auth\":{\"type\":\"none\"}", "\"auth\":{\"type\":\"none\",\"extra\":\"sensitive\"}")]
    [TestCase("\"auth\":{\"type\":\"none\"}", "\"auth\":{\"type\":\"bearer\",\"token\":\"sensitive\"}")]
    [TestCase("\"auth\":{\"type\":\"none\"}", "\"auth\":{\"type\":\"bearer\",\"token\":\"kv://vault/name?bad=1\"}")]
    [TestCase("\"auth\":{\"type\":\"none\"}", "\"auth\":{\"type\":\"managedIdentity\"}")]
    [TestCase("\"endpoints\":{\"default\":\"https://sink.test\"}", "\"endpoints\":{\"default\":\"http://sink.test\"}")]
    [TestCase("\"package\":\"one\"", "\"package\":\"../one\"")]
    [TestCase("\"package\":\"one\"", "\"package\":\"one\",\"packageVersion\":\"nonsense\"")]
    [TestCase("\"package\":\"one\"", "\"package\":\"one\",\"concurrency\":0")]
    [TestCase("\"package\":\"one\"", "\"package\":\"one\",\"rateLimit\":{\"requests\":0,\"per\":\"1s\"}")]
    [TestCase("\"package\":\"one\"", "\"package\":\"one\",\"batch\":{\"maxSize\":0}")]
    [TestCase("\"package\":\"one\"", "\"package\":\"one\",\"defaults\":{\"ttlMultiplier\":3}")]
    public void InvalidPluginShapesAndSecretsAreRedacted(string oldValue, string newValue)
    {
        var error = Assert.Throws<ConfigurationException>(() =>
            new EnvironmentConfigSource().Load(Environment(Plugin.Replace(oldValue, newValue))));
        Assert.That(error!.Message, Does.Not.Contain("sensitive"));
    }

    [TestCase("\"schemaVersion\":1", "\"schemaVersion\":2")]
    [TestCase("\"dataPlaneUri\":\"https://model.test\"", "\"dataPlaneUri\":\"https://user:password@model.test?q=sensitive\"")]
    [TestCase("\"threshold\":10", "\"threshold\":1e999")]
    [TestCase("\"operator\":\"GreaterThan\"", "\"operator\":\"greaterThan\"")]
    [TestCase("\"query\":{}", "\"query\":{\"a\":1,\"a\":2}")]
    [TestCase("\"query\":{}", "\"query\":[]")]
    [TestCase("\"query\":{}", "\"unexpected\":1,\"query\":{}")]
    [TestCase("\"query\":{}", "\"interval\":\"9s\",\"query\":{}")]
    [TestCase("\"query\":{}", "\"expiresInMinutes\":1,\"query\":{}")]
    [TestCase("\"query\":{}", "\"expiresInMinutes\":10081,\"query\":{}")]
    [TestCase("\"query\":{}", "\"ttlMultiplier\":1,\"query\":{}")]
    [TestCase("\"query\":{}", "\"context\":\"{{password}}\",\"query\":{}")]
    [TestCase("\"unhealthy\":{\"operator\":\"GreaterThan\",\"threshold\":10}", "\"degraded\":{\"operator\":\"Equal\",\"threshold\":1}")]
    [TestCase("\"unhealthy\":{\"operator\":\"GreaterThan\",\"threshold\":10}", "\"stateMap\":{\"OK\":\"Healthy\",\"ok\":\"Degraded\"}")]
    [TestCase("\"unhealthy\":{\"operator\":\"GreaterThan\",\"threshold\":10}", "\"stateMap\":{\"OK\":\"Invalid\"}")]
    public void InvalidModelsAreRejectedBeforePackageValidation(string oldValue, string newValue) =>
        Assert.Throws<ConfigurationException>(() => new EnvironmentConfigSource().Load(
            Environment(model: Model.Replace(oldValue, newValue))));

    [TestCase("SHE_PLUGIN_ONE", "$.endpoints.default")]
    [TestCase("SHE_PLUGIN_ONE", "$.auth.tokenEndpoint")]
    [TestCase("SHE_MODEL_ONE", "$.healthModel.dataPlaneUri")]
    [TestCase("SHE_MODEL_ONE", "$.healthModel.tokenScope")]
    public void MalformedIdnHttpsUrisAreRejectedAtTheirRedactedLocation(string variable, string path)
    {
        var env = EnvironmentWithHttpsUri(path, "https://sensitive-payload\u200D.test/secret-value");
        var error = Assert.Throws<ConfigurationException>(() => new EnvironmentConfigSource().Load(env));
        Assert.Multiple(() =>
        {
            Assert.That(error!.Diagnostics, Does.Contain(new ConfigurationDiagnostic(variable, path)));
            Assert.That(error.ToString(), Does.Not.Contain("sensitive-payload").And.Not.Contain("secret-value"));
            Assert.That(error.InnerException, Is.Null);
        });
    }

    [TestCase("$.endpoints.default")]
    [TestCase("$.auth.tokenEndpoint")]
    [TestCase("$.healthModel.dataPlaneUri")]
    [TestCase("$.healthModel.tokenScope")]
    public void ValidIdnHttpsUrisRemainAccepted(string path)
    {
        var env = EnvironmentWithHttpsUri(path, "https://bücher.test/path");
        Assert.DoesNotThrow(() => new EnvironmentConfigSource().Load(env));
    }

    [Test]
    public void MalformedIdnEndpointDoesNotEscapePackageValidationAsUriFormatException()
    {
        using var fixture = new PackageFixture();
        fixture.Add();
        using var catalog = PackageCatalog.Load(fixture.Root);
        var env = EnvironmentWithHttpsUri("$.endpoints.default", "https://foo\u200D.test");
        var error = Assert.Throws<ConfigurationException>(() =>
        {
            var snapshot = new EnvironmentConfigSource().Load(env);
            ConfigurationValidator.Validate(snapshot, catalog, ConfigurationValidationMode.Deployment);
        });
        Assert.That(error!.Diagnostics, Does.Contain(new ConfigurationDiagnostic("SHE_PLUGIN_ONE", "$.endpoints.default")));
        Assert.That(error.ToString(), Does.Not.Contain("foo"));
        Assert.That(error.ToString().Contains("\u200D", StringComparison.Ordinal), Is.False);
    }

    private static Dictionary<string, string> EnvironmentWithHttpsUri(string path, string uri)
    {
        var plugin = JsonNode.Parse(Plugin)!;
        var model = JsonNode.Parse(Model)!;
        switch (path)
        {
            case "$.endpoints.default":
                plugin["endpoints"]!["default"] = uri;
                break;
            case "$.auth.tokenEndpoint":
                plugin["auth"] = JsonNode.Parse("""{"type":"oauth2ClientCredentials","clientId":"client","clientSecret":"kv://vault/secret","scopes":["read"]}""");
                plugin["auth"]!["tokenEndpoint"] = uri;
                break;
            case "$.healthModel.dataPlaneUri":
                model["healthModel"]!["dataPlaneUri"] = uri;
                break;
            case "$.healthModel.tokenScope":
                model["healthModel"]!["tokenScope"] = uri;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(path));
        }
        return Environment(plugin.ToJsonString(), model.ToJsonString());
    }

    [Test]
    public void DuplicateNamesNormalizedUrisAndSignalKeysFail()
    {
        foreach (var pair in new[]
        {
            ("SHE_PLUGIN_TWO", Plugin),
            ("SHE_MODEL_TWO", Model),
            ("SHE_MODEL_TWO", Model.Replace("model-one", "model-two").Replace("https://model.test", "https://MODEL.test:443/"))
        })
        {
            var env = Environment();
            env.Add(pair.Item1, pair.Item2);
            Assert.Throws<ConfigurationException>(() => new EnvironmentConfigSource().Load(env));
        }
        var duplicate = Model.Replace("}]}", "}," + Model[(Model.IndexOf("[{", StringComparison.Ordinal) + 1)..^2] + "]}");
        Assert.Throws<ConfigurationException>(() => new EnvironmentConfigSource().Load(Environment(model: duplicate)));
    }

    [TestCase(null, null, null, 60, 3)]
    [TestCase(null, "2m", "3m", 120, 6)]
    [TestCase("10s", "2m", "3m", 10, 1)]
    [TestCase(null, null, "1d", 86400, 4320)]
    public void IntervalPrecedenceAndTtlCeiling(string? signal, string? plugin, string? model, int seconds, int ttl)
    {
        var pluginJson = plugin is null ? Plugin : Plugin[..^1] + ",\"defaults\":{\"interval\":\"" + plugin + "\"}}";
        var modelJson = model is null ? Model : Model.Replace("\"signals\":", "\"defaults\":{\"interval\":\"" + model + "\"},\"signals\":");
        if (signal is not null)
            modelJson = modelJson.Replace("\"query\":", "\"interval\":\"" + signal + "\",\"query\":");
        var result = new EnvironmentConfigSource().Load(Environment(pluginJson, modelJson)).Models[0].Signals[0];
        Assert.That(result.Interval.TotalSeconds, Is.EqualTo(seconds));
        Assert.That(result.ExpiresInMinutes, Is.EqualTo(ttl));
    }

    [TestCase("\"ttlMultiplier\":100", 100)]
    [TestCase("\"ttlMultiplier\":100000", 10080)]
    [TestCase("\"ttlMultiplier\":100000,\"expiresInMinutes\":2", 2)]
    public void ExplicitTtlOverridesAndComputedTtlClamps(string fields, int expected)
    {
        var json = Model.Replace("\"query\":{}", fields + ",\"query\":{}");
        Assert.That(new EnvironmentConfigSource().Load(Environment(model: json)).Models[0].Signals[0].ExpiresInMinutes,
            Is.EqualTo(expected));
    }

    [Test]
    public void SignalMultiplierOverridesModelExplicitTtlAndTimeoutIsBounded()
    {
        var model = Model.Replace("\"signals\":", "\"defaults\":{\"expiresInMinutes\":9},\"signals\":")
            .Replace("\"query\":{}", "\"ttlMultiplier\":4,\"interval\":\"10s\",\"query\":{}");
        var snapshot = new EnvironmentConfigSource().Load(Environment(model: model));
        Assert.That(snapshot.Models[0].Signals[0].ExpiresInMinutes, Is.EqualTo(1));
        Assert.That(snapshot.Plugins[0].GetEffectiveTimeout(TimeSpan.FromSeconds(10)), Is.EqualTo(TimeSpan.FromSeconds(10)));
    }

    [Test]
    public void HostTtlIsCheckedAgainstEffectiveSignalIntervalNotUnusedHostInterval()
    {
        var env = Environment(model: Model.Replace("\"query\":{}", "\"interval\":\"10s\",\"query\":{}"));
        env["SHE_DEFAULT_TTL_MULTIPLIER"] = "1";
        Assert.That(new EnvironmentConfigSource().Load(env).Models[0].Signals[0].ExpiresInMinutes, Is.EqualTo(1));
    }

    [TestCase(1, false)]
    [TestCase(2, true)]
    public void ModelBudgetUsesEffectivePollRate(int budget, bool accepted)
    {
        var json = Model.Replace("\"dataPlaneUri\":", $"\"reportsPerMinute\":{budget},\"dataPlaneUri\":")
            .Replace("\"query\":{}", "\"interval\":\"30s\",\"query\":{}");
        if (accepted)
            Assert.DoesNotThrow(() => new EnvironmentConfigSource().Load(Environment(model: json)));
        else
            Assert.Throws<ConfigurationException>(() => new EnvironmentConfigSource().Load(Environment(model: json)));
    }

    [TestCase(7, "7m")]
    [TestCase(13, "13m")]
    public void RepeatingFractionRatesMeetExactBudget(int count, string interval)
    {
        var model = ModelWithSignalIntervals(Enumerable.Repeat(interval, count), 1);
        var snapshot = new EnvironmentConfigSource().Load(Environment(model: model));
        Assert.That(snapshot.Models[0].Signals, Has.Length.EqualTo(count));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void MixedFractionRatesMeetExactBudgetRegardlessOfOrder(bool reverse)
    {
        string[] intervals = ["2m", "3m", "7m", "42m"];
        var model = ModelWithSignalIntervals(reverse ? intervals.Reverse() : intervals, 1);
        Assert.DoesNotThrow(() => new EnvironmentConfigSource().Load(Environment(model: model)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void BarelyOverBudgetRatesAreRejectedWithoutTolerance(bool reverse)
    {
        // 140 * 60/8400 + 4201 * 60/8402 = 31. Replace four of each rate
        // with 60/8399 + 6 * 60/8401 + 60/8403: an excess of about 3.44e-17.
        var intervals = Enumerable.Repeat("8400s", 136).Concat(Enumerable.Repeat("8402s", 4197))
            .Concat(["8399s"]).Concat(Enumerable.Repeat("8401s", 6)).Concat(["8403s"]);
        var model = ModelWithSignalIntervals(reverse ? intervals.Reverse() : intervals, 31);
        var error = Assert.Throws<ConfigurationException>(() =>
            new EnvironmentConfigSource().Load(Environment(model: model)));
        Assert.That(error!.Diagnostics, Does.Contain(new ConfigurationDiagnostic("SHE_MODEL_ONE", "$.healthModel.reportsPerMinute")));
    }

    private static string ModelWithSignalIntervals(IEnumerable<string> intervals, int budget)
    {
        var model = JsonNode.Parse(Model)!;
        model["healthModel"]!["reportsPerMinute"] = budget;
        var template = model["signals"]![0]!;
        var signals = new JsonArray();
        foreach (var interval in intervals)
        {
            var signal = template.DeepClone();
            signal["name"] = "signal-" + signals.Count;
            signal["interval"] = interval;
            signals.Add(signal);
        }
        model["signals"] = signals;
        return model.ToJsonString();
    }

    [Test]
    public void AuthProfilesAreTypedImmutableConfigurationOnly()
    {
        var json = Plugin.Replace("\"type\":\"none\"", "\"type\":\"apiKey\",\"headers\":{\"X-Token\":\"kv://vault/token\"}");
        var profile = (ApiKeyAuthProfile)new EnvironmentConfigSource().Load(Environment(json)).Plugins[0].Auth;
        Assert.That(profile.Headers["X-Token"], Is.EqualTo(new Uri("kv://vault/token")));
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, Uri>)profile.Headers).Clear());
    }

    [TestCase("""{"type":"bearer","token":"kv://vault/token/version"}""", typeof(BearerAuthProfile))]
    [TestCase("""{"type":"basic","username":"kv://vault/user","password":"kv://vault/password"}""", typeof(BasicAuthProfile))]
    [TestCase("""{"type":"apiKey","queryParameter":"api-key","secret":"kv://vault/token"}""", typeof(ApiKeyAuthProfile))]
    [TestCase("""{"type":"mtls","certificate":"kv://vault/cert"}""", typeof(MtlsAuthProfile))]
    [TestCase("""{"type":"oauth2ClientCredentials","tokenEndpoint":"https://auth.test/token","clientId":"client","clientSecret":"kv://vault/secret","scopes":["read"]}""", typeof(OAuth2ClientCredentialsAuthProfile))]
    [TestCase("""{"type":"entraId","azure":{"type":"managedIdentity"},"scope":"https://sink.test/.default"}""", typeof(EntraIdAuthProfile))]
    public void BuiltInSinkProfilesParseWithoutResolvingCredentials(string profile, Type expectedType)
    {
        var json = JsonNode.Parse(Plugin)!;
        json["auth"] = JsonNode.Parse(profile);
        Assert.That(new EnvironmentConfigSource().Load(Environment(json.ToJsonString())).Plugins[0].Auth, Is.TypeOf(expectedType));
    }

    [TestCase("""{"type":"managedIdentity","clientId":"11111111-1111-1111-1111-111111111111"}""", typeof(ManagedIdentityAuthProfile))]
    [TestCase("""{"type":"workloadIdentity","tenantId":"11111111-1111-1111-1111-111111111111","clientId":"22222222-2222-2222-2222-222222222222","tokenFile":"external-token-file"}""", typeof(WorkloadIdentityAuthProfile))]
    [TestCase("""{"type":"servicePrincipalSecret","tenantId":"11111111-1111-1111-1111-111111111111","clientId":"22222222-2222-2222-2222-222222222222","clientSecret":"kv://vault/secret"}""", typeof(ServicePrincipalSecretAuthProfile))]
    [TestCase("""{"type":"servicePrincipalCertificate","tenantId":"11111111-1111-1111-1111-111111111111","clientId":"22222222-2222-2222-2222-222222222222","certificate":"kv://vault/cert"}""", typeof(ServicePrincipalCertificateAuthProfile))]
    public void AzureProfilesAreTypedConfigurationData(string profile, Type expectedType)
    {
        var json = JsonNode.Parse(Model)!;
        json["healthModel"]!["auth"] = JsonNode.Parse(profile);
        Assert.That(new EnvironmentConfigSource().Load(Environment(model: json.ToJsonString())).Models[0].HealthModel.Auth,
            Is.TypeOf(expectedType));
    }

    [TestCase("""{"type":"apiKey","headers":{"X":"plain-text"}}""")]
    [TestCase("""{"type":"apiKey","headers":{"X":"kv://vault/name","x":"kv://vault/name"}}""")]
    [TestCase("""{"type":"apiKey","headers":{"X":"kv://vault/name"},"queryParameter":"key","secret":"kv://vault/name"}""")]
    [TestCase("""{"type":"apiKey","queryParameter":"key"}""")]
    [TestCase("""{"type":"basic","username":"kv://vault/name","password":"password"}""")]
    [TestCase("""{"type":"bearer","token":"kv://user:password@vault/name"}""")]
    [TestCase("""{"type":"bearer","token":"kv://vault/name/version/extra"}""")]
    [TestCase("""{"type":"mtls","certificate":"https://vault/cert"}""")]
    [TestCase("""{"type":"oauth2ClientCredentials","tokenEndpoint":"http://auth.test","clientId":"client","clientSecret":"kv://vault/secret","scopes":[]}""")]
    [TestCase("""{"type":"entraId","azure":{"type":"none"},"scope":"scope"}""")]
    [TestCase("""{"type":"BEARER","token":"kv://vault/name"}""")]
    public void InvalidAuthFailsBeforeAnyContextFactory(string profile)
    {
        var json = JsonNode.Parse(Plugin)!;
        json["auth"] = JsonNode.Parse(profile);
        Assert.Throws<ConfigurationException>(() => new EnvironmentConfigSource().Load(Environment(json.ToJsonString())));
    }

    [Test]
    public void WorkloadFileAndManagedIdentityInheritOnlyExplicitEnvironmentData()
    {
        var json = JsonNode.Parse(Model)!;
        json["healthModel"]!["auth"] = JsonNode.Parse("""{"type":"workloadIdentity","tenantId":"11111111-1111-1111-1111-111111111111","clientId":"22222222-2222-2222-2222-222222222222"}""");
        var env = Environment(model: json.ToJsonString());
        Assert.Throws<ConfigurationException>(() => new EnvironmentConfigSource().Load(env));
        env["AZURE_FEDERATED_TOKEN_FILE"] = "externally-provided-file";
        var config = new EnvironmentConfigSource().Load(env);
        Assert.That(((WorkloadIdentityAuthProfile)config.Models[0].HealthModel.Auth).TokenFile, Is.EqualTo("externally-provided-file"));
        env["SHE_MODEL_ONE"] = Model;
        env["AZURE_CLIENT_ID"] = "11111111-1111-1111-1111-111111111111";
        config = new EnvironmentConfigSource().Load(env);
        Assert.That(((ManagedIdentityAuthProfile)config.Models[0].HealthModel.Auth).ClientId,
            Is.EqualTo("11111111-1111-1111-1111-111111111111"));
    }

    [Test]
    public void ExactDefaultBudgetAndEntityWarningBoundariesAreEnforced()
    {
        var root = JsonNode.Parse(Model)!;
        var template = root["signals"]![0]!.DeepClone();
        var signals = new JsonArray();
        for (var index = 0; index < 834; index++)
        {
            var signal = template.DeepClone();
            signal["name"] = "signal-" + index;
            signal["interval"] = index == 833 ? "30s" : "10s";
            signals.Add(signal);
        }
        root["signals"] = signals;
        var snapshot = new EnvironmentConfigSource().Load(Environment(model: root.ToJsonString()));
        Assert.That(snapshot.Warnings, Has.Length.EqualTo(1));
        signals[833]!["interval"] = "10s";
        Assert.Throws<ConfigurationException>(() => new EnvironmentConfigSource().Load(Environment(model: root.ToJsonString())));
        while (signals.Count > 50) signals.RemoveAt(signals.Count - 1);
        Assert.That(new EnvironmentConfigSource().Load(Environment(model: root.ToJsonString())).Warnings, Is.Empty);
    }

    [Test]
    public void StateMapsAreCaseInsensitiveFrozenAndIndependentOfInputJson()
    {
        var root = JsonNode.Parse(Model)!;
        root["signals"]![0]!["evaluation"] = JsonNode.Parse("""{"stateMap":{"OK":"Healthy"},"noData":"Degraded"}""");
        var result = new EnvironmentConfigSource().Load(Environment(model: root.ToJsonString()));
        root["signals"]![0]!["evaluation"]!["stateMap"]!["OK"] = "Unhealthy";
        var evaluation = result.Models[0].Signals[0].Evaluation;
        Assert.That(evaluation.StateMap!["ok"], Is.EqualTo(HealthState.Healthy));
        Assert.That(evaluation.NoData, Is.EqualTo(HealthState.Degraded));
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, HealthState>)evaluation.StateMap!).Clear());
    }

    [Test]
    public void MissingPluginIsRuntimeUnknownButDeploymentErrorWithoutSkippingBasicValidation()
    {
        var snapshot = new EnvironmentConfigSource().Load(Environment(model: Model.Replace("sink-one", "missing-one")));
        using var fixture = new PackageFixture();
        fixture.Add();
        using var catalog = PackageCatalog.Load(fixture.Root);
        Assert.DoesNotThrow(() => ConfigurationValidator.Validate(snapshot, catalog, ConfigurationValidationMode.Runtime));
        Assert.Throws<ConfigurationException>(() =>
            ConfigurationValidator.Validate(snapshot, catalog, ConfigurationValidationMode.Deployment));
        Assert.Throws<ConfigurationException>(() => new EnvironmentConfigSource().Load(
            Environment(model: Model.Replace("sink-one", "missing-one").Replace("\"query\":{}", "\"interval\":\"1s\",\"query\":{}"))));
    }
}
