using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Bijecta.CliAuth.Storage.File;

// FILE_RENAME_INFO with REPLACE_IF_EXISTS | POSIX_SEMANTICS preserves open readers.
// There is deliberately no copy/delete, delete-first, or legacy-rename fallback.
[SupportedOSPlatform("windows")]
internal static partial class WindowsFilePublication
{
    private const uint DeleteAccess = 0x00010000;
    private const uint ShareReadWriteDelete = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint NormalAttributes = 0x00000080;
    private const int FileRenameInfoEx = 22;

    internal static void Publish(string source, string destination)
    {
        using var handle = CreateFile(source, DeleteAccess, ShareReadWriteDelete, IntPtr.Zero,
            OpenExisting, NormalAttributes, IntPtr.Zero);
        if (handle.IsInvalid) { throw Failure(); }
        var information = WindowsRenameInformation.Create(destination);
        // The path buffer is pinned only for this synchronous native call; no session bytes enter native marshalling.
        var pinned = GCHandle.Alloc(information, GCHandleType.Pinned);
        try
        {
            if (!SetFileInformationByHandle(handle, FileRenameInfoEx, pinned.AddrOfPinnedObject(), (uint)information.Length))
            {
                throw Failure();
            }
        }
        finally { pinned.Free(); }
    }

    private static IOException Failure()
    {
        // Retain only the numeric native error internally; FileTokenStore emits a safe StorageUnavailable.
        var error = Marshal.GetLastPInvokeError();
        return new IOException("Native file publication failed.", unchecked((int)(0x80070000u | ((uint)error & 0xffffu))));
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle file, int informationClass,
        IntPtr information, uint bufferSize);
}

// Keep buffer construction platform-independent so its ABI layout can also be tested on non-Windows hosts.
// FILE_RENAME_INFO has a DWORD union, a pointer-aligned HANDLE, a DWORD length, then UTF-16 characters.
// FileName begins at offset 20 on x64/ARM64 and 12 on x86.
internal static class WindowsRenameInformation
{
    internal static byte[] Create(string destination)
    {
        var lengthOffset = IntPtr.Size * 2;
        var nameOffset = lengthOffset + sizeof(uint);
        var nameBytes = checked(destination.Length * sizeof(char));
        var buffer = new byte[checked(nameOffset + nameBytes + sizeof(char))];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0x00000001 | 0x00000002);
        // RootDirectory is NULL; destination is the host-normalized absolute path.
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(lengthOffset), (uint)nameBytes);
        for (var i = 0; i < destination.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(nameOffset + i * sizeof(char)), destination[i]);
        }
        // The zero-initialized tail supplies a UTF-16 terminator, excluded from FileNameLength.
        return buffer;
    }
}
