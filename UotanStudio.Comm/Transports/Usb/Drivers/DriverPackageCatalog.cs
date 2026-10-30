namespace UotanStudio.Comm.Transports.Usb.Drivers;

/// <summary>
/// The immutable catalog of vendor Windows driver packages this library knows about, in display order. Every
/// bundled package is backed by the frames and manifest <see cref="DriverPackageAssets"/> materializes, and each
/// entry only carries localization key suffixes, so the host owns every user-visible string. The two vendor
/// packages that are deliberately not bundled report <see cref="DriverPackageDescriptor.IsAvailable"/> as
/// <see langword="false"/> together with an <see cref="DriverPackageDescriptor.UnavailableReasonKey"/>.
/// <para>本库已知的厂商 Windows 驱动包的不可变目录，按显示顺序排列。每个随包的驱动包由
/// <see cref="DriverPackageAssets"/> 物化的帧与清单承载，条目只携带本地化 key 后缀，所有面向用户的字符串归宿主
/// 所有。两个刻意未随包的厂商包把 <see cref="DriverPackageDescriptor.IsAvailable"/> 置为
/// <see langword="false"/>，并给出 <see cref="DriverPackageDescriptor.UnavailableReasonKey"/>。</para>
/// </summary>
public static class DriverPackageCatalog
{
    private static readonly DriverPackageDescriptor[] AllPackages =
    [
        Describe("rockusb", "Rockusb", "Rockchip", ["rockusb.inf"], null),
        Describe("mediatek-vcom", "MediaTekVcom", "MediaTek", ["cdc-acm.inf"], null),
        Describe("mediatek-full", "MediaTekFull", "MediaTek",
            [
                "android_winusb.inf",
                "cdc-acm.inf",
                "mtkmbim7_x64.inf",
                "mtkmbimv_x64.inf",
                "mtkmbimx_x64.inf",
                "tetherxp.inf",
                "wpdmtp.inf",
            ],
            null),
        Describe("google-usb", "GoogleUsb", "Google", ["android_winusb.inf"], null),
        Describe("rockchip-adb", "RockchipAdb", "Rockchip", ["android_winusb.inf"], null),
        Describe("qualcomm-qud", "QualcommQud", "Qualcomm",
            ["qcadb.inf", "qcfilter.inf", "qcwdfmdm.inf", "qcwdfser.inf", "qcwwan.inf", "qdbusb.inf"],
            null),
        Describe("silabs-cp210x", "SilabsCp210x", "SiliconLabs", ["silabser.inf"], null),
        Describe("ftdi-cdm", "FtdiCdm", "Ftdi", ["Image/FTDIBUS.inf", "Image/FTDIPORT.inf"], null),
        Describe("wch-serial", "WchSerial", "Wch", [], "SelfExtractorOnly"),
        Describe("huawei-hisuite", "HuaweiHiSuite", "Huawei", [], "LicenseAndSize"),
    ];

    /// <summary>
    /// Gets every known driver package, bundled or not.
    /// <para>获取全部已知驱动包，无论是否随包。</para>
    /// </summary>
    public static IReadOnlyList<DriverPackageDescriptor> Packages => AllPackages;

    /// <summary>
    /// Finds the package with the given id, comparing ordinal case-insensitively.
    /// <para>按给定标识查找驱动包，使用序数的忽略大小写比较。</para>
    /// </summary>
    /// <param name="id">The package id, for example <c>rockusb</c>; null or blank matches nothing.
    /// <para>包标识，例如 <c>rockusb</c>；null 或空白不匹配任何包。</para></param>
    /// <returns>The matching descriptor, or <see langword="null"/> when no package has that id.
    /// <para>匹配的描述；没有该标识的包时为 <see langword="null"/>。</para></returns>
    public static DriverPackageDescriptor? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var trimmed = id.Trim();
        foreach (var package in AllPackages)
        {
            if (string.Equals(package.Id, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return package;
            }
        }

        return null;
    }

    private static DriverPackageDescriptor Describe(string id, string displayNameKey, string vendorNameKey,
        string[] infFileNames, string? unavailableReasonKey)
    {
        if (!DriverPackageAssets.TryReadManifest(id, out var files))
        {
            return new DriverPackageDescriptor
            {
                Id = id,
                DisplayNameKey = displayNameKey,
                VendorNameKey = vendorNameKey,
                InfFileNames = infFileNames,
                FileCount = 0,
                TotalBytes = 0,
                IsAvailable = false,
                UnavailableReasonKey = unavailableReasonKey ?? "AssetsMissing",
            };
        }

        var totalBytes = 0L;
        foreach (var file in files)
        {
            totalBytes += file.Length;
        }

        var declaresEveryInf = infFileNames.Length > 0;
        foreach (var infName in infFileNames)
        {
            var payloadName = DriverPackageAssets.PayloadNameOf(infName);
            if (!files.Any(file => string.Equals(file.PayloadName, payloadName, StringComparison.Ordinal)))
            {
                declaresEveryInf = false;
                break;
            }
        }

        return new DriverPackageDescriptor
        {
            Id = id,
            DisplayNameKey = displayNameKey,
            VendorNameKey = vendorNameKey,
            InfFileNames = infFileNames,
            FileCount = files.Count,
            TotalBytes = totalBytes,
            IsAvailable = OperatingSystem.IsWindows() && declaresEveryInf,
            UnavailableReasonKey = unavailableReasonKey,
        };
    }
}