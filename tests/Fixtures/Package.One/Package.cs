using System.Text.Json;
using System.Threading.RateLimiting;
using SelfHostedEvaluators.Plugins.Abstractions;

namespace Fixtures;

public class Package : IPluginPackage
{
    public void Register(IPluginRegistrar registrar) => registrar.SetSinkPlugin<Sink>();
}

public class MissingSinkPackage : IPluginPackage
{
    public void Register(IPluginRegistrar registrar) { }
}

public class DoubleSinkPackage : IPluginPackage
{
    public void Register(IPluginRegistrar registrar)
    {
        registrar.SetSinkPlugin<Sink>();
        registrar.SetSinkPlugin<Sink>();
    }
}

public class AuthPackage : IPluginPackage
{
    public void Register(IPluginRegistrar registrar)
    {
        registrar.SetSinkPlugin<Sink>();
        registrar.AddAuthScheme<Auth>("fixtureAuth");
    }
}

public class ReservedAuthPackage : IPluginPackage
{
    public void Register(IPluginRegistrar registrar)
    {
        registrar.SetSinkPlugin<Sink>();
        registrar.AddAuthScheme<Auth>("BEARER");
    }
}

public class Auth : IAuthScheme
{
    public ValueTask<IAuthContext> CreateContextAsync(AuthSchemeContext context, CancellationToken ct) =>
        throw new NotSupportedException();
}

public class Sink : ISinkPlugin
{
    public static Type RateLimiterType => typeof(RateLimiter);
    public PluginCapabilities Capabilities => new(10, ["default"], ["sink.test"]);
    public ValidationResult ValidateSettings(JsonElement settings) => Validate(settings);
    public ValidationResult ValidateQuery(JsonElement query) => Validate(query);
    private static ValidationResult Validate(JsonElement json)
    {
        if (json.TryGetProperty("throw", out _))
            throw new InvalidOperationException("sensitive");
        return json.TryGetProperty("reject", out _) ? ValidationResult.Failed("sensitive") : ValidationResult.Success;
    }
    public ValueTask<ISinkClient> CreateClientAsync(PluginInstanceContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (context.Settings.TryGetProperty("createThrows", out _))
            throw new InvalidOperationException("sensitive");
        return ValueTask.FromResult<ISinkClient>(new Client(context));
    }

    private sealed class Client(PluginInstanceContext context) : ISinkClient
    {
        private readonly string id = Guid.NewGuid().ToString();
        public Task<IReadOnlyList<SignalResult>> QueryBatchAsync(
            IReadOnlyList<SignalQuery> batch, QueryWindow window, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SignalResult>>(batch.Select(q => (SignalResult)new SignalResult.Observation(
                q.Key, Fixture.PrivateDependency.Marker.Value, context.PluginName, window.ScheduledAt,
                id + ":" + context.RateLimiter.GetType().Name)).ToArray());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
