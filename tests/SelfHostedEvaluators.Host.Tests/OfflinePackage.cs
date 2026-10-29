using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SelfHostedEvaluators.Plugins.Abstractions;

namespace SelfHostedEvaluators.Host.Tests;

public sealed class OfflinePackage : IPluginPackage
{
    public void Register(IPluginRegistrar registrar)
    {
        registrar.SetSinkPlugin<OfflineSink>();
        registrar.AddAuthScheme<OfflineAuth>("fixtureLocal");
    }
}

public sealed class OfflineSink : ISinkPlugin
{
    public PluginCapabilities Capabilities { get; } = new(100, []);
    public ValidationResult ValidateSettings(JsonElement settings) => ValidationResult.Success;
    public ValidationResult ValidateQuery(JsonElement query) => ValidationResult.Success;
    public ValueTask<ISinkClient> CreateClientAsync(PluginInstanceContext context, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        context.Logger.LogDebug("Fixture client created {Client}", id);
        return ValueTask.FromResult<ISinkClient>(new Client(context.Logger, id));
    }
    private sealed class Client(ILogger logger, string id) : ISinkClient
    {
        public Task<IReadOnlyList<SignalResult>> QueryBatchAsync(IReadOnlyList<SignalQuery> batch, QueryWindow window, CancellationToken ct)
        {
            logger.LogDebug("Fixture client polled {Client}", id);
            return Task.FromResult<IReadOnlyList<SignalResult>>(batch.Select(q =>
                (SignalResult)new SignalResult.Observation(q.Key, 5, null, window.ScheduledAt)).ToArray());
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed class OfflineAuth : IAuthScheme
{
    public ValueTask<IAuthContext> CreateContextAsync(AuthSchemeContext context, CancellationToken ct)
    {
        context.Logger.LogDebug("Fixture auth initialized");
        return ValueTask.FromResult<IAuthContext>(new Context());
    }
    private sealed class Context : IAuthContext
    {
        public string Scheme => "fixtureLocal";
        public ValueTask AuthenticateAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "local-custom-auth");
            return ValueTask.CompletedTask;
        }
        public ValueTask ConfigureHttpHandlerAsync(Uri destination, HttpClientHandler handler, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask AuthenticateWebSocketAsync(Uri destination, ClientWebSocketOptions options, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
