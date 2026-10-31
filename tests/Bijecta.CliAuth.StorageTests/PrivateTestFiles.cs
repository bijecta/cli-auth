using Bijecta.CliAuth.Storage.File;

namespace Bijecta.CliAuth.StorageTests;

// Synthetic records must satisfy the store's ADR 0008 location rules, or reads fail as StorageInsecure.
internal static class PrivateTestFiles
{
    // Created exactly as the store creates it: 0700 on Unix, a protected owner-only ACL on Windows.
    internal static void CreateDirectory(string path) => StorageLocation.EnsureTrustedDirectory(path, create: true);

    internal static async Task WriteAsync(string path, string content, CancellationToken ct)
    {
        CreateDirectory(Path.GetDirectoryName(path)!);
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
        await using var stream = new FileStream(path, options);
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(content.AsMemory(), ct);
    }
}
