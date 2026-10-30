using UotanStudio.Firmware.BootSecurity.Patcher;
using UotanStudio.Net.Components.Root;

namespace UotanStudio.Services.PatchPackages;

/// <summary>Root patch package families the boot patch window can provision.
/// <para>修补 Boot 窗口可在线供给的根补丁包类型族。</para></summary>
/// <remarks>The declaration order matches the source list the window presents, so the combo box index maps
/// directly onto this enum. <para>声明顺序与窗口呈现的列表顺序一致，因此下拉框索引可直接映射到本枚举。</para></remarks>
public enum RootPatchPackageSource
{
    /// <summary>Official Magisk release.<para>官方 Magisk 发布版。</para></summary>
    Magisk,
    /// <summary>Magisk Alpha fork maintained by its own upstream.<para>由各自上游维护的 Magisk Alpha 分支。</para></summary>
    MagiskAlpha,
    /// <summary>Official KernelSU release.<para>官方 KernelSU 发布版。</para></summary>
    KernelSu,
    /// <summary>SukiSU release, patched through the KernelSU path.<para>SukiSU 发布版，按 KernelSU 路径修补。</para></summary>
    SukiSu,
    /// <summary>Official APatch release.<para>官方 APatch 发布版。</para></summary>
    APatch,
}

/// <summary>
/// Maps <see cref="RootPatchPackageSource"/> onto the upstream vendor key, storage folder and expected package family.
/// <para>把 <see cref="RootPatchPackageSource"/> 映射到上游供应方键、存储文件夹与期望的包类型族。</para>
/// </summary>
public static class RootPatchSources
{
    /// <summary>KernelPatch superkey the flash panel's root patch uses when the caller supplies none.
    /// <para>闪刷面板的 Root 修补在调用方未提供密钥时使用的 KernelPatch 超级密钥。</para>
    /// </summary>
    /// <remarks>The patch core accepts any non-empty key. The boot patch window leaves its superkey field
    /// empty for the user to fill; this value only backs the flash panel, which has no key input.
    /// <para>修补核心接受任意非空密钥。修补 Boot 窗口的超级密钥框默认留空由用户填写；该值只供没有密钥输入的
    /// 闪刷面板内部使用。</para></remarks>
    public const string KernelPatchDefaultSuperKey = "123";

    /// <summary>Every provisionable source in presentation order.
    /// <para>按呈现顺序排列的全部可供给来源。</para></summary>
    /// <value>Five stable entries; the collection is never mutated after initialization.
    /// <para>五个稳定条目；初始化后不再修改该集合。</para></value>
    public static IReadOnlyList<RootPatchPackageSource> All { get; } =
    [
        RootPatchPackageSource.Magisk,
        RootPatchPackageSource.MagiskAlpha,
        RootPatchPackageSource.KernelSu,
        RootPatchPackageSource.SukiSu,
        RootPatchPackageSource.APatch,
    ];

    /// <summary>Returns the upstream catalog key of a source.
    /// <para>返回来源对应的上游清单键。</para></summary>
    /// <param name="source">Provisionable source. <para>可供给来源。</para></param>
    /// <returns>The key from <see cref="RootPatchCatalog"/>, which is also the storage folder name.
    /// <para><see cref="RootPatchCatalog"/> 中的键，同时作为存储文件夹名。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="source"/> is not a declared value.
    /// <para><paramref name="source"/> 不是已声明的取值。</para></exception>
    public static string Key(this RootPatchPackageSource source) => source switch
    {
        RootPatchPackageSource.Magisk => RootPatchCatalog.MagiskKey,
        RootPatchPackageSource.MagiskAlpha => RootPatchCatalog.MagiskAlphaKey,
        RootPatchPackageSource.KernelSu => RootPatchCatalog.KernelSuKey,
        RootPatchPackageSource.SukiSu => RootPatchCatalog.SukiSuKey,
        RootPatchPackageSource.APatch => RootPatchCatalog.APatchKey,
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown root patch source."),
    };

    /// <summary>Returns the folder name that holds this source's downloaded packages.
    /// <para>返回存放该来源已下载补丁包的文件夹名。</para></summary>
    /// <param name="source">Provisionable source. <para>可供给来源。</para></param>
    /// <returns>The lower-case upstream key, which is a stable on-disk name.
    /// <para>小写的上游键，即稳定的磁盘目录名。</para></returns>
    public static string CommandFolderName(this RootPatchPackageSource source) => source.Key();

    /// <summary>Maps a source list index onto a source.
    /// <para>把来源列表索引映射为来源。</para></summary>
    /// <param name="index">Zero-based index in presentation order. <para>按呈现顺序的零基索引。</para></param>
    /// <returns>The matching source, or <see langword="null"/> when the index is outside the list.
    /// <para>匹配的来源；索引超出列表时为 <see langword="null"/>。</para></returns>
    public static RootPatchPackageSource? FromIndex(int index)
        => index >= 0 && index < All.Count ? All[index] : null;

    /// <summary>Returns the package family a source's downloads are expected to contain.
    /// <para>返回该来源的下载文件预期包含的包类型族。</para></summary>
    /// <param name="source">Provisionable source. <para>可供给来源。</para></param>
    /// <returns>
    /// <see cref="BootPatchKind.Magisk"/> for both Magisk sources and <see cref="BootPatchKind.APatch"/> for
    /// APatch; the KernelSU-path sources list <see cref="BootPatchKind.KernelSu"/> but also publish standalone
    /// kernel packages. Use <see cref="Accepts"/> for the full verdict, because SukiSU also accepts KernelPatch
    /// packages and both KernelSU-path sources accept GKI kernel packages.
    /// <para>两个 Magisk 来源为 <see cref="BootPatchKind.Magisk"/>，APatch 为 <see cref="BootPatchKind.APatch"/>；
    /// KernelSU 路径来源声明 <see cref="BootPatchKind.KernelSu"/>，但也发布独立内核包。完整判定请用
    /// <see cref="Accepts"/>，因为 SukiSU 也接受 KernelPatch 包，两个 KernelSU 路径来源也接受 GKI 内核包。</para>
    /// </returns>
    public static BootPatchKind ExpectedKind(this RootPatchPackageSource source) => source switch
    {
        RootPatchPackageSource.Magisk or RootPatchPackageSource.MagiskAlpha => BootPatchKind.Magisk,
        RootPatchPackageSource.KernelSu or RootPatchPackageSource.SukiSu => BootPatchKind.KernelSu,
        RootPatchPackageSource.APatch => BootPatchKind.APatch,
        _ => BootPatchKind.Unknown,
    };

    /// <summary>Reports whether a detected package family can be patched from this source.
    /// <para>判断检测到的包类型族能否由该来源修补。</para></summary>
    /// <param name="source">Provisionable source. <para>可供给来源。</para></param>
    /// <param name="kind">Detected package family. <para>检测到的包类型族。</para></param>
    /// <returns><see langword="true"/> for the expected family; SukiSU also accepts KernelPatch/KPM builds, and
    /// both KernelSU-path sources accept <see cref="BootPatchKind.GkiKernel"/> kernel packages that patch in
    /// GKI mode. <para>类型族符合预期时为 <see langword="true"/>；SukiSU 另接受 KernelPatch/KPM 构建，两个
    /// KernelSU 路径来源也接受按 GKI 模式修补的 <see cref="BootPatchKind.GkiKernel"/> 内核包。</para></returns>
    public static bool Accepts(this RootPatchPackageSource source, BootPatchKind kind)
        => kind == source.ExpectedKind()
            || (source == RootPatchPackageSource.SukiSu && kind == BootPatchKind.KernelPatch)
            || (source is RootPatchPackageSource.KernelSu or RootPatchPackageSource.SukiSu
                && kind == BootPatchKind.GkiKernel);

    /// <summary>Returns the KernelPatch superkey the flash panel uses for this source when the caller supplies none.
    /// <para>返回闪刷面板在该来源未提供密钥时使用的 KernelPatch 超级密钥。</para></summary>
    /// <param name="source">Provisionable source. <para>可供给来源。</para></param>
    /// <returns><see cref="KernelPatchDefaultSuperKey"/> for SukiSU; <see langword="null"/> otherwise.
    /// <para>SukiSU 为 <see cref="KernelPatchDefaultSuperKey"/>；其他来源为 <see langword="null"/>。</para></returns>
    public static string? DefaultSuperKey(this RootPatchPackageSource source)
        => source == RootPatchPackageSource.SukiSu ? KernelPatchDefaultSuperKey : null;
}