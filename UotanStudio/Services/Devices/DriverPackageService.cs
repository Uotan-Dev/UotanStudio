using UotanStudio.Comm.Transports.Usb.Drivers;

namespace UotanStudio.Services.Devices;

/// <summary>
/// One vendor driver package offered by the settings page driver installer, carrying localization keys
/// rather than display text so the view stays localizable.
/// <para>设置页驱动安装器提供的一个厂商驱动包，只携带本地化键而非显示文本，使界面可本地化。</para>
/// </summary>
/// <param name="Id">The stable package identifier used by install and removal.
/// <para>安装与移除使用的稳定包标识。</para></param>
/// <param name="NameKey">The localization key of the package display name.
/// <para>包显示名的本地化键。</para></param>
/// <param name="VendorNameKey">The localization key of the vendor description.
/// <para>厂商说明的本地化键。</para></param>
/// <param name="InfFileNames">The INF files the package stages.
/// <para>包暂存的 INF 文件。</para></param>
/// <param name="TotalBytes">The uncompressed payload size of the package.
/// <para>包解压后的负载字节数。</para></param>
/// <param name="IsAvailable">Whether the package payload is present in this build.
/// <para>本构建是否携带该包的负载。</para></param>
/// <param name="UnavailableReasonKey">The Comm reason suffix explaining a missing payload, or
/// <see langword="null"/> when the package is available or the reason is unknown.
/// <para>说明缺包原因的 Comm 原因后缀；包可用或原因未知时为 <see langword="null"/>。</para></param>
public sealed record DriverPackageEntry(string Id, string NameKey, string VendorNameKey,
    IReadOnlyList<string> InfFileNames, long TotalBytes, bool IsAvailable, string? UnavailableReasonKey);

/// <summary>
/// Platform-neutral facade over the vendor driver package catalog used by the settings page. Every
/// member is safe to call on any platform: outside Windows the list is empty and mutating operations
/// fail without touching the system.
/// <para>设置页使用的厂商驱动包目录平台中立面。所有成员在任意平台都可安全调用：非 Windows 下列表为空，
/// 变更操作直接失败且不触碰系统。</para>
/// </summary>
public interface IDriverPackageService
{
    /// <summary>
    /// Gets whether vendor driver packages can be installed here, which requires Windows.
    /// <para>获取当前环境是否可安装厂商驱动包；仅 Windows 支持。</para>
    /// </summary>
    /// <value><see langword="true"/> only on Windows.<para>仅在 Windows 上为 <see langword="true"/>。</para></value>
    bool IsSupported { get; }

    /// <summary>
    /// Gets the packages this build carries, in catalog order.
    /// <para>获取本构建携带的驱动包，按目录顺序排列。</para>
    /// </summary>
    /// <value>The available package descriptors, empty outside Windows.
    /// <para>可用的驱动包描述列表；非 Windows 下为空。</para></value>
    IReadOnlyList<DriverPackageEntry> Packages { get; }

    /// <summary>
    /// Stages one vendor driver package into the driver store and asks PnP to install it on every present
    /// device that currently has no driver. Requires an administrator token, which a non-elevated process
    /// obtains through a UAC prompt.
    /// <para>把一个厂商驱动包暂存到驱动仓库，并请求 PnP 在当前所有无驱动的在位设备上安装。需要管理员
    /// 令牌，未提权进程经 UAC 提示获得。</para>
    /// </summary>
    /// <param name="packageId">The package identifier.<para>驱动包标识。</para></param>
    /// <returns>The operation result; never throws and fails outside Windows.
    /// <para>操作结果；绝不抛出，非 Windows 下返回失败。</para></returns>
    WinUsbDriverOperationResult Install(string packageId);

    /// <summary>
    /// Removes the staged driver packages of one vendor package. Requires an administrator token and only
    /// removes packages this application staged.
    /// <para>移除一个厂商包已暂存的驱动包。需要管理员令牌，且只移除本应用暂存的包。</para>
    /// </summary>
    /// <param name="packageId">The package identifier.<para>驱动包标识。</para></param>
    /// <returns>The operation result; never throws and fails outside Windows.
    /// <para>操作结果；绝不抛出，非 Windows 下返回失败。</para></returns>
    WinUsbDriverOperationResult Uninstall(string packageId);
}

/// <summary>
/// Default <see cref="IDriverPackageService"/> backed by the Comm driver package catalog and installer.
/// <para><see cref="IDriverPackageService"/> 的默认实现，由 Comm 的驱动包目录与安装器支撑。</para>
/// </summary>
public sealed class DriverPackageService : IDriverPackageService
{
    private static readonly DriverPackageService Shared = new();

    private readonly IUsbDriverConfigService _driverConfig;

    /// <summary>
    /// Gets the shared instance used when no implementation is injected.
    /// <para>获取未注入实现时使用的共享实例。</para>
    /// </summary>
    /// <value>The default service instance.<para>默认服务实例。</para></value>
    public static IDriverPackageService Default => Shared;

    /// <summary>
    /// Initializes the service over the default USB driver configuration service.
    /// <para>基于默认 USB 驱动配置服务初始化该服务。</para>
    /// </summary>
    public DriverPackageService()
        : this(UsbDriverConfigService.Default)
    {
    }

    /// <summary>
    /// Initializes the service over an injected driver configuration service.
    /// <para>基于注入的驱动配置服务初始化该服务。</para>
    /// </summary>
    /// <param name="driverConfig">The driver configuration service that performs the elevated work.
    /// <para>执行提权操作的驱动配置服务。</para></param>
    public DriverPackageService(IUsbDriverConfigService driverConfig)
    {
        _driverConfig = driverConfig;
    }

    /// <inheritdoc />
    public bool IsSupported => OperatingSystem.IsWindows() && DriverPackageInstaller.IsSupported;

    /// <inheritdoc />
    public IReadOnlyList<DriverPackageEntry> Packages => OperatingSystem.IsWindows()
        ? [.. DriverPackageCatalog.Packages.Select(static package => new DriverPackageEntry(
            package.Id,
            $"DriverPackage_{package.DisplayNameKey}_Name",
            $"DriverPackage_{package.VendorNameKey}_Coverage",
            package.InfFileNames,
            package.TotalBytes,
            package.IsAvailable,
            package.UnavailableReasonKey))]
        : [];

    /// <inheritdoc />
    public WinUsbDriverOperationResult Install(string packageId) => _driverConfig.InstallPackage(packageId);

    /// <inheritdoc />
    public WinUsbDriverOperationResult Uninstall(string packageId) => _driverConfig.UninstallPackage(packageId);
}