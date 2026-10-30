namespace UotanStudio.Assets;

/// <summary>
/// Stable keys of the shipped bundles and of individual files inside them.
/// <para>随包资源包及其内部文件的稳定键。</para>
/// </summary>
public static class AssetNames
{
    /// <summary>Bundle keys, which are also directory paths below the asset root.
    /// <para>资源包键，同时是资源根下的目录路径。</para>
    /// </summary>
    public static class Bundles
    {
        /// <summary>MiSans font faces. <para>MiSans 字形。</para></summary>
        public const string Fonts = "fonts";

        /// <summary>OpenHarmony UI Test capture libraries and control agents.
        /// <para>OpenHarmony UI Test 采集库与控制代理。</para></summary>
        public const string OhosDevice = "ohos/device";

        /// <summary>Vendor Rockusb driver package. <para>厂商 Rockusb 驱动包。</para></summary>
        public const string RockusbDriver = "drivers/rockusb";
        /// <summary>MediaTek USB VCOM driver package. <para>联发科 USB VCOM 驱动包。</para></summary>
        public const string MediaTekVcomDriver = "drivers/mediatek-vcom";
        /// <summary>MediaTek USB full driver package. <para>联发科 USB 全量驱动包。</para></summary>
        public const string MediaTekFullDriver = "drivers/mediatek-full";
        /// <summary>Google USB driver package. <para>Google USB 驱动包。</para></summary>
        public const string GoogleUsbDriver = "drivers/google-usb";
        /// <summary>Rockchip ADB driver package. <para>瑞芯微 ADB 驱动包。</para></summary>
        public const string RockchipAdbDriver = "drivers/rockchip-adb";
        /// <summary>Qualcomm QUD driver package. <para>高通 QUD 驱动包。</para></summary>
        public const string QualcommQudDriver = "drivers/qualcomm-qud";
        /// <summary>Silicon Labs CP210x driver package. <para>Silicon Labs CP210x 驱动包。</para></summary>
        public const string SilabsCp210xDriver = "drivers/silabs-cp210x";
        /// <summary>FTDI CDM driver package. <para>FTDI CDM 驱动包。</para></summary>
        public const string FtdiCdmDriver = "drivers/ftdi-cdm";

        /// <summary>Native arm64 Edify updater. <para>原生 arm64 Edify 更新器。</para></summary>
        public const string DeliveryUpdater = "delivery";

        /// <summary>Compiled magic(5) rule table. <para>编译型 magic(5) 规则表。</para></summary>
        public const string MagicRules = "firmware/magic";

        /// <summary>Bundled scrcpy server jar; empty unless the repository ships it.
        /// <para>随包的 scrcpy 服务端 jar；仓库未随包时为空。</para></summary>
        public const string ScrcpyServer = "scrcpy";
    }

    /// <summary>File names inside <see cref="Bundles.Fonts"/>.
    /// <para><see cref="Bundles.Fonts"/> 内的文件名。</para>
    /// </summary>
    public static class Fonts
    {
        /// <summary>Regular face. <para>常规字重字形。</para></summary>
        public const string Regular = "MiSans-Regular.ttf";

        /// <summary>Bold face. <para>粗体字重字形。</para></summary>
        public const string Bold = "MiSans-Bold.ttf";
    }

    /// <summary>File names inside <see cref="Bundles.DeliveryUpdater"/>.
    /// <para><see cref="Bundles.DeliveryUpdater"/> 内的文件名。</para>
    /// </summary>
    public static class Delivery
    {
        /// <summary>Native updater executable. <para>原生更新器可执行文件。</para></summary>
        public const string Updater = "update-binary-arm64";
    }

    /// <summary>File names inside <see cref="Bundles.MagicRules"/>.
    /// <para><see cref="Bundles.MagicRules"/> 内的文件名。</para>
    /// </summary>
    public static class Magic
    {
        /// <summary>Serialized rule table. <para>序列化规则表。</para></summary>
        public const string Rules = "CompiledMagicData";
    }

    /// <summary>File names inside <see cref="Bundles.ScrcpyServer"/>.
    /// <para><see cref="Bundles.ScrcpyServer"/> 内的文件名。</para>
    /// </summary>
    public static class Scrcpy
    {
        /// <summary>Server jar pushed to the device. <para>推送到设备的服务端 jar。</para></summary>
        public const string ServerJar = "scrcpy-server.jar";
    }
}