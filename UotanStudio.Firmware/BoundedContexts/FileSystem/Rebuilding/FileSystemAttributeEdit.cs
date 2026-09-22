namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Rebuilding;

/// <summary>Explicit Linux ownership and permission changes; the inode file type is retained.</summary>
public sealed record FileSystemAttributeEdit(string Path, uint Uid, uint Gid, ushort Permissions,
    uint? ExpectedUid = null, uint? ExpectedGid = null, ushort? ExpectedPermissions = null);