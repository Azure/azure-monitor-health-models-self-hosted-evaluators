using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace Checkout.Demo.Tests;

internal static class InfrastructureTools
{
    internal static string DemoDirectory
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && directory.Name != "demo_system")
                directory = directory.Parent;
            return directory?.FullName ?? throw new InvalidOperationException("Cannot find demo_system.");
        }
    }

    internal static string ArtifactDirectory { get; } =
        Path.Combine(AppContext.BaseDirectory, "infrastructure-artifacts");

    internal static (int ExitCode, string Output) Run(string toolVariable, params string[] arguments)
    {
        var executable = ResolveTool(toolVariable);
        Assert.That(executable, Is.Not.Null.And.Not.Empty,
            $"Set {toolVariable} to the real executable or put it on PATH.");
        Assert.That(File.Exists(executable), Is.True, $"Missing executable: {executable}");
        var start = new ProcessStartInfo(executable!)
        {
            WorkingDirectory = DemoDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"{toolVariable} timed out.");
        }
        return (process.ExitCode, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
    }

    private static readonly Dictionary<string, string> ToolNames = new()
    {
        ["BICEP_CLI_PATH"] = "bicep"
    };

    // An explicit variable always wins; otherwise search the current and persisted PATH, then well-known install locations.
    internal static string? ResolveTool(string toolVariable)
    {
        var configured = Environment.GetEnvironmentVariable(toolVariable);
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        if (!ToolNames.TryGetValue(toolVariable, out var name)) return null;

        var fileName = OperatingSystem.IsWindows() ? name + ".exe" : name;
        var directories = new List<string>();
        void AddPath(string? path) => directories.AddRange(
            (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        AddPath(Environment.GetEnvironmentVariable("PATH"));
        if (OperatingSystem.IsWindows())
        {
            // A process started before a winget install does not see the updated PATH.
            AddPath(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));
            AddPath(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            directories.Add(Path.Combine(home, ".azure", "bin"));
            directories.Add(Path.Combine(local, "Microsoft", "WinGet", "Links"));
            directories.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Bicep CLI"));
            directories.Add(Path.Combine(local, "Programs", "Bicep CLI"));
            var packages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(packages))
                directories.AddRange(Directory.EnumerateDirectories(packages, "*" + name + "*"));
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            directories.AddRange([Path.Combine(home, ".azure", "bin"), "/usr/local/bin", "/usr/bin"]);
        }

        return directories.Select(d => Path.Combine(d, fileName)).FirstOrDefault(File.Exists);
    }

    // Each bicep launch costs 1-3s, so identical requests share one result across tests running in parallel.
    private static readonly ConcurrentDictionary<string, Lazy<JsonElement>> CompileCache = new();
    private static readonly ConcurrentDictionary<string, Lazy<(int ExitCode, string Output, JsonElement? Snapshot)>> SnapshotCache = new();

    internal static JsonElement Compile(string template) =>
        CompileCache.GetOrAdd(template, key => new Lazy<JsonElement>(() => CompileUncached(key))).Value;

    private static JsonElement CompileUncached(string template)
    {
        var file = Path.Combine(DemoDirectory, "infra", template + ".bicep");
        Assert.That(File.Exists(file), Is.True, $"Missing demo template: {template}.bicep");
        var result = Run("BICEP_CLI_PATH", "build", file, "--no-restore", "--stdout");
        Assert.That(result.ExitCode, Is.Zero, result.Output);
        using var json = JsonDocument.Parse(result.Output);
        return json.RootElement.Clone();
    }

    internal static JsonElement[] Resources(JsonElement template, string type) =>
        template.GetProperty("resources").EnumerateArray()
            .Where(r => r.GetProperty("type").GetString() == type).ToArray();

    internal static JsonElement Resource(JsonElement template, string type) =>
        Resources(template, type).Single();

    internal static JsonElement ResolveVariable(JsonElement template, JsonElement value)
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
        return text.StartsWith("[variables('", StringComparison.Ordinal) && text.EndsWith("')]", StringComparison.Ordinal)
            ? template.GetProperty("variables").GetProperty(text[12..^3])
            : value;
    }

    internal static JsonElement Snapshot(string template, string parameterAssignments = "", string group = "checkout-test-rg", string prefix = "demo")
    {
        var result = SnapshotAttempt(template, parameterAssignments, group, prefix);
        Succeeded((result.ExitCode, result.Output));
        return result.Snapshot!.Value;
    }

    internal static (int ExitCode, string Output, JsonElement? Snapshot) SnapshotAttempt(
        string template, string parameterAssignments = "", string group = "checkout-test-rg", string prefix = "demo") =>
        SnapshotCache.GetOrAdd(string.Join('\u0001', template, parameterAssignments, group, prefix),
            _ => new Lazy<(int, string, JsonElement?)>(() => SnapshotUncached(template, parameterAssignments, group, prefix))).Value;

    private static (int ExitCode, string Output, JsonElement? Snapshot) SnapshotUncached(
        string template, string parameterAssignments, string group, string prefix)
    {
        var source = Path.Combine(DemoDirectory, "infra", template + ".bicep");
        Assert.That(File.Exists(source), Is.True, $"Missing demo template: {template}.bicep");
        var file = Path.Combine(DemoDirectory, "infra", $"test-{Guid.NewGuid():N}.bicepparam");
        var snapshotFile = Path.ChangeExtension(file, ".snapshot.json");
        try
        {
            File.WriteAllText(file, $"""
                using '{template}.bicep'
                param location = 'eastus'
                param namePrefix = '{prefix}'
                {parameterAssignments}
                """);
            var result = Run("BICEP_CLI_PATH", "snapshot", file, "--mode", "overwrite",
                "--subscription-id", "11111111-1111-1111-1111-111111111111", "--resource-group", group,
                "--location", "eastus");
            if (result.ExitCode != 0) return (result.ExitCode, result.Output, null);
            using var json = JsonDocument.Parse(File.ReadAllText(snapshotFile));
            return (result.ExitCode, result.Output, json.RootElement.Clone());
        }
        finally
        {
            File.Delete(file);
            File.Delete(snapshotFile);
        }
    }

    internal static void Succeeded((int ExitCode, string Output) result) =>
        Assert.That(result.ExitCode, Is.Zero, result.Output);
}
