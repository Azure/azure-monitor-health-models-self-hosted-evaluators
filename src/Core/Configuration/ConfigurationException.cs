namespace SelfHostedEvaluators.Core.Configuration;

/// <summary>Identifies a configuration problem without disclosing its value.</summary>
/// <param name="Variable">The environment variable or manifest location.</param>
/// <param name="PropertyPath">The JSON property path containing the problem.</param>
public sealed record ConfigurationDiagnostic(string Variable, string PropertyPath);

/// <summary>Reports configuration errors using locations only, never configuration or credential payloads.</summary>
public sealed class ConfigurationException : Exception
{
    /// <summary>Copies diagnostic locations into an exception with a location-only message.</summary>
    /// <param name="diagnostics">The rejected configuration locations.</param>
    public ConfigurationException(IEnumerable<ConfigurationDiagnostic> diagnostics)
        : this(diagnostics.ToArray()) { }

    private ConfigurationException(ConfigurationDiagnostic[] diagnostics)
        : base(string.Join(Environment.NewLine, diagnostics.Select(d => $"{d.Variable}:{d.PropertyPath}"))) =>
        Diagnostics = Array.AsReadOnly(diagnostics);

    /// <summary>Gets the immutable collection of rejected configuration locations.</summary>
    public IReadOnlyList<ConfigurationDiagnostic> Diagnostics { get; }

    internal static ConfigurationException At(string variable, string path) => new([new(variable, path)]);
}
