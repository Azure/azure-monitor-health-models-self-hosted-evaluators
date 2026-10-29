using Azure.Core;
using Azure.Core.Pipeline;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using SelfHostedEvaluators.Core.Auth;
using SelfHostedEvaluators.Core.Diagnostics;

namespace SelfHostedEvaluators.Host;

/// <summary>Provides host service boundaries for production defaults and offline composition of the real pipeline.</summary>
public sealed class HostDependencies
{
    /// <summary>Gets the reader that supplies a complete environment snapshot at startup.</summary>
    public Func<IReadOnlyDictionary<string, string>> ReadEnvironment { get; init; } = () =>
        Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(p => (string)p.Key, p => (string)p.Value!, StringComparer.Ordinal);
    /// <summary>Gets the runtime clock, defaulting to system time.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;
    /// <summary>Gets the port used by the loopback-only readiness and liveness endpoints.</summary>
    public int ProbePort { get; init; } = 8080;
    /// <summary>Gets the runtime metric instruments, defaulting to the shared instance.</summary>
    public RuntimeDiagnostics Diagnostics { get; init; } = RuntimeDiagnostics.Shared;
    /// <summary>Gets an optional secret-source factory; the runtime disposes the created source when supported.</summary>
    public Func<Core.Configuration.HostOptions, IKeyVaultSource>? SecretSource { get; init; }
    /// <summary>Gets an optional externally owned credential source instead of the Azure SDK default.</summary>
    public IAzureCredentialSource? AzureCredentials { get; init; }
    /// <summary>Gets an optional transport factory that must preserve the host's hardened settings.</summary>
    public IHttpTransportFactory? Transports { get; init; }
    /// <summary>Gets an optional credential for authenticated Application Insights export.</summary>
    public TokenCredential? TelemetryCredential { get; init; }
    /// <summary>Gets an optional Azure Monitor exporter transport for controlled export destinations.</summary>
    public HttpPipelineTransport? AzureMonitorTransport { get; init; }
    /// <summary>Gets an optional exporter configuration replacing default Azure Monitor or console exporters.</summary>
    public Action<OpenTelemetryBuilder>? Exporters { get; init; }
    /// <summary>Gets an optional logging provider included in host diagnostics.</summary>
    public ILoggerProvider? LoggerProvider { get; init; }
}
