using System.Text;
using Featureflip.Client.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Featureflip.Client;

/// <summary>
/// Persists the SDK's snapshot text to one file, written atomically through a temp file
/// in the same directory that is renamed over the target. The store is opaque: it neither
/// parses nor checks what it holds, so one instance can be shared by several SDK keys.
/// </summary>
/// <remarks>
/// <para>
/// Never throws: a failure is logged and treated as a miss (read) or a dropped write.
/// The parent directory must already exist, it is never created.
/// </para>
/// <para>
/// On Unix the file is created owner-only (mode 0600), including when the target already
/// existed with broader permissions, because the temp file replaces it. On .NET 8 and later
/// that uses <c>FileStreamOptions.UnixCreateMode</c>. Runtimes that load the netstandard2.0 build
/// (.NET Core, .NET 5, 6 and 7, Mono, Unity) create the file through <c>open(2)</c> with mode 0600
/// on Linux and on macOS x64. On any other Unix platform, or if <c>open</c> cannot be bound, the
/// mode follows the process umask and one warning is logged per process. Windows file permissions
/// are inherited from the directory and are not changed.
/// </para>
/// <para>
/// There is deliberately no fsync before the rename, so a power loss can leave a zero-length
/// or partial file. The SDK reads that as <c>snapshot-invalid</c>, logs a warning and serves
/// defaults, and the next successful write replaces it. A process killed mid-write can leave
/// a <c>&lt;name&gt;.&lt;id&gt;.tmp</c> file beside the snapshot, which is never read.
/// </para>
/// </remarks>
public sealed class FileSnapshotStore : ISnapshotStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _path;
    private volatile ILogger _logger = NullLogger.Instance;
    private int _hasLogger;

    /// <summary>Creates a store backed by the file at <paramref name="path"/>.</summary>
    public FileSnapshotStore(string path) : this(path, null)
    {
    }

    /// <summary>Creates a store that reports problems to <paramref name="logger"/>.</summary>
    public FileSnapshotStore(string path, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A file path is required.", nameof(path));
        _path = path;
        if (logger != null)
        {
            _logger = logger;
            _hasLogger = 1;
        }
    }

    /// <summary>
    /// Gives a store built without a logger the client's logger. The first logger wins, so
    /// a store shared by several clients keeps reporting to the same place.
    /// </summary>
    internal void AttachLogger(ILogger logger)
    {
        if (Interlocked.CompareExchange(ref _hasLogger, 1, 0) == 0) _logger = logger;
    }

    /// <inheritdoc />
    public Task<string?> GetAsync(CancellationToken cancellationToken)
    {
        try
        {
            return Task.FromResult<string?>(File.ReadAllText(_path, Utf8NoBom));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Task.FromResult<string?>(null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read snapshot file {Path}", _path);
            return Task.FromResult<string?>(null);
        }
    }

    /// <inheritdoc />
    public Task PutAsync(string text, CancellationToken cancellationToken)
    {
        var tmp = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = CreateExclusive(tmp, _logger))
            {
                var bytes = Utf8NoBom.GetBytes(text);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }

            Replace(tmp, _path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write snapshot file {Path}", _path);
            try
            {
                File.Delete(tmp);
            }
            catch
            {
                // Best effort: the temp file is never read, and the failure above is already logged.
            }
        }

        return Task.CompletedTask;
    }

    private static FileStream CreateExclusive(string path, ILogger logger)
    {
#if NET7_0_OR_GREATER
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(path, options);
#else
        // netstandard2.0 build: FileStream can only create with 0666 & ~umask, which would leave the
        // snapshot readable by other users. On Unix create through open(2) with mode 0600.
        if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
        {
            if (PosixOpen.IsSupported())
            {
                try
                {
                    return PosixOpen.CreateExclusiveOwnerOnly(path);
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
                {
                    WarnDefaultModeOnce(logger, "the C library's open could not be bound");
                }
            }
            else
            {
                WarnDefaultModeOnce(logger, "this operating system or architecture is not supported for owner-only creation");
            }
        }

        return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
#endif
    }

#if !NET7_0_OR_GREATER
    private static int _warnedDefaultMode;

    private static void WarnDefaultModeOnce(ILogger logger, string reason)
    {
        if (Interlocked.Exchange(ref _warnedDefaultMode, 1) != 0) return;
        logger.LogWarning(
            "FileSnapshotStore could not create the snapshot file as owner-only ({Reason}), so it follows the process umask. " +
            "Restrict the snapshot directory, or run on .NET 8 or later.", reason);
    }
#endif

    private static void Replace(string tmp, string target)
    {
#if NETCOREAPP3_0_OR_GREATER
        File.Move(tmp, target, overwrite: true);
#else
        if (File.Exists(target)) File.Replace(tmp, target, null);
        else File.Move(tmp, target);
#endif
    }
}
