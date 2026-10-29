using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;
using SelfHostedEvaluators.Core.Auth;
using SelfHostedEvaluators.Core.Configuration;
using SelfHostedEvaluators.Core.Packages;

namespace SelfHostedEvaluators.Host;

/// <summary>Dispatches offline validation commands or composes the long-running evaluator host.</summary>
/// <param name="dependencies">Optional service boundaries, defaulting to production implementations.</param>
public sealed class HostRunner(HostDependencies? dependencies = null)
{
    private readonly HostDependencies dependencies = dependencies ?? new();

    /// <summary>Executes a host command with location-only validation errors and bounded runtime shutdown.</summary>
    /// <param name="args">No arguments or run, validate, or validate-packages with a directory.</param>
    /// <param name="ct">Requests command cancellation or graceful runtime shutdown.</param>
    /// <returns>Zero for success; one for invalid arguments, failed validation or host failure.</returns>
    public async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        if (args is ["validate-packages", var directory]) return ValidatePackages(directory);
        if (args.Length > 1 || (args.Length == 1 && args[0] is not ("run" or "validate"))) return 1;
        if (args is ["validate"]) return Validate();

        var redactor = new Redactor();
        var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(new() { Args = [] });
        builder.Services.Configure<Microsoft.Extensions.Hosting.HostOptions>(options => options.ShutdownTimeout = Timeout.InfiniteTimeSpan);
        builder.Services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Debug);
            logging.AddFilter("Microsoft", LogLevel.Warning);
            logging.AddJsonConsole();
            if (dependencies.LoggerProvider is { } provider) logging.AddProvider(provider);
        });
        builder.Services.AddSingleton<ILoggerFactory>(sp =>
            new SafeLoggerFactory(ActivatorUtilities.CreateInstance<LoggerFactory>(sp), redactor));
        builder.Services.AddSingleton(dependencies);
        builder.Services.AddSingleton(redactor);
        builder.Services.AddSingleton(new RuntimeHealth(dependencies.Time));
        builder.Services.AddSingleton<RuntimeWorker>();
        builder.Services.AddHostedService<HealthEndpoints>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<RuntimeWorker>());
        builder.Services.AddSingleton<IHostLifetime, Microsoft.Extensions.Hosting.Internal.ConsoleLifetime>();
        var host = builder.Build();
        var worker = host.Services.GetRequiredService<RuntimeWorker>();
        try
        {
            await host.StartAsync(ct).ConfigureAwait(false);
            var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
            using var registration = ct.Register(lifetime.StopApplication);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, lifetime.ApplicationStopping).ConfigureAwait(false); }
            catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested) { }
            worker.BeginShutdown();
            try { await host.StopAsync(worker.ShutdownToken).WaitAsync(worker.ShutdownToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (worker.ShutdownToken.IsCancellationRequested) { }
            return worker.ExitCode;
        }
        catch (Exception)
        {
            worker.BeginShutdown();
            Console.Error.WriteLine("Evaluator host failed (startup).");
            try
            {
                await worker.StopAsync(worker.ShutdownToken).WaitAsync(worker.ShutdownToken).ConfigureAwait(false);
                await host.StopAsync(worker.ShutdownToken).WaitAsync(worker.ShutdownToken).ConfigureAwait(false);
            }
            catch (Exception) { }
            return 1;
        }
        finally
        {
            // Do not let Generic Host disposal tear down telemetry under retained plugin/auth calls.
            var cleanup = Task.Run(async () =>
            {
                await worker.ResourcesReleased.ConfigureAwait(false);
                if (host is IAsyncDisposable asyncHost) await asyncHost.DisposeAsync().ConfigureAwait(false);
                else host.Dispose();
            });
            _ = cleanup.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            try { await cleanup.WaitAsync(worker.ShutdownToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (worker.ShutdownToken.IsCancellationRequested) { }
        }
    }

    private int Validate()
    {
        try
        {
            var env = dependencies.ReadEnvironment().ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            using var catalog = PackageCatalog.Load(env.GetValueOrDefault("SHE_PACKAGES_DIR", "/app/packages"));
            var snapshot = new EnvironmentConfigSource().Load(env);
            ConfigurationValidator.Validate(snapshot, catalog, ConfigurationValidationMode.Deployment);
            Console.WriteLine("Configuration accepted (deployment).");
            return 0;
        }
        catch (ConfigurationException error)
        {
            WriteDiagnostics(error);
            Console.Error.WriteLine("Configuration rejected (deployment validation).");
            return 1;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("Configuration rejected (deployment validation).");
            return 1;
        }
    }

    private static int ValidatePackages(string directory)
    {
        try
        {
            using var catalog = PackageCatalog.Load(directory);
            if (catalog.Packages.Count == 0) return 1;
            Console.WriteLine("Package catalog accepted.");
            return 0;
        }
        catch (ConfigurationException error)
        {
            WriteDiagnostics(error);
            return 1;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("Package catalog rejected.");
            return 1;
        }
    }

    internal static void WriteDiagnostics(ConfigurationException error)
    {
        foreach (var diagnostic in error.Diagnostics)
        {
            var variable = Regex.IsMatch(diagnostic.Variable, @"^SHE_[a-zA-Z0-9_]{1,200}\z|^SHE_(PLUGIN|MODEL)_<KEY>\z|^AZURE_CLIENT_ID\z|^APPLICATIONINSIGHTS_CONNECTION_STRING\z")
                ? diagnostic.Variable : "SHE_PACKAGES_DIR";
            var path = "$";
            foreach (Match match in Regex.Matches(diagnostic.PropertyPath, @"\.([a-zA-Z][a-zA-Z0-9]*)(\[[0-9]+\])?"))
            {
                if (match.Index != path.Length || !SafeDiagnosticProperties.Contains(match.Groups[1].Value)) break;
                path += match.Value;
            }
            Console.Error.WriteLine($"{variable}:{path}");
        }
    }

    private static readonly HashSet<string> SafeDiagnosticProperties = new(StringComparer.Ordinal)
    {
        "schemaVersion", "name", "package", "packageVersion", "endpoints", "auth", "batch", "maxSize",
        "rateLimit", "requests", "per", "concurrency", "timeout", "defaults", "settings", "interval",
        "ttlMultiplier", "expiresInMinutes", "healthModel", "signals", "entity", "plugin", "query",
        "evaluation", "context", "dataPlaneUri", "apiVersion", "tokenScope", "reportsPerMinute",
        "type", "headers", "queryParameter", "secret", "token", "username", "password", "certificate",
        "tokenEndpoint", "clientId", "clientSecret", "scopes", "azure", "scope", "tenantId", "tokenFile",
        "degraded", "unhealthy", "operator", "threshold", "stateMap", "noData",
        "id", "version", "assembly", "entryType", "abstractionsVersion"
    };
}
