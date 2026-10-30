using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Bijecta.CliAuth.Storage.File;

internal static partial class UnixSessionLock
{
    private const int ExclusiveNonblocking = 2 | 4;

    internal static bool TryAcquire(SafeFileHandle handle)
    {
        var result = OperatingSystem.IsMacOS()
            ? MacFlock(handle, ExclusiveNonblocking)
            : LinuxFlock(handle, ExclusiveNonblocking);
        if (result == 0) { return true; }
        var error = Marshal.GetLastPInvokeError();
        // EINTR also retries through the bounded, cancellable asynchronous loop.
        if (error == 4 || error == (OperatingSystem.IsMacOS() ? 35 : 11)) { return false; }
        throw new IOException("Native session locking failed.", error);
    }

    [LibraryImport("libc.so.6", EntryPoint = "flock", SetLastError = true)]
    private static partial int LinuxFlock(SafeFileHandle handle, int operation);

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "flock", SetLastError = true)]
    private static partial int MacFlock(SafeFileHandle handle, int operation);
}
