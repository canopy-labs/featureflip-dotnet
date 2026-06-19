using Featureflip.Client.Internal;
using Xunit;

namespace Featureflip.Client.Tests.Internal;

public class SemverComparerTests
{
    [Theory]
    [InlineData("1.2.3")]
    [InlineData("1")]
    [InlineData("1.0")]
    [InlineData("v2.1")]            // leading "v" tolerated
    [InlineData("V2.1")]            // leading "V" tolerated
    [InlineData("2.0.0+build.7")]   // build metadata stripped
    [InlineData("1.0.0-alpha")]     // prerelease
    [InlineData("1.0.0-alpha.1")]
    [InlineData("99999999999999999999.0")] // overflow-free (won't fit in long)
    public void TryParse_ValidVersions_ReturnsTrue(string value)
    {
        Assert.True(SemverComparer.TryParse(value, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1.x")]      // non-numeric release segment
    [InlineData("1..2")]     // empty release segment
    [InlineData("v")]        // empty core after stripping "v"
    [InlineData("1.0.0-")]   // trailing "-" with no identifiers
    [InlineData("1.0.0-a..b")] // empty prerelease identifier
    public void TryParse_InvalidVersions_ReturnsFalse(string? value)
    {
        Assert.False(SemverComparer.TryParse(value, out _));
    }

    [Theory]
    // Missing trailing segments compare as 0.
    [InlineData("2.0", "2.0.0", 0)]
    [InlineData("2.0.0", "2.0", 0)]
    // Multi-segment ordering (the regression: decimal comparison got these wrong).
    [InlineData("2.10.1", "2.0", 1)]
    [InlineData("2.10", "2.9", 1)]
    [InlineData("1.9.9", "2.0", -1)]
    // Leading zeros do not change the numeric value.
    [InlineData("1.01", "1.1", 0)]
    // Overflow-free comparison of huge segments.
    [InlineData("99999999999999999999.0", "1.0", 1)]
    // Leading "v" and build metadata are ignored for precedence.
    [InlineData("v1.2.3", "1.2.3", 0)]
    [InlineData("1.2.3+build", "1.2.3", 0)]
    // Prerelease precedence (semver §11).
    [InlineData("1.0.0-alpha", "1.0.0", -1)]            // prerelease < release
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1", -1)]    // longer wins when shared equal
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta", -1)] // numeric < alphanumeric
    [InlineData("1.0.0-alpha", "1.0.0-beta", -1)]       // ordinal identifier compare
    [InlineData("1.0.0-1", "1.0.0-2", -1)]              // numeric identifier compare
    [InlineData("1.0.0-1", "1.0.0-10", -1)]             // numeric, not lexical
    // Case-sensitive ASCII sort order (semver §11, #1447): A–Z (65–90) sort before a–z (97–122).
    // A case-folding comparer would order these wrong / treat them as equal.
    [InlineData("1.0.0-Beta", "1.0.0-alpha", -1)]       // 'B'(66) < 'a'(97): Beta < alpha
    [InlineData("1.0.0-RC", "1.0.0-rc", -1)]            // 'R'(82) < 'r'(114): RC != rc
    public void Compare_ReturnsExpectedSign(string a, string b, int expectedSign)
    {
        Assert.True(SemverComparer.TryParse(a, out var left));
        Assert.True(SemverComparer.TryParse(b, out var right));

        Assert.Equal(expectedSign, System.Math.Sign(SemverComparer.Compare(left, right)));
        // Comparison must be antisymmetric.
        Assert.Equal(-expectedSign, System.Math.Sign(SemverComparer.Compare(right, left)));
    }
}
