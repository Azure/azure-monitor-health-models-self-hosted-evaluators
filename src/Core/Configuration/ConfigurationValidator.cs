using SelfHostedEvaluators.Core.Packages;

namespace SelfHostedEvaluators.Core.Configuration;

/// <summary>Controls whether unresolved plugin references can run as Unknown signals.</summary>
public enum ConfigurationValidationMode
{
    /// <summary>Allows missing plugin configurations or packages so their signals can report Unknown.</summary>
    Runtime,
    /// <summary>Rejects unresolved references before deployment.</summary>
    Deployment
}

/// <summary>Validates a parsed snapshot against the actual bundled packages without contacting services.</summary>
public static class ConfigurationValidator
{
    /// <summary>Checks package versions, capabilities, auth schemes, settings and known signal queries.</summary>
    /// <param name="snapshot">The complete, locally validated configuration.</param>
    /// <param name="catalog">The loaded package catalog to validate against.</param>
    /// <param name="mode">Whether unresolved plugin references are permitted.</param>
    /// <exception cref="ConfigurationException">Package-aware validation found invalid locations.</exception>
    public static void Validate(ConfigSnapshot snapshot, PackageCatalog catalog, ConfigurationValidationMode mode)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(catalog);
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        catalog.ThrowIfDisposed();
        var errors = new List<ConfigurationDiagnostic>();
        foreach (var plugin in snapshot.Plugins)
        {
            if (mode == ConfigurationValidationMode.Runtime && !catalog.Packages.ContainsKey(plugin.Package)) continue;
            ValidatePlugin(plugin, catalog, errors);
        }
        var plugins = snapshot.Plugins.ToDictionary(p => p.Name, StringComparer.Ordinal);
        foreach (var model in snapshot.Models)
        {
            CheckAuth(model.HealthModel.Auth, catalog, model.Variable, "$.healthModel.auth", errors);
            foreach (var signal in model.Signals)
            {
                if (!plugins.TryGetValue(signal.Plugin, out var plugin) || !catalog.Packages.TryGetValue(plugin.Package, out var package))
                {
                    if (mode == ConfigurationValidationMode.Deployment)
                        errors.Add(new(model.Variable, signal.PropertyPath + ".plugin"));
                    continue;
                }
                try
                {
                    if (!package.Sink.ValidateQuery(signal.Query).IsValid)
                        errors.Add(new(model.Variable, signal.PropertyPath + ".query"));
                }
                catch (Exception) { errors.Add(new(model.Variable, signal.PropertyPath + ".query")); }
            }
        }
        if (errors.Count > 0) throw new ConfigurationException(errors);
    }

    internal static void ValidatePlugin(PluginConfiguration plugin, PackageCatalog catalog, List<ConfigurationDiagnostic> errors)
    {
        CheckAuth(plugin.Auth, catalog, plugin.Variable, "$.auth", errors);
        if (!catalog.Packages.TryGetValue(plugin.Package, out var package))
        {
            errors.Add(new(plugin.Variable, "$.package"));
            return;
        }
        if (plugin.PackageVersion is not null && !PackageVersionConstraint.Matches(package.Manifest.Version, plugin.PackageVersion))
            errors.Add(new(plugin.Variable, "$.packageVersion"));
        if (plugin.Batch.MaxSize > package.Capabilities.MaxBatchSizeCap)
            errors.Add(new(plugin.Variable, "$.batch.maxSize"));
        if (package.Capabilities.RequiredEndpoints.Any(name => !plugin.Endpoints.ContainsKey(name)) ||
            (package.Capabilities.AllowedHosts is { } allowed &&
             plugin.Endpoints.Values.Any(endpoint => !allowed.Contains(endpoint.IdnHost, StringComparer.OrdinalIgnoreCase))))
            errors.Add(new(plugin.Variable, "$.endpoints"));
        try
        {
            if (!package.Sink.ValidateSettings(plugin.Settings).IsValid)
                errors.Add(new(plugin.Variable, "$.settings"));
        }
        catch (Exception) { errors.Add(new(plugin.Variable, "$.settings")); }
    }

    private static void CheckAuth(AuthProfile auth, PackageCatalog catalog, string variable, string path,
        List<ConfigurationDiagnostic> errors)
    {
        if (!catalog.KnownAuthSchemes.Contains(auth.Type)) errors.Add(new(variable, path + ".type"));
    }
}
