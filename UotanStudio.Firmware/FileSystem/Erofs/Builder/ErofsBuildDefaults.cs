namespace UotanStudio.Firmware.FileSystem.Erofs.Builder;

/// <summary>
/// <para>进程级 EROFS 构建默认值，宿主设置在启动时同步到这里，使不依赖 UI 的构建路径
/// （重打包、格式处理器）也能遵循用户选择。</para>
/// Process-wide EROFS build defaults, synced from host settings at startup so build paths without
/// UI access (repack, format handlers) also honor the user's choice.
/// </summary>
public static class ErofsBuildDefaults
{
    /// <summary>
    /// <para>新建镜像的默认 legacy（pre-5.13）兼容值：旧内核（Linux 5.4/5.10）无需 big-pcluster 特性即可挂载。</para>
    /// Default legacy (pre-5.13) compatibility for newly built images: older kernels (Linux 5.4/5.10)
    /// can mount them without the big-pcluster feature.
    /// </summary>
    public const bool DefaultLegacyCompatibility = true;

    /// <summary>
    /// Default for the EROFS rebuild compression search: when enabled, a preserved rebuild may build the
    /// whole image up to three times to keep the smallest candidate, and only for snapshots that already
    /// carried compression estimates at extraction time.
    /// <para>EROFS 重建压缩搜索的默认值：开启后受保留重建最多按候选编码器完整构建镜像三次以保留最小候选，
    /// 且只对解包时已采集到压缩估算的快照生效。</para>
    /// </summary>
    public const bool DefaultRebuildOptimization = false;

    private static bool _legacyCompatibility = DefaultLegacyCompatibility;
    private static bool _rebuildOptimization = DefaultRebuildOptimization;

    /// <summary>Legacy (pre-5.13) layout for EROFS images built without explicit options.
    /// <para>未显式给出选项时构建的 EROFS 镜像采用 legacy（pre-5.13）布局。</para></summary>
    /// <value>Process-wide flag read and written with volatile access so host settings synced at startup
    /// are observed by concurrent builds; seeded from <see cref="DefaultLegacyCompatibility"/>.
    /// <para>进程级标志，以 volatile 方式读写，使启动时同步的宿主设置能被并发构建观察到；初值来自
    /// <see cref="DefaultLegacyCompatibility"/>。</para></value>
    public static bool LegacyCompatibility
    {
        get => Volatile.Read(ref _legacyCompatibility);
        set => Volatile.Write(ref _legacyCompatibility, value);
    }

    /// <summary>Searches for the smallest EROFS rebuild among the compression estimates captured at
    /// extraction instead of publishing the single inherited-profile build.
    /// <para>在解包时采集的压缩估算之间搜索最小的 EROFS 重建结果，而不是直接发布单次继承档案构建。</para></summary>
    /// <value>Process-wide flag read and written with volatile access, seeded from
    /// <see cref="DefaultRebuildOptimization"/> and disabled by default because one rebuild may build the
    /// complete image up to three times. It is honoured only when the preservation snapshot already carries
    /// compression estimates, which requires the flag to have been enabled while the image was extracted.
    /// <para>进程级标志，以 volatile 方式读写，初值来自 <see cref="DefaultRebuildOptimization"/>，默认关闭，
    /// 因为一次重建最多完整构建镜像三次。仅当保留快照已携带压缩估算时才会生效，而这要求解包该镜像时本标志
    /// 已开启。</para></value>
    public static bool RebuildOptimization
    {
        get => Volatile.Read(ref _rebuildOptimization);
        set => Volatile.Write(ref _rebuildOptimization, value);
    }
}