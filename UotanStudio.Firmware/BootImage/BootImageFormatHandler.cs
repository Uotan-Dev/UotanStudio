using System.Text;
using UotanStudio.Firmware.Common;

namespace UotanStudio.Firmware.BootImage;

/// <summary>
/// IFormatHandler 实现：Android Boot / Vendor Boot 镜像。
/// <para>IFormatHandler implementation for Android Boot / Vendor Boot images.</para>
/// </summary>
public sealed class BootImageFormatHandler : IFormatHandler
{
    private const string BootMagic = "ANDROID!";
    private const string VendorBootMagic = "VNDRBOOT";

    /// <inheritdoc />
    public string FormatName => "Android Boot / Vendor Boot Image";

    /// <inheritdoc />
    public IReadOnlyList<string> FileExtensions => ["img", "boot", "vendor_boot"];

    /// <inheritdoc />
    /// <remarks>
    /// 依据文件头部魔数（ANDROID! / VNDRBOOT）识别，不依赖扩展名。
    /// <para>Detects by the leading magic bytes (ANDROID! / VNDRBOOT), independent of the extension.</para>
    /// </remarks>
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            using var fs = File.OpenRead(filePath);
            Span<byte> header = stackalloc byte[8];
            var read = fs.Read(header);

            var isAndroid = header[0] == (byte)'A' && header[1] == (byte)'N' && header[2] == (byte)'D'
                && header[3] == (byte)'R' && header[4] == (byte)'O' && header[5] == (byte)'I'
                && header[6] == (byte)'D' && header[7] == (byte)'!';
            var isVendor = header[0] == (byte)'V' && header[1] == (byte)'N' && header[2] == (byte)'D'
                && header[3] == (byte)'R' && header[4] == (byte)'B' && header[5] == (byte)'O'
                && header[6] == (byte)'O' && header[7] == (byte)'T';
            return read >= 8 && (isAndroid || isVendor);
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var magic = ReadMagic(filePath);
                if (magic == BootMagic)
                {
                    using var parser = new BootImageParser();
                    parser.ParseFile(filePath);
                    var h = parser.Header;
                    var summary = $"Boot image v{(int)h.HeaderVersion} parsed.\n" +
                                  $"Page size: {h.PageSize}\nKernel: {h.KernelSize} bytes\n" +
                                  $"Ramdisk: {h.RamdiskSize} bytes\nCmdline: {h.CommandLine}";
                    return FirmwareOpResult.Ok(summary, metadata: new Dictionary<string, string>
                    {
                        ["Type"] = "boot",
                        ["HeaderVersion"] = ((int)h.HeaderVersion).ToString(),
                        ["PageSize"] = h.PageSize.ToString(),
                        ["KernelSize"] = h.KernelSize.ToString(),
                        ["RamdiskSize"] = h.RamdiskSize.ToString(),
                        ["SecondSize"] = h.SecondSize.ToString(),
                        ["DtbSize"] = h.DtbSize.ToString(),
                        ["OsVersion"] = h.OsVersionString,
                        ["CommandLine"] = h.CommandLine,
                        ["KernelCompression"] = h.KernelCompression.ToString()
                    }, components:
                    [
                        FirmwareComponent.Of("kernel", h.KernelSize),
                        FirmwareComponent.Of("ramdisk", h.RamdiskSize),
                        FirmwareComponent.Of("second", h.SecondSize),
                        FirmwareComponent.Of("dtb", h.DtbSize)
                    ]);
                }

                if (magic == VendorBootMagic)
                {
                    using var parser = new VendorBootParser();
                    parser.ParseFile(filePath);
                    var h = parser.Header;
                    var summary = $"Vendor boot image v{h.HeaderVersion} parsed.\n" +
                                  $"Vendor ramdisk: {h.VendorRamdiskSize} bytes\n" +
                                  $"DTB: {h.DtbSize} bytes\nRamdisk entries: {parser.RamdiskTable.Count}";
                    return FirmwareOpResult.Ok(summary, metadata: new Dictionary<string, string>
                    {
                        ["Type"] = "vendor_boot",
                        ["HeaderVersion"] = h.HeaderVersion.ToString(),
                        ["PageSize"] = h.PageSize.ToString(),
                        ["VendorRamdiskSize"] = h.VendorRamdiskSize.ToString(),
                        ["DtbSize"] = h.DtbSize.ToString(),
                        ["RamdiskTableEntryCount"] = parser.RamdiskTable.Count.ToString(),
                        ["VendorCmdline"] = h.VendorCmdline
                    });
                }

                return FirmwareOpResult.Fail($"Not a boot image (magic: {magic ?? "<none>"}).");
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail(ex.Message);
            }
        }, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// ANDROID!：解包出 kernel / ramdisk / second / dtb / dtbo / signature / header；
    /// VNDRBOOT：解包出 vendor_ramdisk / dtb / bootconfig / header，以及 V4 的 ramdisk 表。
    /// <para>ANDROID!: unpacks kernel / ramdisk / second / dtb / dtbo / signature / header;
    /// VNDRBOOT: unpacks vendor_ramdisk / dtb / bootconfig / header plus the V4 ramdisk table.</para>
    /// </remarks>
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var magic = ReadMagic(filePath);
                return magic == BootMagic
                    ? UnpackBoot(filePath, outputDirectory)
                    : magic == VendorBootMagic
                    ? UnpackVendorBoot(filePath, outputDirectory)
                    : FirmwareOpResult.Fail($"Not a boot image (magic: {magic ?? "<none>"}).");
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail(ex.Message);
            }
        }, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 输入可以是解包目录（含 header + kernel/ramdisk/second/dtb/dtbo/signature 等文件），
    /// 也可以是现成的 boot 镜像文件（解析后按原头重打包）。仅支持 ANDROID! 镜像重打包。
    /// <para>Input may be an unpacked directory (with header + kernel/ramdisk/second/dtb/dtbo/signature)
    /// or an existing boot image file (reparsed and repacked with its original header). Only ANDROID!
    /// images can be repacked.</para>
    /// </remarks>
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (string.IsNullOrWhiteSpace(inputPath) || (!File.Exists(inputPath) && !Directory.Exists(inputPath)))
                {
                    return FirmwareOpResult.Fail("Input path does not exist.");
                }

                if (Directory.Exists(inputPath))
                {
                    return PackFromDirectory(inputPath, outputPath);
                }

                // 输入是现成镜像：解析后原样重打包
                using var parser = new BootImageParser();
                parser.ParseFile(inputPath);
                var packer = BootImagePacker.FromParser(parser);
                packer.PackToFile(outputPath);
                return FirmwareOpResult.Ok(
                    $"Repacked boot image v{(int)parser.Header.HeaderVersion} to {outputPath}");
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail(ex.Message);
            }
        }, cancellationToken);
    }

    private static FirmwareOpResult UnpackBoot(string filePath, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        using var parser = new BootImageParser();
        parser.ParseFile(filePath);
        var h = parser.Header;

        var entries = new List<string>();
        WriteIfNonEmpty(outputDirectory, "header", Encoding.UTF8.GetBytes(h.ToHeaderFile()), entries);

        WriteIfNonEmpty(outputDirectory, "kernel", parser.Kernel, entries);
        WriteIfNonEmpty(outputDirectory, "ramdisk", parser.Ramdisk, entries);
        WriteIfNonEmpty(outputDirectory, "second", parser.Second, entries);
        WriteIfNonEmpty(outputDirectory, "dtb", parser.Dtb, entries);
        WriteIfNonEmpty(outputDirectory, "dtbo", parser.RecoveryDtbo, entries);
        WriteIfNonEmpty(outputDirectory, "signature", parser.Signature, entries);

        return FirmwareOpResult.Ok(
            $"Unpacked boot image v{(int)h.HeaderVersion} ({entries.Count} files) to {outputDirectory}",
            entries: entries);
    }

    private static FirmwareOpResult UnpackVendorBoot(string filePath, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        using var parser = new VendorBootParser();
        parser.ParseFile(filePath);
        var h = parser.Header;

        var entries = new List<string>();
        WriteIfNonEmpty(outputDirectory, "header", Encoding.UTF8.GetBytes(h.ToString()), entries);
        WriteIfNonEmpty(outputDirectory, "vendor_ramdisk", parser.VendorRamdisk, entries);
        WriteIfNonEmpty(outputDirectory, "dtb", parser.Dtb, entries);
        WriteIfNonEmpty(outputDirectory, "bootconfig", parser.Bootconfig, entries);

        // V4：按 ramdisk 表提取每个命名 ramdisk
        if (parser.RamdiskTable.Count > 0 && parser.VendorRamdisk.Length > 0)
        {
            var tableDir = Path.Combine(outputDirectory, "vendor_ramdisks");
            Directory.CreateDirectory(tableDir);

            for (var i = 0; i < parser.RamdiskTable.Count; i++)
            {
                var entry = parser.RamdiskTable[i];
                if (entry.RamdiskSize == 0)
                {
                    continue;
                }

                long start = entry.RamdiskOffset;
                var end = start + entry.RamdiskSize;
                if (start < 0 || end > parser.VendorRamdisk.Length)
                {
                    continue;
                }

                var name = string.IsNullOrWhiteSpace(entry.RamdiskName)
                    ? $"ramdisk_{i}"
                    : SanitizeFileName(entry.RamdiskName);
                var outPath = Path.Combine(tableDir, name);
                File.WriteAllBytes(outPath, parser.VendorRamdisk.AsSpan((int)start, (int)entry.RamdiskSize).ToArray());
                entries.Add(outPath);
            }
        }

        return FirmwareOpResult.Ok(
            $"Unpacked vendor boot image v{h.HeaderVersion} ({entries.Count} files) to {outputDirectory}",
            entries: entries);
    }

    private static FirmwareOpResult PackFromDirectory(string inputDirectory, string outputPath)
    {
        var headerPath = Path.Combine(inputDirectory, "header");
        if (!File.Exists(headerPath))
        {
            return FirmwareOpResult.Fail("Directory does not contain a 'header' file.");
        }

        var header = BootImageHeader.ParseHeaderFile(File.ReadAllText(headerPath));
        if (header == null)
        {
            return FirmwareOpResult.Fail("Failed to parse the 'header' file.");
        }

        var packer = new BootImagePacker
        {
            Header = header,
            Kernel = ReadComponent(inputDirectory, "kernel"),
            Ramdisk = ReadComponent(inputDirectory, "ramdisk"),
            Second = ReadComponent(inputDirectory, "second"),
            Dtb = ReadComponent(inputDirectory, "dtb"),
            RecoveryDtbo = ReadComponent(inputDirectory, "dtbo"),
            Signature = ReadComponent(inputDirectory, "signature")
        };

        packer.PackToFile(outputPath);
        return FirmwareOpResult.Ok($"Packed boot image v{(int)header.HeaderVersion} to {outputPath}");
    }

    private static byte[] ReadComponent(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        return File.Exists(path) ? File.ReadAllBytes(path) : [];
    }

    private static void WriteIfNonEmpty(string directory, string name, byte[] data, List<string> entries)
    {
        if (data == null || data.Length == 0)
        {
            return;
        }

        var outPath = Path.Combine(directory, name);
        File.WriteAllBytes(outPath, data);
        entries.Add(outPath);
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return name;
    }

    private static string? ReadMagic(string filePath)
    {
        using var fs = File.OpenRead(filePath);
        var buffer = new byte[8];
        var read = 0;
        while (read < 8)
        {
            var n = fs.Read(buffer, read, 8 - read);
            if (n <= 0)
            {
                break;
            }

            read += n;
        }
        return read >= 8 ? Encoding.ASCII.GetString(buffer) : null;
    }
}