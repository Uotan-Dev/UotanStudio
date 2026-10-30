namespace UotanStudio.Comm.Transports.Usb.Drivers;

/// <summary>
/// Describes one vendor Windows driver package: the stable package id, the localization key suffixes the
/// host turns into display text, the INF files the install path stages, and the size of the materialized
/// payload. The descriptor carries no localized text, so the host owns every user-visible string.
/// <para>描述一个厂商 Windows 驱动包：稳定的包标识、宿主用于生成显示文案的本地化 key 后缀、安装路径要暂存的
/// INF 文件，以及物化后负载的大小。描述不携带任何本地化文案，所有面向用户的字符串归宿主所有。</para>
/// </summary>
public sealed record DriverPackageDescriptor
{
    /// <summary>
    /// Gets the stable package id, for example <c>mediatek-vcom</c>, used to look the package up and to name
    /// its embedded assets.
    /// <para>获取稳定的包标识，例如 <c>mediatek-vcom</c>，用于查找驱动包并命名其内嵌资源。</para>
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the localization key suffix for the package display name.
    /// <para>获取包显示名的本地化 key 后缀。</para>
    /// </summary>
    public required string DisplayNameKey { get; init; }

    /// <summary>
    /// Gets the localization key suffix for the vendor name.
    /// <para>获取厂商名的本地化 key 后缀。</para>
    /// </summary>
    public required string VendorNameKey { get; init; }

    /// <summary>
    /// Gets the INF files, as paths relative to the materialized package root, that the install path stages;
    /// the list is empty for a package that is not bundled.
    /// <para>获取安装路径要暂存的 INF 文件（相对物化包根的路径）；未内置的包为空列表。</para>
    /// </summary>
    public required IReadOnlyList<string> InfFileNames { get; init; }

    /// <summary>
    /// Gets the number of files the package materializes.
    /// <para>获取包物化出的文件数量。</para>
    /// </summary>
    public required int FileCount { get; init; }

    /// <summary>
    /// Gets the total uncompressed payload size in bytes, or <c>0</c> when the package is not bundled.
    /// <para>获取未压缩负载的总字节数；未内置的包为 <c>0</c>。</para>
    /// </summary>
    public required long TotalBytes { get; init; }

    /// <summary>
    /// Gets whether this build can install the package here, which requires intact embedded assets and a
    /// Windows platform.
    /// <para>获取此构建能否在当前环境安装该包：需要内嵌资源完整且平台为 Windows。</para>
    /// </summary>
    public required bool IsAvailable { get; init; }

    /// <summary>
    /// Gets the localization key suffix explaining why the package is not bundled, or <see langword="null"/>
    /// for a bundled package.
    /// <para>获取说明包未内置原因的本地化 key 后缀；已内置的包为 <see langword="null"/>。</para>
    /// </summary>
    public string? UnavailableReasonKey { get; init; }
}