using System.Diagnostics;
using System.Text.Json;
using SelfHostedEvaluators.Plugins.Static;

namespace SelfHostedEvaluators.Host.Tests;

internal sealed class HostFixture : IDisposable
{
    private static readonly Lazy<string> SharedRoot = new(CreateSharedRoot);
    private static readonly SemaphoreSlim ConsoleGate = new(1, 1);
    private static readonly string HostAssembly = FindHostAssembly();

    private static readonly Lazy<string> OfflineRoot = new(CreateOfflineRoot);
    internal static string PackagesRoot => SharedRoot.Value;
    internal static string OfflinePackagesRoot => OfflineRoot.Value;
    internal static bool Created => SharedRoot.IsValueCreated;
    internal static bool OfflineCreated => OfflineRoot.IsValueCreated;
    public string Root => SharedRoot.Value;
    public Dictionary<string, string> Environment { get; }

    private static string CreateSharedRoot()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "host-work", Guid.NewGuid().ToString("N"));
        // Built once per test run and shared by every fixture.
        var package = Path.Combine(root, "static", "1.0.0");
        Directory.CreateDirectory(package);
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll"))
            File.Copy(file, Path.Combine(package, Path.GetFileName(file)));
        WriteManifest(package, "static", typeof(StaticPackage));
        return root;
    }

    private static string CreateOfflineRoot()
    {
        var root = SharedRoot.Value + "-offline";
        CopyDirectory(Path.Combine(SharedRoot.Value, "static"), Path.Combine(root, "static"));
        var offline = Path.Combine(root, "offline", "1.0.0");
        CopyDirectory(Path.Combine(SharedRoot.Value, "static", "1.0.0"), offline);
        WriteManifest(offline, "offline", typeof(OfflinePackage));
        return root;
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
    }

    private static void WriteManifest(string directory, string id, Type entry) =>
        File.WriteAllText(Path.Combine(directory, "plugin.json"), JsonSerializer.Serialize(new
        {
            id, version = "1.0.0", assembly = Path.GetFileName(entry.Assembly.Location),
            entryType = entry.FullName, abstractionsVersion = "1.0.0"
        }));

    private static string FindHostAssembly()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(repo!.FullName, "SelfHostedEvaluators.slnx"))) repo = repo.Parent;
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        return Path.Combine(repo.FullName, "src", "Host", "bin", configuration, "net10.0", "SelfHostedEvaluators.Host.dll");
    }

    public HostFixture()
    {
        Environment = new()
        {
            ["SHE_PACKAGES_DIR"] = Root,
            ["SHE_PLUGIN_STATIC"] = """{"schemaVersion":1,"name":"static-one","package":"static","endpoints":{"default":"https://sink.test"},"auth":{"type":"none"}}""",
            ["SHE_MODEL_ONE"] = """{"schemaVersion":1,"name":"model-one","healthModel":{"dataPlaneUri":"https://model.test"},"signals":[{"entity":"entity-one","name":"signal-one","plugin":"static-one","query":{"kind":"observation","value":5},"evaluation":{"unhealthy":{"operator":"GreaterThan","threshold":10}}}]}""",
            ["SHE_SHUTDOWN_TIMEOUT"] = "1s"
        };
    }

    public async Task<(int Code, string Output, string Error)> Cli(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        start.ArgumentList.Add(HostAssembly);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("SHE_", StringComparison.Ordinal) ||
                     k is "AZURE_CLIENT_ID" or "APPLICATIONINSIGHTS_CONNECTION_STRING").ToArray())
            start.Environment.Remove(key);
        foreach (var entry in Environment) start.Environment[entry.Key] = entry.Value;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
        catch { process.Kill(entireProcessTree: true); throw; }
        var stderr = await error;
        return (process.ExitCode, await output + stderr, stderr);
    }

    // In-process equivalent of Cli for configuration-only commands that never bind ports.
    public async Task<(int Code, string Output, string Error)> Run(params string[] args)
    {
        await ConsoleGate.WaitAsync();
        var (originalOut, originalError) = (Console.Out, Console.Error);
        var output = new StringWriter();
        var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            using var offline = new OfflineRuntime();
            var code = await new HostRunner(new()
            {
                ReadEnvironment = () => Environment, Diagnostics = offline.Diagnostics
            }).RunAsync(args, CancellationToken.None);
            return (code, output.ToString() + error, error.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            ConsoleGate.Release();
        }
    }

    public void Dispose()
    {
    }
}

[SetUpFixture]
public sealed class HostFixtureCleanup
{
    // The host writes location-only diagnostics to stderr, which the test platform would otherwise report as build warnings.
    [OneTimeSetUp]
    public void QuietStandardError() => Console.SetError(TextWriter.Null);

    [OneTimeTearDown]
    public void Cleanup()
    {
        if (!HostFixture.Created) return;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Directory.Delete(HostFixture.PackagesRoot, true);
        if (HostFixture.OfflineCreated) Directory.Delete(HostFixture.OfflinePackagesRoot, true);
    }
}
