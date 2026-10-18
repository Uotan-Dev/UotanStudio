namespace UotanStudio.Firmware.FileSystem.Rebuilding;

/// <summary>Explicit Linux ownership and permission changes; the inode file type is retained.
/// <para>显式修改 Linux 属主与权限；保留 inode 的文件类型。</para></summary>
/// <param name="Path">In-image path of the target entry; a leading <c>/</c> is trimmed and segments must not be empty, <c>.</c> or <c>..</c>. Hardlinked regular files share the change.
/// <para>目标项在镜像内的路径；前导 <c>/</c> 会被去除，且路径段不得为空、<c>.</c> 或 <c>..</c>。硬链接的普通文件同步变更。</para></param>
/// <param name="Uid">New numeric user identifier.<para>新的数字用户标识。</para></param>
/// <param name="Gid">New numeric group identifier.<para>新的数字组标识。</para></param>
/// <param name="Permissions">New permission bits, at most <c>0xFFF</c>; file-type bits come from the source inode.
/// <para>新权限位，最大 <c>0xFFF</c>；文件类型位取自源 inode。</para></param>
/// <param name="ExpectedUid">Guard requiring the current uid to equal this value; <see langword="null"/> skips the check.
/// <para>守卫值：要求当前 uid 与之相等；<see langword="null"/> 表示跳过检查。</para></param>
/// <param name="ExpectedGid">Guard requiring the current gid to equal this value; <see langword="null"/> skips the check.
/// <para>守卫值：要求当前 gid 与之相等；<see langword="null"/> 表示跳过检查。</para></param>
/// <param name="ExpectedPermissions">Guard requiring the current permission bits to equal this value (at most <c>0xFFF</c>); <see langword="null"/> skips the check.
/// <para>守卫值：要求当前权限位与之相等（最大 <c>0xFFF</c>）；<see langword="null"/> 表示跳过检查。</para></param>
public sealed record FileSystemAttributeEdit(string Path, uint Uid, uint Gid, ushort Permissions,
    uint? ExpectedUid = null, uint? ExpectedGid = null, ushort? ExpectedPermissions = null);