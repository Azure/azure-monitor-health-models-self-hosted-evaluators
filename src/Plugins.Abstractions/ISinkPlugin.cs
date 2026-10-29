using System.Text.Json;

namespace SelfHostedEvaluators.Plugins.Abstractions;

/// <summary>Describes a sink's capabilities, offline validation and per-configuration client factory.</summary>
public interface ISinkPlugin
{
    /// <summary>Gets the package's batch cap, required endpoint names and optional allowed hosts.</summary>
    PluginCapabilities Capabilities { get; }
    /// <summary>Validates package-specific settings without network access or secret resolution.</summary>
    /// <param name="settings">The settings object from a plugin configuration.</param>
    /// <returns>Success or nonempty, redacted errors that do not echo configuration values.</returns>
    ValidationResult ValidateSettings(JsonElement settings);
    /// <summary>Validates a sink-specific signal query without executing it.</summary>
    /// <param name="query">The query object from a signal configuration.</param>
    /// <returns>Success or nonempty, redacted errors.</returns>
    ValidationResult ValidateQuery(JsonElement query);
    /// <summary>Creates a new sink client for one named plugin configuration.</summary>
    /// <param name="context">The configuration and host-owned services for this instance.</param>
    /// <param name="ct">Cancels activation; pre-cancellation must be observed.</param>
    /// <returns>A separate client whose lifetime is managed by the host.</returns>
    ValueTask<ISinkClient> CreateClientAsync(PluginInstanceContext context, CancellationToken ct);
}
