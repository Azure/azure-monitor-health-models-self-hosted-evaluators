using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Logging.Abstractions;
using SelfHostedEvaluators.Core.Configuration;
using SelfHostedEvaluators.Plugins.Abstractions;

namespace SelfHostedEvaluators.Core.Tests;

internal sealed class PackageFixture : IDisposable
{
    internal static readonly List<string> Directories = [];
    public string Root { get; } = EmptyDirectory();
    private readonly List<IDisposable> resources = [];
    public static string EmptyDirectory()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "package-work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        Directories.Add(path);
        return path;
    }
    public string Add(string id = "one", string version = "1.0.0", string entry = "Package",
        string abstractions = "1.0.0", string assembly = "Fixture.Package.dll", string fixture = "One")
    {
        var directory = Path.Combine(Root, id, version);
        Directory.CreateDirectory(directory);
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(repo.FullName, "SelfHostedEvaluators.slnx")))
            repo = repo.Parent!;
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var source = Path.Combine(repo.FullName, "tests", "Fixtures", "Package." + fixture, "bin", configuration, "net10.0");
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(directory, Path.GetFileName(file)), true);
        File.WriteAllText(Path.Combine(directory, "plugin.json"), JsonSerializer.Serialize(new
        {
            id, version, assembly, entryType = "Fixtures." + entry, abstractionsVersion = abstractions
        }));
        return directory;
    }

    public ValueTask<PluginInstanceContext> Context(PluginConfiguration configuration, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = 0 });
        resources.Add(limiter);
        return ValueTask.FromResult(new PluginInstanceContext(configuration.Name, configuration.Settings, configuration.Batch.MaxSize,
            new Dictionary<string, HttpClient>(), new NoAuth(), limiter, NullLogger.Instance, TimeProvider.System));
    }

    public void Dispose()
    {
        foreach (var resource in resources) resource.Dispose();
    }

    private sealed class NoAuth : IAuthContext
    {
        public string Scheme => "none";
        public ValueTask AuthenticateAsync(HttpRequestMessage request, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask ConfigureHttpHandlerAsync(Uri destination, HttpClientHandler handler, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask AuthenticateWebSocketAsync(Uri destination, ClientWebSocketOptions options, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

}

[SetUpFixture]
public sealed class PackageFixtureCleanup
{
    [OneTimeTearDown]
    public void Cleanup()
    {
        // Collectible ALCs unload after test stack frames have released their types.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        foreach (var directory in PackageFixture.Directories)
            Directory.Delete(directory, true);
        PackageFixture.Directories.Clear();
    }
}
