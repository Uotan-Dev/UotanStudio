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
    /// <summary>The device-id coverage per package id, built lazily from the embedded INF files.
    /// <para>按包标识缓存的设备 ID 覆盖表，从内嵌 INF 文件惰性构建。</para></summary>
    private static readonly Dictionary<string, IReadOnlyList<string>> CoverageById =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Guards <see cref="CoverageById"/> construction. <para>守护 <see cref="CoverageById"/> 的构建。</para></summary>
    private static readonly Lock CoverageSync = new();

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

    /// <summary>
    /// Finds the first catalog package whose bundled INFs claim one of the given device ids, comparing
    /// ordinal case-insensitively. The ids may be hardware ids or compatible ids, mirroring how PnP ranks a
    /// staged driver; packages without bundled INFs never match. Coverage is parsed from the embedded INF
    /// files once per package and cached for the process lifetime.
    /// <para>查找其内置 INF 声明了给定设备 ID 中任意一个的首个目录包，按序数忽略大小写比较。这些 ID 可以是
    /// 硬件 ID 或兼容 ID，与 PnP 为已暂存驱动排序的方式一致；没有内置 INF 的包绝不匹配。覆盖表从内嵌 INF
    /// 解析一次并按包缓存到进程生命周期结束。</para>
    /// </summary>
    /// <param name="deviceIds">The device ids to match, for example the hardware and compatible ids of one
    /// USB device node; null or empty matches nothing.
    /// <para>要匹配的设备 ID，例如一个 USB 设备节点的硬件与兼容 ID；null 或空白不匹配任何包。</para></param>
    /// <returns>The first matching package in catalog order, or <see langword="null"/> when no package
    /// claims one of the ids.
    /// <para>按目录顺序首个匹配的包；没有任何包声明这些 ID 时为 <see langword="null"/>。</para></returns>
    public static DriverPackageDescriptor? FindMatch(IReadOnlyList<string>? deviceIds)
    {
        if (deviceIds is not { Count: > 0 })
        {
            return null;
        }

        lock (CoverageSync)
        {
            foreach (var package in AllPackages)
            {
                if (package.InfFileNames.Count == 0)
                {
                    continue;
                }

                var coverage = GetCoverage(package);
                foreach (var deviceId in deviceIds)
                {
                    if (coverage.Contains(deviceId, StringComparer.OrdinalIgnoreCase))
                    {
                        return package;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the device ids one package's bundled INFs claim, caching the result per package id. Packages
    /// whose INFs cannot be read report an empty coverage and never match.
    /// <para>读取一个包的内置 INF 声明的设备 ID，并按包标识缓存结果。无法读取 INF 的包上报空覆盖表，
    /// 绝不参与匹配。</para>
    /// </summary>
    /// <param name="package">The package descriptor. <para>驱动包描述。</para></param>
    /// <returns>The claimed device ids, possibly empty. <para>声明的设备 ID；可能为空。</para></returns>
    private static IReadOnlyList<string> GetCoverage(DriverPackageDescriptor package)
    {
        if (CoverageById.TryGetValue(package.Id, out var cached))
        {
            return cached;
        }

        HashSet<string> claimed = new(StringComparer.OrdinalIgnoreCase);
        foreach (var infName in package.InfFileNames)
        {
            if (DriverPackageAssets.TryReadFile(package.Id, infName) is not { } inf ||
                !DriverPackageInfReader.TryRead(inf, out var metadata) || metadata is null)
            {
                continue;
            }

            foreach (var hardwareId in metadata.HardwareIds)
            {
                claimed.Add(hardwareId);
            }
        }

        IReadOnlyList<string> coverage = [.. claimed];
        CoverageById.Add(package.Id, coverage);
        return coverage;
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