using SelfHostedEvaluators.Core.Configuration;
using SelfHostedEvaluators.Plugins.Abstractions;

namespace SelfHostedEvaluators.Core.Packages;

/// <summary>Validates configured package instances and activates a separate sink client for each one.</summary>
public sealed class PluginManager
{
    private readonly PackageCatalog catalog;
    private readonly Func<PluginConfiguration, CancellationToken, ValueTask<PluginInstanceContext>>? contextFactory;
    private readonly PluginContextFactory? ownedContextFactory;

    /// <summary>Creates a manager for callers that explicitly own all resources in supplied contexts.</summary>
    /// <param name="catalog">The externally owned loaded package catalog.</param>
    /// <param name="contextFactory">The factory whose caller retains ownership of supplied context services.</param>
    public PluginManager(PackageCatalog catalog,
        Func<PluginConfiguration, CancellationToken, ValueTask<PluginInstanceContext>> contextFactory)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    /// <summary>Creates a manager that ties each generated context lease to its returned client's lifetime.</summary>
    /// <param name="catalog">The externally owned loaded package catalog.</param>
    /// <param name="contextFactory">The factory creating owned per-instance resources.</param>
    public PluginManager(PackageCatalog catalog, PluginContextFactory contextFactory)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        ownedContextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    /// <summary>Validates and activates a configured sink, cleaning up partial activation on failure.</summary>
    /// <param name="configuration">The named package instance to activate.</param>
    /// <param name="ct">Cancels validation-to-activation work and propagates caller cancellation.</param>
    /// <returns>A caller-owned client, wrapping its context lease when using the owned-context factory.</returns>
    /// <exception cref="ConfigurationException">Validation or activation failed; only configuration locations are exposed.</exception>
    public async ValueTask<ISinkClient> CreateClientAsync(PluginConfiguration configuration, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ct.ThrowIfCancellationRequested();
        catalog.ThrowIfDisposed();
        var diagnostics = new List<ConfigurationDiagnostic>();
        ConfigurationValidator.ValidatePlugin(configuration, catalog, diagnostics);
        if (diagnostics.Count > 0) throw new ConfigurationException(diagnostics);
        PluginContextLease? resources = null;
        ISinkClient? client = null;
        try
        {
            var context = ownedContextFactory is null
                ? await contextFactory!(configuration, ct).ConfigureAwait(false)
                : (resources = await ownedContextFactory.CreateAsync(configuration, ct).ConfigureAwait(false)).Context;
            ct.ThrowIfCancellationRequested();
            client = await catalog.Packages[configuration.Package].Sink.CreateClientAsync(context, ct).ConfigureAwait(false)
                ?? throw ConfigurationException.At(configuration.Variable, "$");
            ct.ThrowIfCancellationRequested();
            return resources is null ? client : resources.Own(client);
        }
        catch (Exception error)
        {
            try { if (client is not null) await client.DisposeAsync().ConfigureAwait(false); }
            finally { if (resources is not null) await resources.DisposeAsync().ConfigureAwait(false); }
            if (error is OperationCanceledException && ct.IsCancellationRequested) throw;
            throw ConfigurationException.At(configuration.Variable, "$");
        }
    }
}
