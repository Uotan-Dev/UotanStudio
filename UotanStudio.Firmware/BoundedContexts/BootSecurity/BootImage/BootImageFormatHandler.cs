using System.Text;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Formats;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Packer;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Parser;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Repacking;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage;

public sealed class BootImageFormatHandler : IFormatHandler, IRepackMetadataProvider
{
    void IRepackMetadataProvider.ConfigureRepackMetadata(RepackMetadata metadata, string sourcePath, string outputDirectory)
    {
        metadata.Configuration = BootRepackConfiguration.FromImage(sourcePath);
        var hasAvbFooter = metadata.Configuration.ContainsKey("footer");
        metadata.CanPackDirectory = metadata.Configuration.Count > 0 && !hasAvbFooter;
        if (hasAvbFooter)
        {
            metadata.PackingNotes = " The source boot image carries an AVB footer; the directory cannot be rebuilt without dropping verified-boot data, so repacking is disabled until re-signed through an AVB-aware workflow.";
        }
    }

    private const string BootMagic = "ANDROID!";
    private const string VendorBootMagic = "VNDRBOOT";

    public string FormatName => "Android Boot / Vendor Boot Image";

    public IReadOnlyList<string> FileExtensions => ["img", "boot", "vendor_boot"];

    /// <summary>
    /// Determines whether the file matches this format.
    /// <para>判断文件是否匹配此格式。</para>
    /// </summary>
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            using var fs = File.OpenRead(filePath);
            Span<byte> header = stackalloc byte[64];
            var read = fs.Read(header);

            var isAndroid = header[0] == (byte)'A' && header[1] == (byte)'N' && header[2] == (byte)'D'
                && header[3] == (byte)'R' && header[4] == (byte)'O' && header[5] == (byte)'I'
                && header[6] == (byte)'D' && header[7] == (byte)'!';
            var isVendor = header[0] == (byte)'V' && header[1] == (byte)'N' && header[2] == (byte)'D'
                && header[3] == (byte)'R' && header[4] == (byte)'B' && header[5] == (byte)'O'
                && header[6] == (byte)'O' && header[7] == (byte)'T';
            return read >= 8 && (isAndroid || isVendor || TegraBlobTool.IsTegraBlob(header[..read]) ||
                ZImageTool.IsZImage(header[..read]));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the firmware metadata asynchronously.
    /// <para>异步读取固件元数据。</para>
    /// </summary>
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
                    var h = BootImageParser.ReadHeaderFile(filePath);
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

                var prefix = ReadPrefix(filePath, 4096);
                if (TegraBlobTool.IsTegraBlob(prefix))
                {
                    var parsed = TegraBlobTool.Parse(File.ReadAllBytes(filePath));
                    if (!parsed.IsValid)
                        return FirmwareOpResult.Fail(parsed.ErrorMessage ?? "Invalid Tegra blob.");
                    return FirmwareOpResult.Ok($"Tegra blob v{parsed.Header!.Version} parsed.\n" +
                        $"Kernel: {parsed.Header.KernelSize} bytes ({parsed.Header.CompressionType})\n" +
                        $"Ramdisk: {parsed.Header.RamdiskSize} bytes", metadata: new Dictionary<string, string>
                        {
                            ["Type"] = "tegra",
                            ["Version"] = parsed.Header.Version.ToString(),
                            ["KernelSize"] = parsed.Header.KernelSize.ToString(),
                            ["RamdiskSize"] = parsed.Header.RamdiskSize.ToString(),
                            ["CompressionType"] = parsed.Header.CompressionType.ToString()
                        }, components:
                    [
                        FirmwareComponent.Of("kernel", parsed.Header.KernelSize),
                        FirmwareComponent.Of("ramdisk", parsed.Header.RamdiskSize)
                    ]);
                }

                if (ZImageTool.IsZImage(prefix))
                {
                    var parsed = ZImageTool.Parse(File.ReadAllBytes(filePath));
                    if (!parsed.IsValid)
                        return FirmwareOpResult.Fail(parsed.ErrorMessage ?? "Invalid zImage.");
                    return FirmwareOpResult.Ok($"ARM zImage parsed.\nCompressed payload: {parsed.CompressedSize} bytes\n" +
                        $"Compression: {parsed.CompressionType}", metadata: new Dictionary<string, string>
                        {
                            ["Type"] = "zimage",
                            ["CompressedOffset"] = parsed.CompressedOffset.ToString(),
                            ["CompressedSize"] = parsed.CompressedSize.ToString(),
                            ["Compression"] = parsed.CompressionType.ToString()
                        }, components: [FirmwareComponent.Of("kernel", parsed.CompressedSize)]);
                }

                return FirmwareOpResult.Fail($"Not a boot image (magic: {magic ?? "<none>"}).");
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail(ex.Message);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Extracts the firmware contents asynchronously.
    /// <para>异步提取固件内容。</para>
    /// </summary>
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
        => UotanStudio.Firmware.SharedKernel.Repacking.RepackMetadata.UnpackAsync(this, filePath, outputDirectory, options,
            cancellationToken, () => UnpackCoreAsync(filePath, outputDirectory, options, cancellationToken));

    private Task<FirmwareOpResult> UnpackCoreAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var magic = ReadMagic(filePath);
                if (magic == BootMagic)
                    return UnpackBoot(filePath, outputDirectory);
                if (magic == VendorBootMagic)
                    return UnpackVendorBoot(filePath, outputDirectory);

                var data = File.ReadAllBytes(filePath);
                if (TegraBlobTool.IsTegraBlob(data))
                {
                    var parsed = TegraBlobTool.Parse(data);
                    if (!parsed.IsValid)
                        return FirmwareOpResult.Fail(parsed.ErrorMessage ?? "Invalid Tegra blob.");
                    Directory.CreateDirectory(outputDirectory);
                    var entries = new List<string>();
                    WriteIfNonEmpty(outputDirectory, "kernel", parsed.Kernel, entries);
                    WriteIfNonEmpty(outputDirectory, "ramdisk", parsed.Ramdisk, entries);
                    return FirmwareOpResult.Ok($"Unpacked Tegra blob ({entries.Count} files) to {outputDirectory}", entries: entries);
                }

                if (ZImageTool.IsZImage(data))
                {
                    var parsed = ZImageTool.Parse(data);
                    if (!parsed.IsValid)
                        return FirmwareOpResult.Fail(parsed.ErrorMessage ?? "Invalid zImage.");
                    Directory.CreateDirectory(outputDirectory);
                    var kernelPath = Path.Combine(outputDirectory, "kernel");
                    File.WriteAllBytes(kernelPath, parsed.CompressedPayload);
                    return FirmwareOpResult.Ok($"Unpacked ARM zImage to {outputDirectory}", entries: [kernelPath]);
                }

                return FirmwareOpResult.Fail($"Not a boot image (magic: {magic ?? "<none>"}).");
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail(ex.Message);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Packs the input into firmware asynchronously.
    /// <para>异步将输入打包为固件。</para>
    /// </summary>
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
        => PackAsync(inputPath, outputPath, cancellationToken, RepackMetadataContext.Unspecified);

    /// <summary>Packs using a metadata context captured before background work.
    /// <para>后台执行前固定元数据上下文并打包。</para></summary>
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken,
        RepackMetadataContext context)
    {
        context = context.Capture();
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
                    BootRepackConfiguration.Pack(inputPath, outputPath, cancellationToken, context);
                    return FirmwareOpResult.Ok($"Packed boot image to {outputPath}");
                }


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
        WriteIfNonEmpty(outputDirectory, "vendor_ramdisk", parser.VendorRamdisk, entries);
        WriteIfNonEmpty(outputDirectory, "dtb", parser.Dtb, entries);
        WriteIfNonEmpty(outputDirectory, "bootconfig", parser.Bootconfig, entries);


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

    private static byte[] ReadPrefix(string filePath, int maxBytes)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, FileOptions.SequentialScan);
        var length = (int)Math.Min(fs.Length, maxBytes);
        var buffer = new byte[length];
        fs.ReadExactly(buffer);
        return buffer;
    }
}