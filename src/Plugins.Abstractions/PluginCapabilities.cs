namespace SelfHostedEvaluators.Plugins.Abstractions;

/// <summary>Declares immutable package limits and endpoint requirements for offline validation.</summary>
public sealed class PluginCapabilities
{
    /// <summary>Creates capabilities, copying endpoint and host lists.</summary>
    /// <param name="maxBatchSizeCap">The positive maximum number of queries supported in one batch.</param>
    /// <param name="requiredEndpoints">The exact, case-sensitive endpoint names required by the sink.</param>
    /// <param name="allowedHosts">Allowed exact DNS hosts, matched ignoring case, or null for no package restriction.</param>
    public PluginCapabilities(
        int maxBatchSizeCap,
        IReadOnlyList<string> requiredEndpoints,
        IReadOnlyList<string>? allowedHosts = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBatchSizeCap);
        ArgumentNullException.ThrowIfNull(requiredEndpoints);
        MaxBatchSizeCap = maxBatchSizeCap;
        RequiredEndpoints = Array.AsReadOnly(requiredEndpoints.ToArray());
        AllowedHosts = allowedHosts is null ? null : Array.AsReadOnly(allowedHosts.ToArray());
    }

    /// <summary>Gets the upper bound for the configured batch size.</summary>
    public int MaxBatchSizeCap { get; }
    /// <summary>Gets the required named HTTP endpoints.</summary>
    public IReadOnlyList<string> RequiredEndpoints { get; }
    /// <summary>Gets the exact allowed hosts, or null when the package imposes no host allowlist.</summary>
    public IReadOnlyList<string>? AllowedHosts { get; }
}
