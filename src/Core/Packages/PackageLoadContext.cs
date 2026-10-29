using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Threading.RateLimiting;
using SelfHostedEvaluators.Plugins.Abstractions;

namespace SelfHostedEvaluators.Core.Packages;

internal sealed class PackageLoadContext(string assemblyPath, string directory) : AssemblyLoadContext(isCollectible: true)
{
    private readonly AssemblyDependencyResolver resolver = new(assemblyPath);
    private static readonly HashSet<string> FrameworkAssemblies = Directory.EnumerateFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll")
        .Select(Path.GetFileNameWithoutExtension).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name == typeof(IPluginPackage).Assembly.GetName().Name) return typeof(IPluginPackage).Assembly;
        if (assemblyName.Name == typeof(RateLimiter).Assembly.GetName().Name) return typeof(RateLimiter).Assembly;
        if (assemblyName.Name is { } name && (FrameworkAssemblies.Contains(name) ||
            (name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal) && name.EndsWith(".Abstractions", StringComparison.Ordinal))))
            return Default.LoadFromAssemblyName(assemblyName);

        var path = resolver.ResolveAssemblyToPath(assemblyName);
        if (path is null) throw new FileNotFoundException("Package-private dependency unavailable.");
        PackagePaths.EnsureContainedFile(directory, path);
        return LoadFromAssemblyPath(path);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (path is null) throw new DllNotFoundException("Package-private native dependency unavailable.");
        PackagePaths.EnsureContainedFile(directory, path);
        return LoadUnmanagedDllFromPath(path);
    }
}

internal static class PackagePaths
{
    internal static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Package links are not supported.");
    }

    internal static void EnsureContainedFile(string directory, string path)
    {
        var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        var file = Path.GetFullPath(path);
        if (!file.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) || !File.Exists(file))
            throw new IOException("Package file is unavailable.");
        var current = file;
        while (current.Length >= root.Length)
        {
            RejectLink(current);
            current = Path.GetDirectoryName(current)!;
        }
    }

    internal static void RejectTreeLinks(string directory)
    {
        RejectLink(directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            RejectLink(entry);
            if (Directory.Exists(entry)) RejectTreeLinks(entry);
        }
    }
}
