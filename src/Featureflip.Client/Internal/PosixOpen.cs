// Compiled into the netstandard2.0 build only (net7+ creates owner-only files through
// FileStreamOptions.UnixCreateMode). The test project links this file under
// SNAPSHOT_POSIX_OPEN_TESTS so the helper can run on a net8/net10 test host.
#if !NET7_0_OR_GREATER || SNAPSHOT_POSIX_OPEN_TESTS
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Featureflip.Client.Internal;

/// <summary>
/// Creates a file exclusively with mode 0600 through <c>open(2)</c>, for runtimes that load the
/// netstandard2.0 build, where <c>FileStream</c> can only create with <c>0666 &amp; ~umask</c>.
/// </summary>
/// <remarks>
/// <para>
/// A snapshot holds targeting rules with customer attribute values, and the temp file is renamed
/// over the target, so a default-mode temp file would leave the snapshot world-readable and widen
/// a previously owner-only target.
/// </para>
/// <para>
/// Supported: Linux on x86, x64, Arm and Arm64, and macOS on x64. <c>open</c> is variadic, and on
/// Apple Arm64 variadic arguments are passed on the stack, so a plain P/Invoke would pass a garbage
/// mode there. Anything else reports unsupported and the caller falls back.
/// </para>
/// </remarks>
internal static class PosixOpen
{
    private const int EEXIST = 17;

    // Linux (generic ABI shared by x86, x64, Arm and Arm64) and macOS values differ.
    private const int LinuxWriteOnly = 0x1, LinuxCreate = 0x40, LinuxExclusive = 0x80, LinuxCloseOnExec = 0x80000;
    private const int MacWriteOnly = 0x1, MacCreate = 0x200, MacExclusive = 0x800, MacCloseOnExec = 0x1000000;

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPStr)] string path, int flags, uint mode);

    /// <summary>Whether this OS and architecture have verified flag values and a safe calling convention.</summary>
    public static bool IsSupported()
    {
        var arch = RuntimeInformation.ProcessArchitecture;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return arch is Architecture.X86 or Architecture.X64 or Architecture.Arm or Architecture.Arm64;
        }

        return RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && arch == Architecture.X64;
    }

    /// <summary>
    /// Creates <paramref name="path"/> with <c>O_CREAT | O_EXCL | O_WRONLY | O_CLOEXEC</c> and mode 0600.
    /// Throws <see cref="DllNotFoundException"/> or <see cref="EntryPointNotFoundException"/> when
    /// <c>open</c> cannot be bound, and <see cref="IOException"/> when the call itself fails.
    /// </summary>
    public static FileStream CreateExclusiveOwnerOnly(string path)
    {
        var linux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
        var flags = linux
            ? LinuxWriteOnly | LinuxCreate | LinuxExclusive | LinuxCloseOnExec
            : MacWriteOnly | MacCreate | MacExclusive | MacCloseOnExec;

        var fd = Open(path, flags, 0x180 /* 0600 */);
        if (fd < 0)
        {
            var errno = Marshal.GetLastWin32Error();
            var message = $"Could not create '{path}': {new Win32Exception(errno).Message} (errno {errno})";
            throw errno == EEXIST ? new IOException(message) : new IOException(message, errno);
        }

        var handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        try
        {
            return new FileStream(handle, FileAccess.Write);
        }
        catch
        {
            // The FileStream never took ownership, so close the descriptor here.
            handle.Dispose();
            throw;
        }
    }
}
#endif
