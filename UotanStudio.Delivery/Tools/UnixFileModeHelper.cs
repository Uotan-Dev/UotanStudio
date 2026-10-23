namespace UotanStudio.Delivery;

/// <summary>
/// Grants the standard 0755-style Unix permission bits to a delivered file so scripts and native tools stay
/// executable after ZIP extraction; on Windows this is a no-op.
/// <para>为交付文件授予标准的 0755 风格 Unix 权限位，使脚本与原生工具在 ZIP 解压后仍可执行；Windows 上为空操作。</para>
/// </summary>
internal static class DeliveryUnixPermissions
{
    /// <summary>
    /// Applies user/group/other read-execute permissions (plus user write) to <paramref name="path"/> on
    /// Unix platforms; on Windows it returns without touching the file.
    /// <para>在 Unix 平台上为 <paramref name="path"/> 应用用户/组/其他的读执行权限（外加用户写权限）；
    /// Windows 上直接返回，不改动文件。</para>
    /// </summary>
    /// <param name="path">Absolute path of the delivered file. <para>交付文件的绝对路径。</para></param>
    internal static void SetExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }
}