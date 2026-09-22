namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Rebuilding;

/// <summary>Sets or deletes a user-namespace xattr; null means absent and empty bytes mean an existing empty value.</summary>
public sealed record FileSystemXattrEdit(string Path, string Name, byte[]? Value,
    bool CheckOldValue = false, byte[]? ExpectedValue = null);