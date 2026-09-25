using UotanStudio.Firmware.SharedKernel;
namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Builder;

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

    private static bool _legacyCompatibility = DefaultLegacyCompatibility;

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
}