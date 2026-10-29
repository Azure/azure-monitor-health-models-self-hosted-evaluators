using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SelfHostedEvaluators.Core.Auth;
using SelfHostedEvaluators.Core.Configuration;
using SelfHostedEvaluators.Core.Evaluation;
using SelfHostedEvaluators.Core.Packages;
using SelfHostedEvaluators.Core.Polling;
using SelfHostedEvaluators.Core.Reporting;
using SelfHostedEvaluators.Plugins.Abstractions;

namespace SelfHostedEvaluators.Host;

internal sealed class RuntimeWorker(HostDependencies dependencies, Redactor redactor, RuntimeHealth health,
    IHostApplicationLifetime lifetime, ILoggerFactory bootstrapLogs) : IHostedService
{
    private readonly object sync = new();
    private readonly CancellationTokenSource scheduling = new();
    private readonly CancellationTokenSource polls = new();
    private readonly CancellationTokenSource checks = new();
    private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? shutdown;
    private long shutdownStarted;
    private TimeSpan shutdownTimeout = TimeSpan.FromSeconds(5);
    private Task? run;
    private int exitCode;
    public int ExitCode => Volatile.Read(ref exitCode);
    public CancellationToken ShutdownToken { get { BeginShutdown(); return shutdown!.Token; } }
    public Task ResourcesReleased => released.Task;

    public Task StartAsync(CancellationToken ct)
    {
        lifetime.ApplicationStopping.Register(BeginShutdown);
        run = Task.Run(RunAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    public void BeginShutdown()
    {
        lock (sync)
        {
            if (shutdown is not null) return;
            health.Stop();
            shutdownStarted = dependencies.Time.GetTimestamp();
            shutdown = new(shutdownTimeout, dependencies.Time);
            Observe(scheduling.CancelAsync());
            Observe(checks.CancelAsync());
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        BeginShutdown();
        if (run is null) { released.TrySetResult(); return; }
        try { await run.WaitAsync(ShutdownToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ShutdownToken.IsCancellationRequested) { }
    }

    private async Task RunAsync()
    {
        PackageCatalog? catalog = null;
        TelemetrySession? telemetry = null;
        IKeyVaultSource? secretSource = null;
        AzureSdkCredentialSource? ownedCredentials = null;
        PollExecutor? executor = null;
        Scheduler? scheduler = null;
        Publisher? publisher = null;
        List<ModelClient> models = [];
        Task scheduleTask = Task.CompletedTask, checkTask = Task.CompletedTask, publishTask = Task.CompletedTask;
        var logger = bootstrapLogs.CreateLogger("SelfHostedEvaluators.Host");
        var configRecorded = false;
        var startRecorded = false;
        var packagesRecorded = false;
        var phase = "host_options";
        void RecordPackages()
        {
            if (packagesRecorded || catalog is null) return;
            foreach (var package in catalog.Packages.Values)
            {
                dependencies.Diagnostics.PackageLoaded(package.Manifest.Id, true);
                logger.LogInformation("Package load {Package} {Version} {Sha256} {Outcome} {Reason}",
                    package.Manifest.Id, package.Manifest.Version, package.Sha256, "accepted", "validated");
            }
            packagesRecorded = true;
        }
        try
        {
            var env = dependencies.ReadEnvironment().ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            var source = new EnvironmentConfigSource();
            var hostOptions = source.LoadHostOptions(env);
            lock (sync) { shutdownTimeout = hostOptions.ShutdownTimeout; }
            phase = "package_catalog";
            catalog = PackageCatalog.Load(hostOptions.PackagesDirectory);
            phase = "environment_snapshot";
            var snapshot = source.Load(env);
            phase = "configuration_validation";
            ConfigurationValidator.Validate(snapshot, catalog, ConfigurationValidationMode.Runtime);
            scheduling.Token.ThrowIfCancellationRequested();
            phase = "telemetry_initialization";
            telemetry = new(hostOptions, dependencies, redactor);
            await telemetry.StartAsync(scheduling.Token).ConfigureAwait(false);
            logger = telemetry.Logger;
            dependencies.Diagnostics.HostStarted();
            logger.LogInformation(new EventId(1000), "Evaluator host started {Event} {Version} {Models} {Plugins} {Packages}",
                "evaluator.host.starts", "1.0.0", snapshot.Models.Length, snapshot.Plugins.Length, catalog.Packages.Count);
            startRecorded = true;
            RecordPackages();
            dependencies.Diagnostics.ConfigLoaded(true);
            configRecorded = true;
            logger.LogDebug("Configuration load {Outcome} {Plugins} {Models} {Reason}", "accepted",
                snapshot.Plugins.Length, snapshot.Models.Length, "validated");
            scheduling.Token.ThrowIfCancellationRequested();

            phase = "sink_initialization";
            secretSource = dependencies.SecretSource?.Invoke(snapshot.Host) ?? new DeferredKeyVaultSource(snapshot.Host.AzureClientId);
            var secrets = new SecretResolver(secretSource, snapshot.Host.SecretCacheTtl, dependencies.Time, redactor, logger);
            var credentials = dependencies.AzureCredentials ?? (ownedCredentials = new(dependencies.Transports));
            var registry = new AuthRegistry(secrets, new(secrets, credentials, dependencies.Time, redactor), redactor,
                dependencies.Time, dependencies.Transports, logger);
            registry.RegisterPackageSchemes(catalog.AuthSchemes);
            var manager = new PluginManager(catalog, new PluginContextFactory(catalog, registry, redactor, logger,
                dependencies.Time, dependencies.Diagnostics));
            executor = new(snapshot, manager, dependencies.Time, dependencies.Diagnostics, logger);
            await executor.InitializeAsync(snapshot.Plugins.Where(p => catalog.Packages.ContainsKey(p.Package)).Select(p => p.Name),
                scheduling.Token).ConfigureAwait(false);
            phase = "model_initialization";
            foreach (var model in snapshot.Models)
                models.Add(await ModelClient.CreateAsync(model.Name, model.HealthModel, registry, dependencies.Time,
                    redactor, dependencies.Diagnostics, logger, scheduling.Token).ConfigureAwait(false));
            scheduling.Token.ThrowIfCancellationRequested();
            phase = "scheduler";

            var lookup = snapshot.Models.SelectMany(m => m.Signals.Select(s =>
                (Key: new SignalKey(m.HealthModel.DataPlaneUri, s.Entity, s.Name), Signal: s, Model: m.Name)))
                .ToDictionary(s => s.Key);
            var states = new Dictionary<SignalKey, HealthState>();
            var stateSync = new object();
            var evaluator = new Evaluator(redactor, dependencies.Time, dependencies.Diagnostics);
            var builder = new ReportBuilder(dependencies.Time, redactor);
            publisher = new(models, dependencies.Time, redactor, dependencies.Diagnostics, logger);
            publishTask = publisher.RunAsync(checks.Token);
            checkTask = Task.Run(() => new ModelChecks(models, dependencies.Time, redactor, dependencies.Diagnostics, logger)
                .RunAsync(snapshot, checks.Token), CancellationToken.None);
            scheduler = new(dependencies.Time, dependencies.Diagnostics);
            scheduleTask = scheduler.RunAsync(snapshot, async (batch, _) =>
            {
                if (scheduling.IsCancellationRequested) return;
                // A schedule stop does not cancel actual polls; only the shared drain deadline does.
                var results = await executor.ExecuteAsync(batch, polls.Token).ConfigureAwait(false);
                foreach (var result in results)
                {
                    var configured = lookup[result.Key];
                    var evaluated = evaluator.Evaluate(result.Key, configured.Signal, result);
                    lock (stateSync)
                    {
                        if (!states.TryGetValue(result.Key, out var previous) || previous != evaluated.State)
                        {
                            states[result.Key] = evaluated.State;
                            logger.LogInformation("Signal state changed {Model} {Plugin} {Entity} {Signal} {State}",
                                configured.Model, batch.PluginName, result.Key.Entity, result.Key.Name, evaluated.State);
                        }
                    }
                    await publisher.EnqueueAsync(builder.Build(evaluated), polls.Token).ConfigureAwait(false);
                }
            }, scheduling.Token);
            dependencies.Diagnostics.SetActiveSignals(lookup.Count);
            health.Activate(scheduler);
            await scheduleTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (scheduling.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (error is ConfigurationException configurationError) HostRunner.WriteDiagnostics(configurationError);
            Interlocked.Exchange(ref exitCode, 1);
            if (!configRecorded) dependencies.Diagnostics.ConfigLoaded(false);
            if (phase == "package_catalog")
            {
                dependencies.Diagnostics.PackageLoaded("catalog", false);
                logger.LogDebug("Package load {Package} {Outcome} {Reason}", "catalog", "rejected", "catalog_validation_failed");
            }
            logger.LogDebug("Configuration/startup load {Outcome} {Reason}", "rejected", phase);
        }
        finally
        {
            RecordPackages();
            if (!startRecorded)
            {
                dependencies.Diagnostics.HostStarted();
                logger.LogInformation(new EventId(1000), "Evaluator host started {Event} {Version}", "evaluator.host.starts", "1.0.0");
            }
            lifetime.StopApplication();
            BeginShutdown();
            dependencies.Diagnostics.SetActiveSignals(0);
            try
            {
                await ObserveCompletion(scheduleTask).WaitAsync(ShutdownToken).ConfigureAwait(false);
                if (scheduler is not null) await scheduler.DrainAsync(ShutdownToken).ConfigureAwait(false);
                if (executor is not null) await executor.DrainAsync(ShutdownToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ShutdownToken.IsCancellationRequested) { Observe(polls.CancelAsync()); }
            catch (Exception) { Interlocked.Exchange(ref exitCode, 1); }
            try
            {
                if (publisher is not null)
                    await publisher.FlushAsync(ShutdownToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ShutdownToken.IsCancellationRequested) { }
            catch (Exception) { Interlocked.Exchange(ref exitCode, 1); }

            // Keep all owners alive until actual calls, including canceled/hung calls, finish.
            var cleanup = Task.Run(async () =>
            {
                try
                {
                    if (scheduler is not null) await ObserveCompletion(scheduler.DisposeAsync().AsTask()).ConfigureAwait(false);
                    if (executor is not null) await ObserveCompletion(executor.DisposeAsync().AsTask()).ConfigureAwait(false);
                    await ObserveCompletion(Task.WhenAll(checkTask, publishTask,
                        publisher?.Completion ?? Task.CompletedTask)).ConfigureAwait(false);
                    foreach (var model in models) Release(model.Dispose);
                    if (secretSource is IDisposable disposable) Release(disposable.Dispose);
                    if (ownedCredentials is not null) Release(ownedCredentials.Dispose);
                    if (catalog is not null) Release(catalog.Dispose);
                    if (telemetry is not null)
                    {
                        try
                        {
                            telemetry.Flush(RemainingMilliseconds());
                            await telemetry.StopAsync(ShutdownToken).ConfigureAwait(false);
                        }
                        finally { telemetry.Dispose(); }
                    }
                }
                finally { released.TrySetResult(); }
            });
            Observe(cleanup);
            try { await cleanup.WaitAsync(ShutdownToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ShutdownToken.IsCancellationRequested) { }
        }
    }

    private int RemainingMilliseconds() => ShutdownToken.IsCancellationRequested ? 0 :
        Math.Max(0, (int)Math.Min(int.MaxValue,
            (shutdownTimeout - dependencies.Time.GetElapsedTime(shutdownStarted)).TotalMilliseconds));

    private static void Observe(Task task) =>
        _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

    private static async Task ObserveCompletion(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception) { }
    }

    private static void Release(Action dispose)
    {
        try { dispose(); }
        catch (Exception) { }
    }

    private sealed class DeferredKeyVaultSource(string? clientId) : IKeyVaultSource, IDisposable
    {
        private readonly Lazy<KeyVaultSecretSource> source = new(() =>
            new(clientId ?? throw new InvalidOperationException("A bootstrap UAMI is required.")));
        public ValueTask<string> GetSecretAsync(KeyVaultReference reference, CancellationToken ct) =>
            source.Value.GetSecretAsync(reference, ct);
        public void Dispose() { if (source.IsValueCreated) source.Value.Dispose(); }
    }
}
