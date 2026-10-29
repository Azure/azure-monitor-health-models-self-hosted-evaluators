using System.Text.RegularExpressions;
using NuGet.Versioning;

namespace SelfHostedEvaluators.Core.Packages;

/// <summary>Describes the entry assembly and contract version declared in a package's plugin.json file.</summary>
/// <param name="Id">The package identifier matching its directory name.</param>
/// <param name="Version">The semantic version matching its version directory.</param>
/// <param name="Assembly">The package-relative path to the entry DLL.</param>
/// <param name="EntryType">The fully qualified concrete package entry type.</param>
/// <param name="AbstractionsVersion">The plugin contract version whose major must match the host.</param>
public sealed record PackageManifest(string Id, string Version, string Assembly, string EntryType, string AbstractionsVersion);

internal static class SemanticVersion
{
    private const string Number = "(0|[1-9][0-9]*)";
    private static readonly Regex Pattern = new(
        "^" + Number + "\\." + Number + "\\." + Number +
        "(?:-((?:0|[1-9][0-9]*|[0-9]*[a-zA-Z-][0-9a-zA-Z-]*)(?:\\.(?:0|[1-9][0-9]*|[0-9]*[a-zA-Z-][0-9a-zA-Z-]*))*))?" +
        "(?:\\+([0-9a-zA-Z-]+(?:\\.[0-9a-zA-Z-]+)*))?\\z", RegexOptions.CultureInvariant);

    internal static IComparer<NuGetVersion> Comparer { get; } = Comparer<NuGetVersion>.Create(Compare);
    internal static string Identity(NuGetVersion version) =>
        $"{version.Major}.{version.Minor}.{version.Patch}" + (version.IsPrerelease ? "-" + version.Release : "");

    internal static int Compare(NuGetVersion? left, NuGetVersion? right)
    {
        if (left is null || right is null) return left is null ? (right is null ? 0 : -1) : 1;
        var result = left.Version.CompareTo(right.Version);
        if (result != 0) return result;
        if (!left.IsPrerelease || !right.IsPrerelease)
            return left.IsPrerelease == right.IsPrerelease ? 0 : left.IsPrerelease ? -1 : 1;
        var a = left.ReleaseLabels.ToArray();
        var b = right.ReleaseLabels.ToArray();
        for (var index = 0; index < Math.Min(a.Length, b.Length); index++)
        {
            var aNumeric = a[index].All(char.IsAsciiDigit);
            var bNumeric = b[index].All(char.IsAsciiDigit);
            result = aNumeric != bNumeric ? (aNumeric ? -1 : 1) :
                aNumeric && a[index].Length != b[index].Length ? a[index].Length.CompareTo(b[index].Length) :
                StringComparer.Ordinal.Compare(a[index], b[index]);
            if (result != 0) return result;
        }
        return a.Length.CompareTo(b.Length);
    }

    internal static bool TryParse(string value, out NuGetVersion version)
    {
        version = null!;
        return Pattern.IsMatch(value) && NuGetVersion.TryParse(value, out version!);
    }
}

/// <summary>Validates and matches exact semantic versions, explicit ranges and supported floating versions.</summary>
public static class PackageVersionConstraint
{
    /// <summary>Checks whether a package version constraint uses the supported syntax.</summary>
    /// <param name="value">An exact version, explicit range, or major/minor floating constraint.</param>
    /// <returns>Whether the constraint is valid, without trimming or guessing its meaning.</returns>
    public static bool IsValid(string value) => TryParse(value, out _);

    /// <summary>Tests a semantic version against a supported constraint; bare versions are exact matches.</summary>
    /// <param name="version">The complete package semantic version.</param>
    /// <param name="constraint">The exact version, range or floating constraint.</param>
    /// <returns>True only when both inputs parse and the version satisfies the constraint.</returns>
    public static bool Matches(string version, string constraint)
    {
        if (!SemanticVersion.TryParse(version, out var parsed) || !TryParse(constraint, out var range)) return false;
        if (range.MinVersion is { } min)
        {
            var comparison = SemanticVersion.Compare(parsed, min);
            if (comparison < 0 || (comparison == 0 && !range.IsMinInclusive)) return false;
        }
        if (range.MaxVersion is { } max)
        {
            var comparison = SemanticVersion.Compare(parsed, max);
            if (comparison > 0 || (comparison == 0 && !range.IsMaxInclusive)) return false;
        }
        return range.Float is null || range.Float.Satisfies(parsed);
    }

    private static bool TryParse(string value, out VersionRange range)
    {
        range = null!;
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim()) return false;
        if (SemanticVersion.TryParse(value, out var exact))
        {
            range = new VersionRange(exact, true, exact, true);
            return true;
        }
        // Bare versions are exact; only explicit ranges or floating versions have range semantics.
        if (value.Contains('*'))
            return Regex.IsMatch(value, @"^(?:\*|(0|[1-9][0-9]*)\.\*|(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.\*)\z") &&
                VersionRange.TryParse(value, out range!);
        if (value.Length < 3 || value[0] is not ('[' or '(') || value[^1] is not (']' or ')')) return false;
        var bounds = value[1..^1].Split(',');
        if (bounds.Length is < 1 or > 2 || bounds.Any(b => b.Trim().Length > 0 && !SemanticVersion.TryParse(b.Trim(), out _))) return false;
        return VersionRange.TryParse(value, out range!) &&
            (range.MinVersion is null || range.MaxVersion is null || SemanticVersion.Compare(range.MinVersion, range.MaxVersion) <= 0);
    }
}
