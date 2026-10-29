using System.Buffers.Binary;
using System.Text;
using Bijecta.CliAuth.Storage.File;

namespace Bijecta.CliAuth.StorageTests;

public sealed class WindowsRenameInformationTests
{
    [Theory]
    [InlineData(@"C:\sessions\record.json")]
    [InlineData(@"C:\users\utilisateur-é\会話\record.json")]
    [InlineData(@"\\?\C:\sessions\record.json")]
    public void NativeRenameBufferUsesPointerAlignmentAndUtf16ByteLength(string destination)
    {
        var buffer = WindowsRenameInformation.Create(destination);
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(buffer));
        // DWORD flags/padding, NULL RootDirectory, DWORD byte length, UTF-16 filename, UTF-16 NUL.
        Assert.All(buffer[sizeof(uint)..(2 * IntPtr.Size)], value => Assert.Equal((byte)0, value));
        Assert.Equal((uint)(destination.Length * sizeof(char)), BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(2 * IntPtr.Size)));
        var nameOffset = 2 * IntPtr.Size + sizeof(uint);
        Assert.Equal(destination, Encoding.Unicode.GetString(buffer, nameOffset, destination.Length * sizeof(char)));
        Assert.Equal(new byte[2], buffer[^2..]);
    }
}
