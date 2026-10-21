using System.Net.Http.Headers;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Featureflip.Client.Internal;

/// <summary>
/// The <c>featureflip-csharp/&lt;version&gt;</c> User-Agent token every request carries, so the
/// Evaluation API can count SDK usage by name and version (#4139, contract in #4138).
/// </summary>
internal static class SdkIdentity
{
    public const string Name = "featureflip-csharp";

    /// <summary>
    /// The package version, read from <see cref="AssemblyInformationalVersionAttribute"/>.
    /// publish-csharp-sdk.yml rewrites the csproj <c>&lt;Version&gt;</c> from the release tag and
    /// the build stamps it here, so this cannot drift from what shipped the way a literal did
    /// in four other SDKs (#2141). The SDK appends <c>+&lt;commit&gt;</c> source-link metadata,
    /// which is stripped.
    /// </summary>
    public static readonly string Version = ReadVersion();

    private static string ReadVersion()
    {
        var informational = typeof(SdkIdentity).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrEmpty(informational) ? "unknown" : informational!.Split('+')[0];
    }

    /// <summary>
    /// Sets the User-Agent on <paramref name="headers"/>: the SDK token, then the wrapper's token
    /// when one is configured. A wrapper that is not a valid product token is dropped with a
    /// warning rather than thrown, because identity metadata must never stop the client starting.
    /// </summary>
    public static void Apply(HttpRequestHeaders headers, FeatureFlagOptions options, ILogger? logger)
    {
        headers.UserAgent.Add(new ProductInfoHeaderValue(Name, Version));

        if (string.IsNullOrEmpty(options.WrapperName)) return;
        try
        {
            headers.UserAgent.Add(new ProductInfoHeaderValue(options.WrapperName!, options.WrapperVersion));
        }
        catch (FormatException)
        {
            logger?.LogWarning(
                "Ignoring wrapper '{WrapperName}/{WrapperVersion}': not a valid User-Agent product token.",
                options.WrapperName, options.WrapperVersion);
        }
    }
}
