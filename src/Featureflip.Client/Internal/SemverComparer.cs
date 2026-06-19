using System;
using System.Collections.Generic;

namespace Featureflip.Client.Internal;

/// <summary>
/// Compares semantic-version strings (https://semver.org) for the <c>Semver*</c> condition
/// operators.
/// </summary>
/// <remarks>
/// Tolerant of real-world version strings:
/// <list type="bullet">
/// <item>an optional leading <c>v</c> (e.g. <c>v2.1</c>);</item>
/// <item>an arbitrary number of dot-separated numeric segments — missing trailing segments
/// compare as <c>0</c>, so <c>2.0</c> equals <c>2.0.0</c>;</item>
/// <item>an optional <c>-prerelease</c> suffix, which has lower precedence than the release
/// it qualifies;</item>
/// <item><c>+build</c> metadata, which is ignored for precedence.</item>
/// </list>
/// Numeric segments are compared digit-by-digit rather than parsed into a fixed-width integer,
/// so arbitrarily large version numbers never overflow. A value whose release core is missing
/// or non-numeric is "not a version" and matches nothing — mirroring how the numeric and
/// date/time operators treat unparseable input.
/// <para>
/// This mirrors <c>apps/evaluation-api/.../Services/SemverComparer.cs</c> (the engine) and the
/// JS SDK's <c>parseSemver</c>/<c>compareSemver</c>. Kept netstandard2.0-safe (no range/index
/// operators) since this SDK multi-targets net10.0/net8.0/netstandard2.0.
/// </para>
/// </remarks>
internal static class SemverComparer
{
    internal readonly struct SemverVersion
    {
        public IReadOnlyList<string> Release { get; }
        public IReadOnlyList<string> Prerelease { get; }

        public SemverVersion(IReadOnlyList<string> release, IReadOnlyList<string> prerelease)
        {
            Release = release;
            Prerelease = prerelease;
        }
    }

    /// <summary>
    /// Attempts to parse <paramref name="value"/> as a semantic version. Returns <c>false</c>
    /// when the release core is missing or any release segment is non-numeric.
    /// </summary>
    public static bool TryParse(string? value, out SemverVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var s = value!.Trim();

        // Optional leading "v"/"V".
        if (s[0] == 'v' || s[0] == 'V')
            s = s.Substring(1);

        // Build metadata ("+...") does not affect precedence.
        var plus = s.IndexOf('+');
        if (plus >= 0)
            s = s.Substring(0, plus);

        // Split the release core from the optional "-prerelease" suffix.
        string corePart;
        string[] prerelease;
        var dash = s.IndexOf('-');
        if (dash >= 0)
        {
            corePart = s.Substring(0, dash);
            var pre = s.Substring(dash + 1);
            if (pre.Length == 0)
                return false; // trailing "-" with no identifiers is malformed
            prerelease = pre.Split('.');
            if (Array.Exists(prerelease, id => id.Length == 0))
                return false;
        }
        else
        {
            corePart = s;
            prerelease = Array.Empty<string>();
        }

        if (corePart.Length == 0)
            return false;

        var release = corePart.Split('.');
        foreach (var seg in release)
        {
            if (!IsAllDigits(seg))
                return false;
        }

        version = new SemverVersion(release, prerelease);
        return true;
    }

    /// <summary>Returns -1, 0, or 1 comparing <paramref name="a"/> to <paramref name="b"/>.</summary>
    public static int Compare(SemverVersion a, SemverVersion b)
    {
        var max = Math.Max(a.Release.Count, b.Release.Count);
        for (var i = 0; i < max; i++)
        {
            var segA = i < a.Release.Count ? a.Release[i] : "0";
            var segB = i < b.Release.Count ? b.Release[i] : "0";
            var cmp = CompareNumericString(segA, segB);
            if (cmp != 0)
                return cmp;
        }

        return ComparePrerelease(a.Prerelease, b.Prerelease);
    }

    private static int ComparePrerelease(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        // A version with no prerelease has higher precedence than one with a prerelease.
        if (a.Count == 0 && b.Count == 0) return 0;
        if (a.Count == 0) return 1;
        if (b.Count == 0) return -1;

        var min = Math.Min(a.Count, b.Count);
        for (var i = 0; i < min; i++)
        {
            var cmp = ComparePrereleaseIdentifier(a[i], b[i]);
            if (cmp != 0)
                return cmp;
        }

        // All shared identifiers equal: the longer prerelease has higher precedence.
        return a.Count.CompareTo(b.Count);
    }

    private static int ComparePrereleaseIdentifier(string a, string b)
    {
        var aNum = IsAllDigits(a);
        var bNum = IsAllDigits(b);

        // Numeric identifiers always have lower precedence than alphanumeric ones.
        if (aNum && bNum) return CompareNumericString(a, b);
        if (aNum) return -1;
        if (bNum) return 1;
        // Semver §11: alphanumeric identifiers compare in ASCII sort order (case-sensitive).
        return string.CompareOrdinal(a, b);
    }

    /// <summary>
    /// Compares two all-digit strings as non-negative integers without parsing (overflow-free):
    /// strip leading zeros, then the longer string is the larger number; equal lengths compare
    /// ordinally.
    /// </summary>
    private static int CompareNumericString(string a, string b)
    {
        a = a.TrimStart('0');
        b = b.TrimStart('0');
        if (a.Length != b.Length)
            return a.Length < b.Length ? -1 : 1;
        return string.CompareOrdinal(a, b);
    }

    private static bool IsAllDigits(string s)
    {
        if (s.Length == 0) return false;
        foreach (var c in s)
        {
            if (c < '0' || c > '9')
                return false;
        }
        return true;
    }
}
