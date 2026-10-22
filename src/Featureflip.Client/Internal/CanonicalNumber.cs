using System.Globalization;
using System.Text;

namespace Featureflip.Client.Internal;

/// <summary>
/// Canonical spelling of a floating-point rollout bucket value (#4236).
/// Same text as ECMAScript <c>Number::toString</c> and the Evaluation API's
/// <c>BucketValueFormatter.CanonicalNumber</c>, so every SDK hashes one number to one bucket.
/// Bucket path only; condition evaluation never calls it.
/// </summary>
/// <remarks>
/// Finds the shortest round-trip digits with a <c>E{p}</c> loop instead of <c>"R"</c>: this
/// file also compiles for netstandard2.0, where the default formatter is not guaranteed shortest.
/// </remarks>
internal static class CanonicalNumber
{
    public static string Format(double x) => FormatCore(x, isSingle: false);

    public static string Format(float x) => FormatCore(x, isSingle: true);

    private static string FormatCore(double x, bool isSingle)
    {
        if (double.IsNaN(x)) return "NaN";
        if (double.IsPositiveInfinity(x)) return "Infinity";
        if (double.IsNegativeInfinity(x)) return "-Infinity";
        if (x == 0) return "0";
        if (x < 0) return "-" + FormatCore(-x, isSingle);

        var (digits, n) = ShortestDigits(x, isSingle);
        var k = digits.Length;

        if (k <= n && n <= 21) return digits + new string('0', n - k);
        if (0 < n && n <= 21) return digits.Substring(0, n) + "." + digits.Substring(n);
        if (-6 < n && n <= 0) return "0." + new string('0', -n) + digits;

        var e = n - 1;
        var sb = new StringBuilder();
        sb.Append(digits[0]);
        if (k > 1) sb.Append('.').Append(digits, 1, k - 1);
        sb.Append('e').Append(e >= 0 ? '+' : '-').Append(Math.Abs(e).ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    // Value = 0.<digits> * 10^n, digits without leading or trailing zeros.
    private static (string Digits, int N) ShortestDigits(double x, bool isSingle)
    {
        var maxPrecision = isSingle ? 8 : 16;
        for (var p = 0; p <= maxPrecision; p++)
        {
            var text = x.ToString("E" + p.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            if (RoundTrips(text, x, isSingle)) return Split(text);

            // The nearest p+1 digit decimal is not always the one that round-trips (the shortest
            // form can sit one digit away, e.g. 2^-44), so also try its neighbour on x's side.
            var neighbour = Neighbour(text, x, p);
            if (neighbour != null && RoundTrips(neighbour, x, isSingle)) return Split(neighbour);
        }

        // A formatter that cannot render 17 significant digits (older .NET Framework) never
        // round-trips above; "R" is its documented round-trip spelling.
        return Split(isSingle
            ? ((float)x).ToString("R", CultureInfo.InvariantCulture)
            : x.ToString("R", CultureInfo.InvariantCulture));
    }

    private static bool RoundTrips(string text, double x, bool isSingle)
        => isSingle
            ? float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture) == (float)x
            : double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture) == x;

    // "d.dddE+xxx" (p+1 digits) -> the next decimal of the same width above or below it,
    // spelled as "<integer>E<exp>", whichever lies on x's side of the formatted value.
    private static string? Neighbour(string text, double x, int p)
    {
        var e = text.IndexOf('E');
        if (e < 0 || p > 16) return null;
        var digits = text.Substring(0, e).Replace(".", "");
        var exp = int.Parse(text.Substring(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var mantissa)) return null;
        var formatted = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        var step = formatted < x ? 1 : -1;
        return (mantissa + step).ToString(CultureInfo.InvariantCulture) + "E"
            + (exp - p).ToString(CultureInfo.InvariantCulture);
    }

    // Splits "d.dddE+xxx", "123.456" or "1E-07" into significant digits and exponent n.
    private static (string Digits, int N) Split(string text)
    {
        var exp = 0;
        var e = text.IndexOfAny(new[] { 'E', 'e' });
        var mantissa = text;
        if (e >= 0)
        {
            exp = int.Parse(text.Substring(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            mantissa = text.Substring(0, e);
        }

        var dot = mantissa.IndexOf('.');
        var intPart = dot >= 0 ? mantissa.Substring(0, dot) : mantissa;
        var all = intPart + (dot >= 0 ? mantissa.Substring(dot + 1) : "");
        var n = intPart.Length + exp;

        var lead = 0;
        while (lead < all.Length - 1 && all[lead] == '0') lead++;
        n -= lead;
        var digits = all.Substring(lead).TrimEnd('0');
        return (digits.Length == 0 ? "0" : digits, n);
    }
}
