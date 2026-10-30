namespace UotanStudio.Assets;

/// <summary>
/// The asset bundles this build ships, resolved from the embedded manifest on first use.
/// <para>本次构建随包的资源包，首次使用时由内嵌清单解析。</para>
/// </summary>
public static class AssetCatalog
{
    private static readonly Lazy<IReadOnlyList<AssetBundle>> All = new(CreateAll, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>MiSans font faces decoded into memory at startup.
    /// <para>启动时解码进内存的 MiSans 字形。</para>
    /// </summary>
    public static AssetBundle Fonts => BundledManifest.Get(AssetNames.Bundles.Fonts);

    /// <summary>OpenHarmony UI Test capture libraries and control agents.
    /// <para>OpenHarmony UI Test 采集库与控制代理。</para>
    /// </summary>
    public static AssetBundle OhosDevice => BundledManifest.Get(AssetNames.Bundles.OhosDevice);

    /// <summary>Vendor Rockusb driver package staged for driver installation.
    /// <para>为驱动安装暂存的厂商 Rockusb 驱动包。</para>
    /// </summary>
    public static AssetBundle RockusbDriver => BundledManifest.Get(AssetNames.Bundles.RockusbDriver);

    /// <summary>MediaTek USB VCOM driver package staged for driver installation.
    /// <para>为驱动安装暂存的联发科 USB VCOM 驱动包。</para>
    /// </summary>
    public static AssetBundle MediaTekVcomDriver => BundledManifest.Get(AssetNames.Bundles.MediaTekVcomDriver);

    /// <summary>MediaTek USB full driver package staged for driver installation.
    /// <para>为驱动安装暂存的联发科 USB 全量驱动包。</para>
    /// </summary>
    public static AssetBundle MediaTekFullDriver => BundledManifest.Get(AssetNames.Bundles.MediaTekFullDriver);

    /// <summary>Google USB driver package staged for driver installation.
    /// <para>为驱动安装暂存的 Google USB 驱动包。</para>
    /// </summary>
    public static AssetBundle GoogleUsbDriver => BundledManifest.Get(AssetNames.Bundles.GoogleUsbDriver);

    /// <summary>Rockchip ADB driver package staged for driver installation.
    /// <para>为驱动安装暂存的瑞芯微 ADB 驱动包。</para>
    /// </summary>
    public static AssetBundle RockchipAdbDriver => BundledManifest.Get(AssetNames.Bundles.RockchipAdbDriver);

    /// <summary>Qualcomm QUD driver package staged for driver installation.
    /// <para>为驱动安装暂存的高通 QUD 驱动包。</para>
    /// </summary>
    public static AssetBundle QualcommQudDriver => BundledManifest.Get(AssetNames.Bundles.QualcommQudDriver);

    /// <summary>Silicon Labs CP210x driver package staged for driver installation.
    /// <para>为驱动安装暂存的 Silicon Labs CP210x 驱动包。</para>
    /// </summary>
    public static AssetBundle SilabsCp210xDriver => BundledManifest.Get(AssetNames.Bundles.SilabsCp210xDriver);

    /// <summary>FTDI CDM driver package staged for driver installation.
    /// <para>为驱动安装暂存的 FTDI CDM 驱动包。</para>
    /// </summary>
    public static AssetBundle FtdiCdmDriver => BundledManifest.Get(AssetNames.Bundles.FtdiCdmDriver);

    /// <summary>Native arm64 Edify updater used by recovery packages.
    /// <para>卡刷包使用的原生 arm64 Edify 更新器。</para>
    /// </summary>
    public static AssetBundle DeliveryUpdater => BundledManifest.Get(AssetNames.Bundles.DeliveryUpdater);

    /// <summary>Compiled magic(5) rule table read by the format detector.
    /// <para>格式探测使用的编译型 magic(5) 规则表。</para>
    /// </summary>
    public static AssetBundle MagicRules => BundledManifest.Get(AssetNames.Bundles.MagicRules);

    /// <summary>Bundled scrcpy server jar; <see cref="AssetBundle.IsPresent"/> is <see langword="false"/> when the
    /// repository does not ship the binary. <para>随包的 scrcpy 服务端 jar；仓库不随包该二进制时
    /// <see cref="AssetBundle.IsPresent"/> 为 <see langword="false"/>。</para>
    /// </summary>
    public static AssetBundle ScrcpyServer => BundledManifest.Get(AssetNames.Bundles.ScrcpyServer);

    /// <summary>Every bundle the catalog declares, in declaration order.
    /// <para>清单声明的全部资源包，按声明顺序。</para>
    /// </summary>
    public static IReadOnlyList<AssetBundle> Bundles => All.Value;

    private static IReadOnlyList<AssetBundle> CreateAll() =>
    [
        Fonts,
        OhosDevice,
        RockusbDriver,
        MediaTekVcomDriver,
        MediaTekFullDriver,
        GoogleUsbDriver,
        RockchipAdbDriver,
        QualcommQudDriver,
        SilabsCp210xDriver,
        FtdiCdmDriver,
        DeliveryUpdater,
        MagicRules,
        ScrcpyServer,
    ];
}