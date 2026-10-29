using System.Collections.ObjectModel;

namespace SelfHostedEvaluators.Plugins.Abstractions;

/// <summary>Records package implementation types without activating clients or contacting services.</summary>
public interface IPluginRegistrar
{
    /// <summary>Registers the package's single concrete sink plugin type.</summary>
    /// <typeparam name="T">The sink plugin implementation to activate later.</typeparam>
    void SetSinkPlugin<T>() where T : class, ISinkPlugin;
    /// <summary>Registers a concrete authentication factory under a case-insensitively unique name.</summary>
    /// <typeparam name="T">The authentication scheme implementation.</typeparam>
    /// <param name="name">A name not already used by a built-in or previously registered scheme.</param>
    void AddAuthScheme<T>(string name) where T : class, IAuthScheme;
}

/// <summary>Describes the implementation types registered by a package.</summary>
/// <param name="SinkPluginType">The package's concrete sink plugin type.</param>
/// <param name="AuthSchemes">The case-insensitive mapping of custom scheme names to factory types.</param>
public sealed record PluginRegistration(Type SinkPluginType, IReadOnlyDictionary<string, Type> AuthSchemes);

/// <summary>Collects one sink registration and rejects conflicting authentication scheme names.</summary>
public sealed class PluginRegistrar : IPluginRegistrar
{
    private readonly HashSet<string> _schemeNames;
    private readonly Dictionary<string, Type> _authSchemes = new(StringComparer.OrdinalIgnoreCase);
    private Type? _sinkPluginType;

    /// <summary>Creates a registrar with the authentication names already reserved by the host.</summary>
    /// <param name="existingAuthSchemeNames">Built-in and earlier package scheme names, or none.</param>
    public PluginRegistrar(IEnumerable<string>? existingAuthSchemeNames = null)
    {
        _schemeNames = new HashSet<string>(existingAuthSchemeNames ?? [], StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public void SetSinkPlugin<T>() where T : class, ISinkPlugin
    {
        if (_sinkPluginType is not null)
        {
            throw new InvalidOperationException("A package must register exactly one sink plugin.");
        }
        if (typeof(T).IsAbstract)
        {
            throw new ArgumentException("The sink plugin type must be concrete.");
        }
        _sinkPluginType = typeof(T);
    }

    /// <inheritdoc />
    public void AddAuthScheme<T>(string name) where T : class, IAuthScheme
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (typeof(T).IsAbstract)
        {
            throw new ArgumentException("The auth scheme type must be concrete.");
        }
        if (!_schemeNames.Add(name))
        {
            throw new InvalidOperationException($"Auth scheme '{name}' is already registered.");
        }
        _authSchemes.Add(name, typeof(T));
    }

    /// <summary>Builds an immutable registration after verifying that a sink was registered.</summary>
    /// <returns>The sink type and a copied, read-only scheme mapping.</returns>
    /// <exception cref="InvalidOperationException">No sink plugin was registered.</exception>
    public PluginRegistration Build()
    {
        if (_sinkPluginType is null)
        {
            throw new InvalidOperationException("A package must register exactly one sink plugin.");
        }
        return new PluginRegistration(_sinkPluginType,
            new ReadOnlyDictionary<string, Type>(
                new Dictionary<string, Type>(_authSchemes, StringComparer.OrdinalIgnoreCase)));
    }
}
