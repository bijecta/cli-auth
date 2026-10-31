using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Bijecta.CliAuth.Core;

namespace Bijecta.CliAuth.Storage.File;

// ADR 0008: the storage directory and session files must be private to the current user.
// Violations throw StorageInsecure; ordinary I/O failures surface as IOException for callers to sanitize.
internal static class StorageLocation
{
    private const UnixFileMode GroupOrOtherWrite = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
    private const UnixFileMode GroupOrOther = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    // ReadData/ListDirectory, WriteData, AppendData, DeleteSubdirectoriesAndFiles, Delete, ChangePermissions,
    // TakeOwnership, plus GENERIC_ALL/WRITE/READ, which may appear unmapped in inherited entries.
    private const uint SensitiveRights = 0x1 | 0x2 | 0x4 | 0x40 | 0x10000 | 0x40000 | 0x80000 |
        0x10000000 | 0x40000000 | 0x80000000;

    /// <summary>Validates the directory, creating it privately first when requested.</summary>
    /// <returns>False only when the directory does not exist and <paramref name="create"/> is false.</returns>
    internal static bool EnsureTrustedDirectory(string directory, bool create)
    {
        if (!Directory.Exists(directory))
        {
            if (!create) { return false; }
            CreatePrivateDirectory(directory);
        }
        // A symlinked directory is allowed; its final target must satisfy the same rules.
        var info = new DirectoryInfo(directory);
        var target = info.LinkTarget is null ? info.FullName : info.ResolveLinkTarget(returnFinalTarget: true)!.FullName;
        if (IsNetworkPath(target)) { throw Insecure(); }
        if (OperatingSystem.IsWindows())
        {
            RequirePrivateAcl(new DirectoryInfo(target).GetAccessControl());
        }
        else if ((System.IO.File.GetUnixFileMode(target) & GroupOrOtherWrite) != 0)
        {
            throw Insecure();
        }
        return true;
    }

    /// <summary>Rejects a session or lock path that is a symbolic link or junction.</summary>
    internal static void RejectLink(string path)
    {
        if (new FileInfo(path).LinkTarget is not null) { throw Insecure(); }
    }

    /// <summary>Validates an opened session file: owner-only mode on Unix, a private ACL on Windows.</summary>
    internal static void RequirePrivateFile(FileStream stream)
    {
        if (OperatingSystem.IsWindows())
        {
            RequirePrivateAcl(stream.GetAccessControl());
        }
        else if ((System.IO.File.GetUnixFileMode(stream.SafeFileHandle) & GroupOrOther) != 0)
        {
            throw Insecure();
        }
    }

    internal static bool IsNetworkPath(string fullPath)
    {
        if (OperatingSystem.IsWindows())
        {
            if (fullPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) { return true; }
            if (fullPath.StartsWith(@"\\", StringComparison.Ordinal) && !fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) &&
                !fullPath.StartsWith(@"\\.\", StringComparison.Ordinal))
            {
                return true;
            }
            var root = Path.GetPathRoot(fullPath);
            return root is not null && new DriveInfo(root).DriveType == DriveType.Network;
        }
        IEnumerable<(string Mount, DriveType Type)> mounts;
        try
        {
            mounts = [.. DriveInfo.GetDrives().Select(drive => (drive.Name, drive.DriveType))];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Unknown mount table: do not block; network shares remain documented as unsupported.
            return false;
        }
        return IsOnNetworkMount(fullPath, mounts);
    }

    // The longest mount point containing the path decides; "/" contains everything.
    internal static bool IsOnNetworkMount(string fullPath, IEnumerable<(string Mount, DriveType Type)> mounts)
    {
        (string Mount, DriveType Type)? best = null;
        foreach (var mount in mounts)
        {
            var prefix = mount.Mount.EndsWith('/') ? mount.Mount : mount.Mount + "/";
            var contains = fullPath == mount.Mount || fullPath.StartsWith(prefix, StringComparison.Ordinal) || prefix == "/";
            if (contains && (best is null || mount.Mount.Length > best.Value.Mount.Length)) { best = mount; }
        }
        return best?.Type == DriveType.Network;
    }

    private static void CreatePrivateDirectory(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return;
        }
        // Parents inherit normally; the leaf gets a protected ACL so its files never inherit broader access.
        var parent = Path.GetDirectoryName(directory);
        if (parent is not null) { Directory.CreateDirectory(parent); }
        new DirectoryInfo(directory).Create(PrivateDirectorySecurity());
    }

    [SupportedOSPlatform("windows")]
    private static DirectorySecurity PrivateDirectorySecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { CurrentUser(), new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }
        return security;
    }

    // Owned by the current user (or Administrators, as elevated administrators produce); no read, write,
    // delete, or permission rights for anyone else, including entries inherited by files.
    [SupportedOSPlatform("windows")]
    internal static void RequirePrivateAcl(FileSystemSecurity security)
    {
        var user = CurrentUser();
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || (owner != user && !owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid))) { throw Insecure(); }
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow || rule.IdentityReference is not SecurityIdentifier sid ||
                IsTrusted(sid, user))
            {
                continue;
            }
            if ((unchecked((uint)rule.FileSystemRights) & SensitiveRights) != 0) { throw Insecure(); }
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsTrusted(SecurityIdentifier sid, SecurityIdentifier user) => sid == user ||
        sid.IsWellKnown(WellKnownSidType.LocalSystemSid) || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) ||
        sid.IsWellKnown(WellKnownSidType.CreatorOwnerSid) || sid.Value == "S-1-3-4"; // OWNER RIGHTS

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw Insecure();
    }

    private static CliAuthException Insecure() => new(AuthErrorCode.StorageInsecure);
}
