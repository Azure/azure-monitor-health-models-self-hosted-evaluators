using System.Collections.Frozen;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NuGet.Versioning;
using SelfHostedEvaluators.Core.Configuration;
using SelfHostedEvaluators.Plugins.Abstractions;

namespace SelfHostedEvaluators.Core.Packages;

/// <summary>Exposes a selected package's entry point, sink registration and copied capabilities.</summary>
public sealed class LoadedPackage
{
    internal LoadedPackage(PackageManifest manifest, IPluginPackage instance, ISinkPlugin sink, PluginRegistration registration, string sha256)
    {
        Manifest = manifest;
        Instance = instance;
        Sink = sink;
        Registration = registration;
        Sha256 = sha256;
        var capabilities = sink.Capabilities ?? throw new InvalidOperationException();
        Capabilities = new(capabilities.MaxBatchSizeCap, capabilities.RequiredEndpoints, capabilities.AllowedHosts);
    }
    /// <summary>Gets the validated manifest of the selected version.</summary>
    public PackageManifest Manifest { get; }
    /// <summary>Gets the uppercase SHA-256 fingerprint of all published file paths and contents.</summary>
    public string Sha256 { get; }
    /// <summary>Gets the activated package entry point.</summary>
    public IPluginPackage Instance { get; }
    /// <summary>Gets the sink factory used for offline validation and client creation.</summary>
    public ISinkPlugin Sink { get; }
    /// <summary>Gets the package's sink and authentication implementation types.</summary>
    public PluginRegistration Registration { get; }
    /// <summary>Gets a copied snapshot of the sink's declared limits and endpoint requirements.</summary>
    public PluginCapabilities Capabilities { get; }
}

/// <summary>Loads the highest bundled version of each package into an isolated, collectible load context.</summary>
/// <remarks>Packages are trusted in-process code. Invalid discovered manifests fail loading without version fallback.</remarks>
public sealed class PackageCatalog : IDisposable
{
    private readonly PackageLoadContext[] contexts;
    private bool disposed;

    private PackageCatalog(Dictionary<string, LoadedPackage> packages, List<PackageLoadContext> contexts, Dictionary<string, Type> schemes)
    {
        Packages = packages.ToFrozenDictionary(StringComparer.Ordinal);
        this.contexts = contexts.ToArray();
        AuthSchemes = schemes.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        KnownAuthSchemes = AuthProfileParser.BuiltInSchemes.Concat(schemes.Keys).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Gets selected packages by case-sensitive package identifier.</summary>
    public IReadOnlyDictionary<string, LoadedPackage> Packages { get; }
    /// <summary>Gets package-provided auth factory types by case-insensitive scheme name.</summary>
    public IReadOnlyDictionary<string, Type> AuthSchemes { get; }
    /// <summary>Gets all built-in and package-provided authentication scheme names.</summary>
    public IReadOnlySet<string> KnownAuthSchemes { get; }

    /// <summary>Discovers, validates, fingerprints and activates packages beneath an id/version directory tree.</summary>
    /// <param name="directory">The local bundled-package root; links and escaped dependency paths are rejected.</param>
    /// <returns>A caller-owned catalog whose selected package contexts remain loaded until disposal.</returns>
    /// <exception cref="ConfigurationException">Discovery, manifest validation or package activation failed.</exception>
    public static PackageCatalog Load(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var contexts = new List<PackageLoadContext>();
        var diagnostics = new List<ConfigurationDiagnostic>();
        var manifests = new List<(PackageManifest Manifest, string Directory, NuGetVersion Version)>();
        try
        {
            PackagePaths.RejectLink(directory);
            RejectMisplacedManifest(directory);
            foreach (var idDirectory in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
            {
                PackagePaths.RejectLink(idDirectory);
                RejectMisplacedManifest(idDirectory);
                foreach (var versionDirectory in Directory.EnumerateDirectories(idDirectory).Order(StringComparer.Ordinal))
                {
                    var manifestPath = Path.Combine(versionDirectory, "plugin.json");
                    try
                    {
                        PackagePaths.RejectTreeLinks(versionDirectory);
                        foreach (var nested in Directory.EnumerateDirectories(versionDirectory))
                            if (Directory.EnumerateFiles(nested, "plugin.json", SearchOption.AllDirectories).Any())
                                throw ConfigurationException.At(manifestPath, "$");
                        using var document = StrictJson.Parse(File.ReadAllText(manifestPath), manifestPath);
                        var json = new StrictJson(document.RootElement, manifestPath).Shape("id", "version", "assembly", "entryType", "abstractionsVersion");
                        var manifest = new PackageManifest(json.Text("id"), json.Text("version"), json.Text("assembly"),
                            json.Text("entryType"), json.Text("abstractionsVersion"));
                        if (!Regex.IsMatch(manifest.Id, @"^[a-zA-Z0-9][a-zA-Z0-9-]*\z", RegexOptions.CultureInvariant) ||
                            manifest.Id != Path.GetFileName(idDirectory)) throw json.Error("id");
                        if (!SemanticVersion.TryParse(manifest.Version, out var version) ||
                            manifest.Version != Path.GetFileName(versionDirectory)) throw json.Error("version");
                        if (!SemanticVersion.TryParse(manifest.AbstractionsVersion, out var abstractions) ||
                            abstractions.Major != typeof(IPluginPackage).Assembly.GetName().Version!.Major)
                            throw json.Error("abstractionsVersion");
                        if (Path.IsPathRooted(manifest.Assembly) || manifest.Assembly.Split('/', '\\').Any(s => s is ".." or "." or "") ||
                            !manifest.Assembly.EndsWith(".dll", StringComparison.Ordinal)) throw json.Error("assembly");
                        try { PackagePaths.EnsureContainedFile(versionDirectory, Path.Combine(versionDirectory, manifest.Assembly)); }
                        catch { throw json.Error("assembly"); }
                        manifests.Add((manifest, Path.GetFullPath(versionDirectory), version));
                    }
                    catch (ConfigurationException error) { diagnostics.AddRange(error.Diagnostics); }
                    catch (Exception) { diagnostics.Add(new(manifestPath, "$")); }
                }
            }
            foreach (var group in manifests.GroupBy(m => m.Manifest.Id))
                if (group.Select(m => SemanticVersion.Identity(m.Version)).Distinct(StringComparer.Ordinal).Count() != group.Count())
                    diagnostics.Add(new(Path.Combine(directory, group.Key), "$.version"));
            if (diagnostics.Count > 0) throw new ConfigurationException(diagnostics);
            var packages = new Dictionary<string, LoadedPackage>(StringComparer.Ordinal);
            var schemes = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in manifests.GroupBy(m => m.Manifest.Id).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var selected = group.OrderByDescending(m => m.Version, SemanticVersion.Comparer).First();
                var manifestPath = Path.Combine(selected.Directory, "plugin.json");
                try
                {
                    var sha256 = Fingerprint(selected.Directory);
                    var assemblyPath = Path.Combine(selected.Directory, selected.Manifest.Assembly);
                    var context = new PackageLoadContext(assemblyPath, selected.Directory);
                    contexts.Add(context);
                    var assembly = context.LoadFromAssemblyPath(assemblyPath);
                    var type = assembly.GetType(selected.Manifest.EntryType, throwOnError: true)!;
                    if (!typeof(IPluginPackage).IsAssignableFrom(type) || type.IsAbstract) throw new InvalidOperationException();
                    var instance = (IPluginPackage)Activator.CreateInstance(type)!;
                    var registrar = new PluginRegistrar(AuthProfileParser.BuiltInSchemes.Concat(schemes.Keys));
                    instance.Register(registrar);
                    var registration = registrar.Build();
                    var sink = (ISinkPlugin)Activator.CreateInstance(registration.SinkPluginType)!;
                    foreach (var scheme in registration.AuthSchemes) schemes.Add(scheme.Key, scheme.Value);
                    packages.Add(selected.Manifest.Id, new(selected.Manifest, instance, sink, registration, sha256));
                }
                catch (Exception) { throw ConfigurationException.At(manifestPath, "$.entryType"); }
            }
            return new(packages, contexts, schemes);
        }
        catch (ConfigurationException)
        {
            foreach (var context in contexts) context.Unload();
            throw;
        }
        catch (Exception)
        {
            foreach (var context in contexts) context.Unload();
            throw ConfigurationException.At("SHE_PACKAGES_DIR", "$");
        }
    }

    private static void RejectMisplacedManifest(string directory)
    {
        if (File.Exists(Path.Combine(directory, "plugin.json")))
            throw ConfigurationException.At(Path.Combine(directory, "plugin.json"), "$");
    }

    private static string Fingerprint(string directory)
    {
        var entries = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path => (Path: path, Relative: Path.GetRelativePath(directory, path).Replace('\\', '/')))
            .OrderBy(file => file.Relative, StringComparer.Ordinal)
            .Select(file =>
            {
                using var stream = File.OpenRead(file.Path);
                return file.Relative + ":" + Convert.ToHexString(SHA256.HashData(stream));
            });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', entries))));
    }

    internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    /// <summary>Requests unload of all owned package contexts; clients must no longer be using them.</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var context in contexts) context.Unload();
    }
}
