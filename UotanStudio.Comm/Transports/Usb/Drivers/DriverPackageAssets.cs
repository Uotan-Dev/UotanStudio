using UotanStudio.Assets;

namespace UotanStudio.Comm.Transports.Usb.Drivers;

/// <summary>
/// One file declared by a bundled driver package: the flat payload name it is stored under, its decoded
/// length, and the lowercase SHA-256 of its payload.
/// <para>随包驱动包声明的一个文件：它在包内使用的扁平负载名、解码长度，以及负载的小写 SHA-256。</para>
/// </summary>
/// <param name="PayloadName">Flat file name, where <c>__</c> encodes one directory level.
/// <para>扁平文件名，其中 <c>__</c> 编码一级目录。</para></param>
/// <param name="Length">Decoded byte count. <para>解码后的字节数。</para></param>
/// <param name="Sha256">Lowercase hexadecimal SHA-256 of the decoded payload.
/// <para>解码负载的小写十六进制 SHA-256。</para></param>
internal sealed record DriverPackageFile(string PayloadName, int Length, string Sha256);

/// <summary>
/// Materializes a bundled vendor driver package into a temporary directory. A package is the
/// <c>drivers/&lt;packageId&gt;</c> asset bundle, and payload names are flat because an asset path cannot carry
/// directories: <c>x64__usb2ser.sys</c> stands for <c>x64/usb2ser.sys</c> and is restored here, which is the
/// layout the staged INF expects. Packages whose INFs bind WinUSB also receive the amd64 coinstallers, which
/// this build stores once in <see cref="AssetCatalog.WinUsbCoinstaller"/> instead of duplicating them inside
/// every package bundle; the files a bundle already carries win over the shared copy. Lengths and digests are
/// checked by <see cref="AssetStore"/> before any byte reaches the disk.
/// <para>把随包的厂商驱动包物化到临时目录。一个包就是 <c>drivers/&lt;packageId&gt;</c> 资源包，负载名是扁平的，
/// 因为资源路径不能携带目录：<c>x64__usb2ser.sys</c> 代表 <c>x64/usb2ser.sys</c> 并在此还原，正是暂存 INF 所需
/// 的布局。INF 绑定 WinUSB 的包还会额外获得 amd64 coinstaller；本构建把它们统一存放在
/// <see cref="AssetCatalog.WinUsbCoinstaller"/>，而不是在每个包资源包里各存一份，包内已有的同名文件优先于共享
/// 副本。任何字节落盘前都由 <see cref="AssetStore"/> 校验长度与摘要。</para>
/// </summary>
internal static class DriverPackageAssets
{
    private const string BundlePrefix = "drivers/";
    private const string DirectorySeparator = "__";
    private const int MaximumPayloadNameLength = 128;

    /// <summary>The INF whose amd64 coinstallers come from the shared bundle; it registers both
    /// <c>WinUSBCoInstaller2.dll</c> and <c>WdfCoInstaller01009.dll</c> in its source-disks and
    /// coinstaller sections, so a package that carries it cannot stage without them.
    /// <para>其 amd64 coinstaller 来自共享资源包的 INF；它在 source-disks 与 coinstaller 段同时登记
    /// <c>WinUSBCoInstaller2.dll</c> 与 <c>WdfCoInstaller01009.dll</c>，因此携带它的包缺少这两个文件即无法暂存。</para>
    /// </summary>
    private const string WinUsbInfFileName = "android_winusb.inf";

    /// <summary>
    /// Restores the package-relative path from a flat payload name.
    /// <para>从扁平负载名还原包内相对路径。</para>
    /// </summary>
    /// <param name="payloadName">Flat payload name, for example <c>x64__usb2ser.sys</c>.
    /// <para>扁平负载名，例如 <c>x64__usb2ser.sys</c>。</para></param>
    /// <returns>The path with <c>/</c> separators, for example <c>x64/usb2ser.sys</c>.
    /// <para>使用 <c>/</c> 分隔的路径，例如 <c>x64/usb2ser.sys</c>。</para></returns>
    internal static string RelativePathOf(string payloadName)
        => payloadName.Replace(DirectorySeparator, "/", StringComparison.Ordinal);

    /// <summary>
    /// Flattens a package-relative path into the payload name the manifest declares.
    /// <para>把包内相对路径扁平化为清单声明的负载名。</para>
    /// </summary>
    /// <param name="relativePath">Path with <c>/</c> separators, for example <c>x64/usb2ser.sys</c>.
    /// <para>使用 <c>/</c> 分隔的路径，例如 <c>x64/usb2ser.sys</c>。</para></param>
    /// <returns>The flat payload name, for example <c>x64__usb2ser.sys</c>.
    /// <para>扁平负载名，例如 <c>x64__usb2ser.sys</c>。</para></returns>
    internal static string PayloadNameOf(string relativePath)
        => relativePath.Replace("/", DirectorySeparator, StringComparison.Ordinal);

    /// <summary>
    /// Reads the declared files of one bundled package, never throwing.
    /// <para>读取一个随包驱动包声明的文件，绝不抛出。</para>
    /// </summary>
    /// <param name="packageId">The package id, for example <c>rockusb</c>.
    /// <para>包标识，例如 <c>rockusb</c>。</para></param>
    /// <param name="files">The declared files in bundle order, or an empty list when the bundle is absent.
    /// <para>按资源包顺序声明的文件；资源包未随包时为空列表。</para></param>
    /// <returns><see langword="true"/> when the bundle ships at least one file.
    /// <para>资源包随包至少一个文件时为 <see langword="true"/>。</para></returns>
    internal static bool TryReadManifest(string packageId, out IReadOnlyList<DriverPackageFile> files)
    {
        var bundle = Resolve(packageId);
        if (bundle is not { IsPresent: true })
        {
            files = [];
            return false;
        }

        var declared = new List<DriverPackageFile>(bundle.Entries.Count);
        foreach (var entry in bundle.Entries)
        {
            declared.Add(new DriverPackageFile(entry.Path, checked((int)entry.Length), entry.Sha256));
        }

        files = declared;
        return true;
    }

    /// <summary>
    /// Writes one bundled package into <paramref name="destinationDirectory"/>, recreating the package
    /// directory layout.
    /// <para>把一个随包驱动包写入 <paramref name="destinationDirectory"/>，并重建包目录结构。</para>
    /// </summary>
    /// <param name="packageId">The package id.<para>包标识。</para></param>
    /// <param name="destinationDirectory">The directory that receives the payload; it is created when missing.
    /// <para>接收负载的目录；不存在时会被创建。</para></param>
    /// <returns>The materialized package-relative paths in bundle order, for example <c>x64/usb2ser.sys</c>.
    /// <para>按资源包顺序物化出的包内相对路径，例如 <c>x64/usb2ser.sys</c>。</para></returns>
    /// <exception cref="InvalidOperationException">This build does not ship the package.
    /// <para>本次构建未随包该驱动包。</para></exception>
    /// <exception cref="InvalidDataException">A package path is unusable, or a frame, length or digest is invalid.
    /// <para>包内路径不可用，或帧、长度、摘要无效。</para></exception>
    /// <exception cref="IOException">A payload file cannot be written. <para>负载文件无法写入。</para></exception>
    internal static IReadOnlyList<string> Materialize(string packageId, string destinationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        var bundle = RequirePresent(packageId);
        Directory.CreateDirectory(destinationDirectory);
        var root = Path.GetFullPath(destinationDirectory);
        var names = new List<string>(bundle.Entries.Count);
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in bundle.Entries)
        {
            names.Add(WriteEntry(entry, root, written));
        }

        if (NeedsSharedCoinstallers(bundle))
        {
            foreach (var entry in SharedCoinstallerEntries())
            {
                var relativePath = RelativePathOf(entry.Path);
                if (!written.Contains(relativePath))
                {
                    names.Add(WriteEntry(entry, root, written));
                }
            }
        }

        return names;
    }

    /// <summary>
    /// Reads one verified file of a bundled package without writing it to disk.
    /// <para>读取随包驱动包中一个已校验的文件，但不写入磁盘。</para>
    /// </summary>
    /// <param name="packageId">The package id.<para>包标识。</para></param>
    /// <param name="relativePath">The package-relative path, for example <c>cdc-acm.inf</c>.
    /// <para>包内相对路径，例如 <c>cdc-acm.inf</c>。</para></param>
    /// <returns>The verified payload, or <see langword="null"/> when the package declares no such file.
    /// <para>校验通过的负载；包未声明该文件时为 <see langword="null"/>。</para></returns>
    /// <exception cref="InvalidDataException">The frame, its decoded length, or its digest is invalid.
    /// <para>帧、解码长度或摘要无效。</para></exception>
    internal static byte[]? TryReadFile(string packageId, string relativePath)
    {
        var bundle = Resolve(packageId);
        if (bundle is not { IsPresent: true })
        {
            return null;
        }

        var payloadName = PayloadNameOf(relativePath);
        foreach (var entry in bundle.Entries)
        {
            if (string.Equals(entry.Path, payloadName, StringComparison.Ordinal))
            {
                return AssetStore.Read(bundle, payloadName);
            }
        }

        return null;
    }

    /// <summary>
    /// Tests whether a package declares the INF that binds WinUSB and therefore needs the shared amd64
    /// coinstallers. <para>判断一个包是否声明了绑定 WinUSB 的 INF，从而需要共享的 amd64 coinstaller。</para>
    /// </summary>
    /// <param name="bundle">The package bundle. <para>包资源包。</para></param>
    /// <returns><see langword="true"/> when the package carries <c>android_winusb.inf</c>.
    /// <para>包携带 <c>android_winusb.inf</c> 时为 <see langword="true"/>。</para></returns>
    internal static bool NeedsSharedCoinstallers(AssetBundle bundle)
        => bundle.Entries.Any(static entry =>
            string.Equals(entry.Path, WinUsbInfFileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads the entries of the shared amd64 coinstaller bundle, which is empty when this build does not ship
    /// it (every non-Windows target). <para>读取共享 amd64 coinstaller 资源包的条目；本构建未随包该资源（所有
    /// 非 Windows 目标）时为空。</para>
    /// </summary>
    /// <returns>The shared entries in bundle order; possibly empty.
    /// <para>按资源包顺序的共享条目；可能为空。</para></returns>
    internal static IReadOnlyList<AssetEntry> SharedCoinstallerEntries()
    {
        var bundle = AssetCatalog.WinUsbCoinstaller;
        return bundle.Entries;
    }

    /// <summary>
    /// Reads the declared files of the shared amd64 coinstaller bundle, never throwing.
    /// <para>读取共享 amd64 coinstaller 资源包声明的文件，绝不抛出。</para>
    /// </summary>
    /// <returns>The declared files in bundle order, or an empty list when the bundle is absent.
    /// <para>按资源包顺序声明的文件；资源包未随包时为空列表。</para></returns>
    internal static IReadOnlyList<DriverPackageFile> ReadSharedCoinstallers()
    {
        var bundle = AssetCatalog.WinUsbCoinstaller;
        if (!bundle.IsPresent)
        {
            return [];
        }

        var declared = new List<DriverPackageFile>(bundle.Entries.Count);
        foreach (var entry in bundle.Entries)
        {
            declared.Add(new DriverPackageFile(entry.Path, checked((int)entry.Length), entry.Sha256));
        }

        return declared;
    }

    /// <summary>
    /// Writes one verified entry below <paramref name="root"/> and records its relative path.
    /// <para>把一个已校验条目写入 <paramref name="root"/> 之下，并记录其相对路径。</para>
    /// </summary>
    /// <param name="entry">The entry to write. <para>要写入的条目。</para></param>
    /// <param name="root">The materialization root. <para>物化根目录。</para></param>
    /// <param name="written">Relative paths already written; the caller owns the set.
    /// <para>已写入的相对路径；集合由调用方持有。</para></param>
    /// <returns>The package-relative path that was written. <para>已写入的包内相对路径。</para></returns>
    private static string WriteEntry(AssetEntry entry, string root, HashSet<string> written)
    {
        var relativePath = RelativePathOf(entry.Path);
        var target = ResolveTargetPath(root, relativePath);
        var directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var payload = AssetStore.Read(entry);
        try
        {
            File.WriteAllBytes(target, payload);
        }
        finally
        {
            Array.Clear(payload);
        }

        written.Add(relativePath);
        return relativePath;
    }

    private static AssetBundle? Resolve(string packageId) => packageId switch
    {
        "rockusb" => AssetCatalog.RockusbDriver,
        "mediatek-vcom" => AssetCatalog.MediaTekVcomDriver,
        "mediatek-full" => AssetCatalog.MediaTekFullDriver,
        "google-usb" => AssetCatalog.GoogleUsbDriver,
        "rockchip-adb" => AssetCatalog.RockchipAdbDriver,
        "qualcomm-qud" => AssetCatalog.QualcommQudDriver,
        "unisoc-spreadtrum" => AssetCatalog.UnisocSpreadtrumDriver,
        _ => null,
    };

    private static AssetBundle RequirePresent(string packageId)
    {
        var bundle = Resolve(packageId);
        return bundle is { IsPresent: true }
            ? bundle
            : throw new InvalidOperationException(
                $"The driver package '{packageId}' is not shipped in this build as the '{BundlePrefix}{packageId}' bundle. "
                + $"本次构建未以 '{BundlePrefix}{packageId}' 资源包随包驱动包 '{packageId}'。");
    }

    private static string ResolveTargetPath(string root, string relativePath)
    {
        if (relativePath.Length is 0 or > MaximumPayloadNameLength)
        {
            throw new InvalidDataException(
                $"The driver package path '{relativePath}' is outside the accepted length range. "
                + $"驱动包路径 '{relativePath}' 超出可接受的长度范围。");
        }

        var combined = root;
        foreach (var segment in relativePath.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                throw new InvalidDataException(
                    $"The driver package path '{relativePath}' is not a usable relative path. "
                    + $"驱动包路径 '{relativePath}' 不是可用的相对路径。");
            }

            combined = Path.Combine(combined, segment);
        }

        var target = Path.GetFullPath(combined);
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The driver package path '{relativePath}' escapes its materialization directory. "
                + $"驱动包路径 '{relativePath}' 逃逸出其物化目录。");
        }

        return target;
    }
}