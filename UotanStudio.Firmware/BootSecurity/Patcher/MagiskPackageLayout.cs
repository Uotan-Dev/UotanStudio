namespace UotanStudio.Firmware.BootSecurity.Patcher;

/// <summary>
/// Derives the Magisk payload layout and the installable runtime entries from package entry names, so the
/// analyzer and the patcher agree without consulting version strings, which forked builds do not publish in a
/// parseable form.
/// <para>根据包内条目名推导 Magisk 载荷布局与可安装的运行时条目，使分析器与修补器不依赖版本字符串取得一致，
/// 因为分支构建并不发布可解析的版本号。</para>
/// </summary>
internal static class MagiskPackageLayout
{
    /// <summary>Reports whether the package splits the daemon into 32-bit and 64-bit native libraries.
    /// <para>报告包是否把守护进程拆分为 32 位与 64 位原生命令库。</para>
    /// </summary>
    /// <param name="entryNames">Entry names of the package, forward-slash normalized.
    /// <para>包的条目名，已规范为正斜杠。</para></param>
    /// <returns><see langword="true"/> when the consolidated library is absent and a split library is present.
    /// <para>合并库不存在且存在分体库时为 <see langword="true"/>。</para></returns>
    public static bool UsesSplitLayout(IReadOnlyList<string> entryNames)
        => !entryNames.Any(name => NameEndsWith(name, "libmagisk.so"))
            && entryNames.Any(name => NameEndsWith(name, "libmagisk32.so") || NameEndsWith(name, "libmagisk64.so"));

    /// <summary>Reports whether one entry is a runtime payload the patched image has to carry.
    /// <para>报告某个条目是否为修补后的镜像必须携带的运行时载荷。</para>
    /// </summary>
    /// <param name="name">Entry file name without its directory.
    /// <para>不含目录的条目文件名。</para></param>
    /// <param name="splitLayout">Whether the package uses the split native libraries.
    /// <para>包是否使用分体原生命令库。</para></param>
    /// <returns><see langword="true"/> for the init, daemon, stub and init-ld payloads of the active layout.
    /// <para>当前布局的 init、守护进程、stub 与 init-ld 载荷为 <see langword="true"/>。</para></returns>
    public static bool IsRuntimeEntry(string name, bool splitLayout)
        => NameEndsWith(name, "libmagiskinit.so")
            || NameEndsWith(name, "stub.apk")
            || NameEndsWith(name, "libinit-ld.so")
            || (splitLayout
                ? NameEndsWith(name, "libmagisk32.so") || NameEndsWith(name, "libmagisk64.so")
                : NameEndsWith(name, "libmagisk.so"));

    /// <summary>Resolves the ramdisk path one runtime entry is installed at.
    /// <para>解析某个运行时条目在 ramdisk 中安装到的路径。</para>
    /// </summary>
    /// <param name="name">Entry file name without its directory.
    /// <para>不含目录的条目文件名。</para></param>
    /// <param name="initTarget">Ramdisk path the init replacement is written to.
    /// <para>init 替换物写入的 ramdisk 路径。</para></param>
    /// <param name="splitLayout">Whether the package uses the split native libraries.
    /// <para>包是否使用分体原生命令库。</para></param>
    /// <returns>The destination path, or <see langword="null"/> for an entry that carries no payload.
    /// <para>目标路径；不携带载荷的条目为 <see langword="null"/>。</para></returns>
    public static string? Target(string name, string initTarget, bool splitLayout)
    {
        if (NameEndsWith(name, "libmagiskinit.so") || name.Equals("magiskinit", StringComparison.OrdinalIgnoreCase)
            || name.Equals("init", StringComparison.OrdinalIgnoreCase)) return initTarget;
        if (NameEndsWith(name, "libmagisk.so") || name.Equals("magisk", StringComparison.OrdinalIgnoreCase)) return "overlay.d/sbin/magisk.xz";
        if (NameEndsWith(name, "libmagisk32.so") || name.Equals("magisk32", StringComparison.OrdinalIgnoreCase)) return "overlay.d/sbin/magisk32.xz";
        if (NameEndsWith(name, "libmagisk64.so") || name.Equals("magisk64", StringComparison.OrdinalIgnoreCase))
            return splitLayout ? "overlay.d/sbin/magisk64.xz" : "overlay.d/sbin/magisk.xz";
        if (NameEndsWith(name, "stub.apk") || name.Equals("stub", StringComparison.OrdinalIgnoreCase)) return "overlay.d/sbin/stub.xz";
        if (NameEndsWith(name, "libinit-ld.so") || name.Equals("init-ld", StringComparison.OrdinalIgnoreCase)) return "overlay.d/sbin/init-ld.xz";
        return name.EndsWith(".xz", StringComparison.OrdinalIgnoreCase) ? $"overlay.d/sbin/{name}" : null;
    }

    /// <summary>Reports whether a package at least carries one installable runtime entry.
    /// <para>报告包是否至少携带一个可安装的运行时条目。</para>
    /// </summary>
    /// <param name="entryNames">Entry names of the package, forward-slash normalized.
    /// <para>包的条目名，已规范为正斜杠。</para></param>
    /// <returns><see langword="true"/> when any entry passes <see cref="IsRuntimeEntry"/> for the derived layout.
    /// <para>任一条目在推导出的布局下通过 <see cref="IsRuntimeEntry"/> 时为 <see langword="true"/>。</para></returns>
    public static bool HasRuntimePayload(IReadOnlyList<string> entryNames)
    {
        var splitLayout = UsesSplitLayout(entryNames);
        return entryNames.Any(name => IsRuntimeEntry(Path.GetFileName(name), splitLayout));
    }

    private static bool NameEndsWith(string name, string suffix)
        => Path.GetFileName(name).Equals(suffix, StringComparison.OrdinalIgnoreCase);
}