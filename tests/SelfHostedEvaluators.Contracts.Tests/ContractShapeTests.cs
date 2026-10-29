using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Logging.Abstractions;
using SelfHostedEvaluators.Plugins.Abstractions;

namespace SelfHostedEvaluators.Contracts.Tests;

public sealed class ContractShapeTests
{
    [Test]
    public void QueryWindowKeepsScheduledTimeAndInterval()
    {
        var time = DateTimeOffset.Parse("2026-09-30T12:00:00Z");
        var window = new QueryWindow(time, TimeSpan.FromMinutes(2));
        Assert.That(window.ScheduledAt, Is.EqualTo(time));
        Assert.That(window.Interval, Is.EqualTo(TimeSpan.FromMinutes(2)));
    }

    [Test]
    public void PublicInterfacesKeepTheSpecifiedSignatures()
    {
        Assert.Multiple(() =>
        {
            Assert.That(typeof(IPluginPackage).Namespace, Is.EqualTo("SelfHostedEvaluators.Plugins.Abstractions"));
            Assert.That(typeof(IPluginPackage).Assembly.GetName().Name,
                Is.EqualTo("SelfHostedEvaluators.Plugins.Abstractions"));
            Assert.That(typeof(IPluginPackage).GetMethod("Register", [typeof(IPluginRegistrar)])?.ReturnType,
                Is.EqualTo(typeof(void)));
            Assert.That(typeof(ISinkPlugin).GetProperty("Capabilities")?.PropertyType,
                Is.EqualTo(typeof(PluginCapabilities)));
            Assert.That(typeof(ISinkPlugin).GetMethod("ValidateSettings", [typeof(JsonElement)])?.ReturnType,
                Is.EqualTo(typeof(ValidationResult)));
            Assert.That(typeof(ISinkPlugin).GetMethod("ValidateQuery", [typeof(JsonElement)])?.ReturnType,
                Is.EqualTo(typeof(ValidationResult)));
            Assert.That(typeof(ISinkPlugin).GetMethod("CreateClientAsync",
                [typeof(PluginInstanceContext), typeof(CancellationToken)])?.ReturnType,
                Is.EqualTo(typeof(ValueTask<ISinkClient>)));
            Assert.That(typeof(ISinkClient).GetMethod("QueryBatchAsync",
                [typeof(IReadOnlyList<SignalQuery>), typeof(QueryWindow), typeof(CancellationToken)])?.ReturnType,
                Is.EqualTo(typeof(Task<IReadOnlyList<SignalResult>>)));
            Assert.That(typeof(ISinkClient).GetInterfaces(), Does.Contain(typeof(IAsyncDisposable)));
        });
    }

    [Test]
    public void SignalKeysDistinguishModelsEntitiesAndNames()
    {
        var keys = new HashSet<SignalKey>
        {
            Key("https://one.example/model", "api", "latency"),
            Key("https://two.example/model", "api", "latency"),
            Key("https://one.example/model", "worker", "latency"),
            Key("https://one.example/model", "api", "errors"),
            Key("https://one.example/model", "api", "latency")
        };

        Assert.That(keys, Has.Count.EqualTo(4));
    }

    [Test]
    public async Task TwoClientsShareOnePackageWithoutSharingNamesOrCredentials()
    {
        IPluginPackage package = new ExamplePackage();
        var registrar = new PluginRegistrar();
        package.Register(registrar);
        var registration = registrar.Build();
        var plugin = (ISinkPlugin)Activator.CreateInstance(registration.SinkPluginType)!;
        await using var firstAuth = new ExampleAuthContext("credential-one");
        await using var secondAuth = new ExampleAuthContext("credential-two");
        using var firstHttp = new HttpClient(new AuthenticationHandler(firstAuth,
            new CredentialCheckingHandler("credential-one")))
        { BaseAddress = new Uri("https://one.example/") };
        using var secondHttp = new HttpClient(new AuthenticationHandler(secondAuth,
            new CredentialCheckingHandler("credential-two")))
        { BaseAddress = new Uri("https://two.example/") };
        using var limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = 2 });
        var firstContext = Context("eu-prod", firstHttp, firstAuth, limiter);
        var secondContext = Context("us-prod", secondHttp, secondAuth, limiter);
        await using var first = await plugin.CreateClientAsync(firstContext, CancellationToken.None);
        await using var second = await plugin.CreateClientAsync(secondContext, CancellationToken.None);
        var query = new SignalQuery(Key(), JsonSerializer.SerializeToElement(new { }));
        var window = new QueryWindow(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(2));

        var firstResults = await first.QueryBatchAsync([query], window, CancellationToken.None);
        var secondResults = await second.QueryBatchAsync([query], window, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.Not.SameAs(second));
            Assert.That(((SignalResult.Observation)firstResults.Single()).NativeState, Is.EqualTo("eu-prod"));
            Assert.That(((SignalResult.Observation)secondResults.Single()).NativeState, Is.EqualTo("us-prod"));
            Assert.That(firstContext.Auth, Is.SameAs(firstAuth));
            Assert.That(secondContext.Auth, Is.SameAs(secondAuth));
        });
    }

    [Test]
    public void ContextRetainsJsonAndSnapshotsEndpointMappings()
    {
        using var http = new HttpClient();
        using var limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = 1 });
        var endpoints = new Dictionary<string, HttpClient> { ["api"] = http };
        var time = new FixedTimeProvider();
        PluginInstanceContext context;
        SignalQuery query;
        using (var document = JsonDocument.Parse("""{"region":"eu"}"""))
        {
            context = new PluginInstanceContext("prod", document.RootElement, 8, endpoints,
                new ExampleAuthContext("private"), limiter, NullLogger.Instance, time);
            query = new SignalQuery(Key(), document.RootElement);
        }
        endpoints.Clear();

        Assert.Multiple(() =>
        {
            Assert.That(context.Settings.GetProperty("region").GetString(), Is.EqualTo("eu"));
            Assert.That(query.Query.GetProperty("region").GetString(), Is.EqualTo("eu"));
            Assert.That(context.HttpClients["api"], Is.SameAs(http));
            Assert.That(context.EffectiveBatchSize, Is.EqualTo(8));
            Assert.That(context.RateLimiter, Is.SameAs(limiter));
            Assert.That(context.Logger, Is.SameAs(NullLogger.Instance));
            Assert.That(context.TimeProvider.GetUtcNow(), Is.EqualTo(time.GetUtcNow()));
        });
    }

    [Test]
    public void ResultsRetainOptionalObservationAndBackpressureMetadata()
    {
        var key = Key();
        var time = DateTimeOffset.Parse("2026-09-30T12:00:00Z");
        SignalResult observation = new SignalResult.Observation(key, null, "OK", time, "native state only");
        SignalResult numeric = new SignalResult.Observation(key, 1.5, null, time);
        SignalResult noData = new SignalResult.NoData(key, "no samples");
        SignalResult failure = new SignalResult.Failure(key, "rate_limited", "try later", TimeSpan.FromSeconds(20));

        Assert.Multiple(() =>
        {
            Assert.That(((SignalResult.Observation)observation).Value, Is.Null);
            Assert.That(((SignalResult.Observation)observation).NativeState, Is.EqualTo("OK"));
            Assert.That(((SignalResult.Observation)observation).ObservedAt, Is.EqualTo(time));
            Assert.That(((SignalResult.Observation)observation).Detail, Is.EqualTo("native state only"));
            Assert.That(((SignalResult.Observation)numeric).Value, Is.EqualTo(1.5));
            Assert.That(((SignalResult.Observation)numeric).NativeState, Is.Null);
            Assert.That(((SignalResult.NoData)noData).Reason, Is.EqualTo("no samples"));
            Assert.That(((SignalResult.Failure)failure).RetryAfter, Is.EqualTo(TimeSpan.FromSeconds(20)));
            Assert.That(new[] { observation, numeric, noData, failure }.Select(result => result.Key),
                Is.All.EqualTo(key));
        });
    }

    [Test]
    public void RegistrarRequiresExactlyOneSinkAndSnapshotsRegistrations()
    {
        var registrar = new PluginRegistrar();
        Assert.Throws<InvalidOperationException>(() => registrar.Build());
        registrar.SetSinkPlugin<ExamplePlugin>();
        Assert.Throws<InvalidOperationException>(() => registrar.SetSinkPlugin<ExamplePlugin>());
        registrar.AddAuthScheme<ExampleAuthScheme>("session");
        var registration = registrar.Build();
        registrar.AddAuthScheme<ExampleAuthScheme>("another");

        Assert.Multiple(() =>
        {
            Assert.That(registration.SinkPluginType, Is.EqualTo(typeof(ExamplePlugin)));
            Assert.That(registration.AuthSchemes["session"], Is.EqualTo(typeof(ExampleAuthScheme)));
            Assert.That(registration.AuthSchemes, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void RegistrarRejectsDuplicateOrPreviouslyRegisteredAuthSchemeNames()
    {
        var registrar = new PluginRegistrar(["bearer", "existingPackageScheme"]);
        registrar.AddAuthScheme<ExampleAuthScheme>("session");
        Assert.Throws<InvalidOperationException>(() => registrar.AddAuthScheme<ExampleAuthScheme>("SESSION"));
        Assert.Throws<InvalidOperationException>(() => registrar.AddAuthScheme<ExampleAuthScheme>("BEARER"));
        Assert.Throws<InvalidOperationException>(() =>
            registrar.AddAuthScheme<ExampleAuthScheme>("existingPackageScheme"));
        Assert.Throws<ArgumentException>(() => registrar.AddAuthScheme<ExampleAuthScheme>(" "));
    }

    [Test]
    public async Task CustomSchemeCanResolveReferencesAtConstructionWithoutExposingSecretsInClientContext()
    {
        using var http = new HttpClient();
        var context = new AuthSchemeContext(
            JsonSerializer.SerializeToElement(new { token = "kv://vault/token" }),
            new ReferenceSecretResolver(), http, NullLogger.Instance, TimeProvider.System);
        IAuthScheme scheme = new ExampleAuthScheme();
        await using var auth = await scheme.CreateContextAsync(context, CancellationToken.None);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://sink.example/");
        using var handler = new HttpClientHandler();
        using var socket = new ClientWebSocket();

        await auth.ConfigureHttpHandlerAsync(new Uri("https://sink.example/"), handler, CancellationToken.None);
        await auth.AuthenticateAsync(request, CancellationToken.None);
        await auth.AuthenticateWebSocketAsync(new Uri("wss://sink.example/"), socket.Options,
            CancellationToken.None);

        Assert.That(request.Headers.GetValues("X-Test-Credential").Single(), Is.EqualTo("resolved-by-auth-only"));
        Assert.That(typeof(PluginInstanceContext).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.PropertyType),
            Does.Not.Contain(typeof(ISecretResolver)));
    }

    [Test]
    public void CapabilitiesAndValidationAreUsableWithoutHostDependencies()
    {
        var endpoints = new List<string> { "api" };
        var hosts = new List<string> { "sink.example" };
        var capabilities = new PluginCapabilities(100, endpoints, hosts);
        endpoints.Clear();
        hosts.Clear();
        var errors = new[] { "settings.region is required" };
        var invalid = ValidationResult.Failed(errors);
        errors[0] = "changed";

        Assert.Multiple(() =>
        {
            Assert.That(capabilities.MaxBatchSizeCap, Is.EqualTo(100));
            Assert.That(capabilities.RequiredEndpoints, Is.EqualTo(new[] { "api" }));
            Assert.That(capabilities.AllowedHosts, Is.EqualTo(new[] { "sink.example" }));
            Assert.That(ValidationResult.Success.IsValid, Is.True);
            Assert.That(invalid.IsValid, Is.False);
            Assert.That(invalid.Errors, Is.EqualTo(new[] { "settings.region is required" }));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PluginCapabilities(0, []));
            Assert.Throws<ArgumentException>(() => ValidationResult.Failed());
        });
    }

    private static SignalKey Key(string model = "https://model.example/model", string entity = "api",
        string name = "latency") => new(new Uri(model), entity, name);

    private static PluginInstanceContext Context(string name, HttpClient http, IAuthContext auth, RateLimiter limiter) =>
        new(name, JsonSerializer.SerializeToElement(new { }), 10,
            new Dictionary<string, HttpClient> { ["api"] = http }, auth, limiter, NullLogger.Instance, TimeProvider.System);

    private sealed class ExamplePackage : IPluginPackage
    {
        public void Register(IPluginRegistrar registrar) => registrar.SetSinkPlugin<ExamplePlugin>();
    }

    public sealed class ExamplePlugin : ISinkPlugin
    {
        public PluginCapabilities Capabilities => new(100, ["api"]);
        public ValidationResult ValidateSettings(JsonElement settings) => ValidationResult.Success;
        public ValidationResult ValidateQuery(JsonElement query) => ValidationResult.Success;
        public ValueTask<ISinkClient> CreateClientAsync(PluginInstanceContext context, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ISinkClient>(new ExampleClient(context));
        }
    }

    private sealed class ExampleClient(PluginInstanceContext context) : ISinkClient
    {
        public async Task<IReadOnlyList<SignalResult>> QueryBatchAsync(
            IReadOnlyList<SignalQuery> batch, QueryWindow window, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "sample");
            using var response = await context.HttpClients["api"].SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            return batch.Select(query => (SignalResult)new SignalResult.Observation(
                query.Key, 1, context.PluginName, window.ScheduledAt)).ToArray();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class AuthenticationHandler(IAuthContext auth, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await auth.AuthenticateAsync(request, ct);
            return await base.SendAsync(request, ct);
        }
    }

    private sealed class CredentialCheckingHandler(string expected) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var accepted = request.Headers.TryGetValues("X-Test-Credential", out var values) &&
                values.SequenceEqual([expected]);
            return Task.FromResult(new HttpResponseMessage(accepted ? HttpStatusCode.OK : HttpStatusCode.Unauthorized));
        }
    }

    private sealed class ExampleAuthContext(string credential) : IAuthContext
    {
        public string Scheme => "session";
        public ValueTask ConfigureHttpHandlerAsync(Uri destination, HttpClientHandler handler, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask AuthenticateAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            request.Headers.Add("X-Test-Credential", credential);
            return ValueTask.CompletedTask;
        }

        public ValueTask AuthenticateWebSocketAsync(Uri destination, ClientWebSocketOptions options,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            options.SetRequestHeader("X-Test-Credential", credential);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public sealed class ExampleAuthScheme : IAuthScheme
    {
        public async ValueTask<IAuthContext> CreateContextAsync(AuthSchemeContext context, CancellationToken ct)
        {
            var reference = new Uri(context.Profile.GetProperty("token").GetString()!);
            var credential = await context.SecretResolver.ResolveSecretAsync(reference, ct);
            return new ExampleAuthContext(credential);
        }
    }

    private sealed class ReferenceSecretResolver : ISecretResolver
    {
        public ValueTask<string> ResolveSecretAsync(Uri reference, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (reference != new Uri("kv://vault/token"))
            {
                throw new InvalidOperationException("Unexpected reference.");
            }
            return ValueTask.FromResult("resolved-by-auth-only");
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-09-30T12:00:00Z");
    }
}
