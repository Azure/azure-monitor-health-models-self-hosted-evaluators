using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using SelfHostedEvaluators.Core.Configuration;
using SelfHostedEvaluators.Core.Packages;

namespace SelfHostedEvaluators.Core.Tests;

public class PackageCatalogTests
{
    [Test]
    public void LoadedPackageAuditHashesCompleteTreeIndependentlyOfRootAndCreationOrder()
    {
        using var first = new PackageFixture();
        using var second = new PackageFixture();
        var source = first.Add();
        var target = second.Add();
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        Directory.CreateDirectory(Path.Combine(target, "nested"));
        File.WriteAllText(Path.Combine(source, "nested", "Z.txt"), "z");
        File.WriteAllText(Path.Combine(source, ".audit"), "hidden");
        File.WriteAllText(Path.Combine(target, ".audit"), "hidden");
        File.WriteAllText(Path.Combine(target, "nested", "Z.txt"), "z");
        using var a = PackageCatalog.Load(first.Root);
        using var b = PackageCatalog.Load(second.Root);
        var hash = a.Packages["one"].Sha256;
        var entries = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Select(p => (Name: Path.GetRelativePath(source, p).Replace('\\', '/'), Path: p))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => p.Name + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p.Path))));
        Assert.That(hash, Is.EqualTo(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', entries))))));
        Assert.That(b.Packages["one"].Sha256, Is.EqualTo(hash));
        File.AppendAllText(Path.Combine(target, "nested", "Z.txt"), "changed");
        using var mutated = PackageCatalog.Load(second.Root);
        Assert.That(mutated.Packages["one"].Sha256, Is.Not.EqualTo(hash));
        File.Move(Path.Combine(target, "nested", "Z.txt"), Path.Combine(target, "nested", "z.txt"));
        using var renamed = PackageCatalog.Load(second.Root);
        Assert.That(renamed.Packages["one"].Sha256, Is.Not.EqualTo(mutated.Packages["one"].Sha256));
    }

    [Test]
    public void MissingDirectoryProducesOnlyConfigurationDiagnostic()
    {
        using var fixture = new PackageFixture();
        var error = Assert.Throws<ConfigurationException>(() => PackageCatalog.Load(Path.Combine(fixture.Root, "sensitive")));
        Assert.That(error!.Message, Is.EqualTo("SHE_PACKAGES_DIR:$"));
    }

    [TestCase("root")]
    [TestCase("id")]
    [TestCase("nested")]
    public void OutOfLayoutAndDuplicateNestedManifestsAreRejected(string location)
    {
        using var fixture = new PackageFixture();
        var package = fixture.Add();
        var destination = location switch
        {
            "root" => fixture.Root,
            "id" => Path.Combine(fixture.Root, "one"),
            _ => Path.Combine(package, "nested")
        };
        Directory.CreateDirectory(destination);
        File.Copy(Path.Combine(package, "plugin.json"), Path.Combine(destination, "plugin.json"));
        Assert.Throws<ConfigurationException>(() => PackageCatalog.Load(fixture.Root));
    }

    [TestCase("1.9.0", "1.*", true)]
    [TestCase("2.0.0", "1.*", false)]
    [TestCase("1.2.4", "1.2.*", true)]
    [TestCase("1.3.0", "1.2.*", false)]
    [TestCase("1.2.4", "1.2.3", false)]
    [TestCase("1.2.3", "1.2.3", true)]
    [TestCase("1.9.0", "[1.0.0,2.0.0)", true)]
    [TestCase("2.0.0", "[1.0.0,2.0.0)", false)]
    [TestCase("1.0.0-beta", "1.0.0-Beta", false)]
    public void VersionConstraintsAreNotMinimumOnly(string version, string constraint, bool matches) =>
        Assert.That(PackageVersionConstraint.Matches(version, constraint), Is.EqualTo(matches));

    [Test]
    public void SemVerPrereleasePrecedenceUsesOrdinalAsciiNotNuGetCaseFolding()
    {
        using var fixture = new PackageFixture();
        fixture.Add(version: "1.0.0-Zulu");
        fixture.Add(version: "1.0.0-alpha");
        using var catalog = PackageCatalog.Load(fixture.Root);
        Assert.That(catalog.Packages["one"].Manifest.Version, Is.EqualTo("1.0.0-alpha"));
    }

    [Test]
    public void RegisteredCustomAuthIsKnownAndRetainsOwnedProfile()
    {
        using var fixture = new PackageFixture();
        fixture.Add(entry: "AuthPackage");
        using var catalog = PackageCatalog.Load(fixture.Root);
        var snapshot = new EnvironmentConfigSource().Load(EnvironmentConfigTests.Environment(
            EnvironmentConfigTests.Plugin.Replace("\"type\":\"none\"", "\"type\":\"fixtureAuth\",\"credential\":\"kv://vault/name\"")));
        ConfigurationValidator.Validate(snapshot, catalog, ConfigurationValidationMode.Deployment);
        Assert.That(((CustomAuthProfile)snapshot.Plugins[0].Auth).Profile.GetProperty("credential").GetString(),
            Is.EqualTo("kv://vault/name"));
        Assert.That(catalog.AuthSchemes.ContainsKey("FIXTUREAUTH"), Is.True);
        var unknown = new EnvironmentConfigSource().Load(EnvironmentConfigTests.Environment(
            EnvironmentConfigTests.Plugin.Replace("\"type\":\"none\"", "\"type\":\"unknownScheme\"")));
        Assert.Throws<ConfigurationException>(() => ConfigurationValidator.Validate(unknown, catalog, ConfigurationValidationMode.Runtime));
    }

    [Test]
    public void HighestSemVerIsLoadedInIsolatedContextWithSharedPublicTypes()
    {
        using var fixture = new PackageFixture();
        fixture.Add(version: "1.9.0");
        fixture.Add(version: "1.10.0-beta.2");
        fixture.Add(version: "1.10.0-beta.11");
        fixture.Add(version: "1.10.0");
        using var catalog = PackageCatalog.Load(fixture.Root);
        var package = catalog.Packages["one"];
        Assert.That(package.Manifest.Version, Is.EqualTo("1.10.0"));
        var type = package.Sink.GetType();
        Assert.That(AssemblyLoadContext.GetLoadContext(type.Assembly), Is.Not.SameAs(AssemblyLoadContext.Default));
        Assert.That(type.GetProperty("RateLimiterType")!.GetValue(null), Is.SameAs(typeof(RateLimiter)));
        Assert.That(package.Capabilities.MaxBatchSizeCap, Is.EqualTo(10));
    }

    [TestCase("MissingSinkPackage")]
    [TestCase("DoubleSinkPackage")]
    [TestCase("ReservedAuthPackage")]
    [TestCase("NoSuchType")]
    public void InvalidRegistrationsNeverFallBack(string entry)
    {
        using var fixture = new PackageFixture();
        fixture.Add(version: "1.0.0");
        fixture.Add(version: "2.0.0", entry: entry);
        Assert.Throws<ConfigurationException>(() => PackageCatalog.Load(fixture.Root));
    }

    [TestCase("2.0.0", "Fixture.Package.dll")]
    [TestCase("1.0.0", "..\\Fixture.Package.dll")]
    [TestCase("1.0.0", "../Fixture.Package.dll")]
    [TestCase("1.0.0", "missing.dll")]
    public void IncompatibleOrEscapingManifestsFail(string abstractions, string assembly)
    {
        using var fixture = new PackageFixture();
        fixture.Add(abstractions: abstractions, assembly: assembly);
        Assert.Throws<ConfigurationException>(() => PackageCatalog.Load(fixture.Root));
    }

    [TestCase("01.0.0")]
    [TestCase("1.0")]
    [TestCase("1.0.0-01")]
    public void InvalidSemVerFails(string version)
    {
        using var fixture = new PackageFixture();
        fixture.Add(version: version);
        Assert.Throws<ConfigurationException>(() => PackageCatalog.Load(fixture.Root));
    }

    [TestCase("\"id\":\"one\"", "\"id\":\"other\"")]
    [TestCase("\"version\":\"1.0.0\"", "\"version\":\"2.0.0\"")]
    [TestCase("\"id\":\"one\"", "\"id\":\"one\",\"id\":\"one\"")]
    [TestCase("\"id\":\"one\"", "\"id\":\"one\",\"unknown\":1")]
    public void ManifestShapeAndDirectoryIdentityAreStrict(string oldValue, string newValue)
    {
        using var fixture = new PackageFixture();
        var path = Path.Combine(fixture.Add(), "plugin.json");
        File.WriteAllText(path, File.ReadAllText(path).Replace(oldValue, newValue));
        Assert.Throws<ConfigurationException>(() => PackageCatalog.Load(fixture.Root));
    }

    [Test]
    public void DuplicateSemVerIdentityAndGlobalAuthNamesFail()
    {
        using var fixture = new PackageFixture();
        fixture.Add(version: "1.0.0+first");
        fixture.Add(version: "1.0.0+second");
        Assert.Throws<ConfigurationException>(() => PackageCatalog.Load(fixture.Root));
        using var auth = new PackageFixture();
        auth.Add(entry: "AuthPackage");
        auth.Add(id: "two", entry: "AuthPackage", fixture: "Two");
        Assert.Throws<ConfigurationException>(() => PackageCatalog.Load(auth.Root));
    }

    [TestCase("\"packageVersion\":\"2.*\",", "packageVersion")]
    [TestCase("\"batch\":{\"maxSize\":11},", "batch.maxSize")]
    [TestCase("\"settings\":{\"reject\":true},", "settings")]
    [TestCase("\"settings\":{\"throw\":true},", "settings")]
    public void PackageAwareValidationIsRedacted(string extra, string path)
    {
        using var fixture = new PackageFixture();
        fixture.Add();
        using var catalog = PackageCatalog.Load(fixture.Root);
        var snapshot = new EnvironmentConfigSource().Load(EnvironmentConfigTests.Environment(
            EnvironmentConfigTests.Plugin.Replace("\"package\":", extra + "\"package\":")));
        var error = Assert.Throws<ConfigurationException>(() =>
            ConfigurationValidator.Validate(snapshot, catalog, ConfigurationValidationMode.Deployment));
        Assert.That(error!.Message, Does.Contain(path).And.Not.Contain("sensitive"));
    }

    [TestCase("https://other.test")]
    [TestCase("https://sink.test.evil.test")]
    public void PackageAllowedHostsAreExact(string endpoint)
    {
        using var fixture = new PackageFixture();
        fixture.Add();
        using var catalog = PackageCatalog.Load(fixture.Root);
        var snapshot = new EnvironmentConfigSource().Load(EnvironmentConfigTests.Environment(
            EnvironmentConfigTests.Plugin.Replace("https://sink.test", endpoint)));
        Assert.Throws<ConfigurationException>(() => ConfigurationValidator.Validate(snapshot, catalog, ConfigurationValidationMode.Runtime));
    }

    [Test]
    public void RequiredEndpointsAndKnownQueriesAreValidated()
    {
        using var fixture = new PackageFixture();
        fixture.Add();
        using var catalog = PackageCatalog.Load(fixture.Root);
        foreach (var env in new[]
        {
            EnvironmentConfigTests.Environment(EnvironmentConfigTests.Plugin.Replace("\"default\":", "\"other\":")),
            EnvironmentConfigTests.Environment(model: EnvironmentConfigTests.Model.Replace("\"query\":{}", "\"query\":{\"reject\":true}"))
        })
        {
            var snapshot = new EnvironmentConfigSource().Load(env);
            Assert.Throws<ConfigurationException>(() => ConfigurationValidator.Validate(snapshot, catalog, ConfigurationValidationMode.Runtime));
        }
    }
}
