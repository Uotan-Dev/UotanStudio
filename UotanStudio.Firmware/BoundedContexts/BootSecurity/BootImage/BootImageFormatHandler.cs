using System.Text;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Formats;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Models;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Packer;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Parser;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Repacking;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage;

/// <summary>
/// Format handler that routes Android boot, vendor boot, Tegra blob and ARM zImage files to their parsers for metadata reading, unpacking and repacking.
/// <para>格式处理器，将 Android boot、vendor boot、Tegra blob 与 ARM zImage 文件分派到各自的解析器，完成元数据读取、解包与重打包。</para>
/// </summary>
public sealed class BootImageFormatHandler : IFormatHandler, IRepackMetadataProvider
{
    void IRepackMetadataProvider.ConfigureRepackMetadata(RepackMetadata metadata, string sourcePath, string outputDirectory)
    {
        if (File.Exists(sourcePath) &&
            ChromeOsTool.DetectResource(ReadPrefix(sourcePath, 4096)) ==
            ChromeOsTool.BootResourceType.RockchipResource)
        {
            metadata.CanPackDirectory = true;
            metadata.Configuration["resource"] = "RSCE";
            metadata.PackingNotes = "Creates a canonical unsigned RSCE container; original OEM fields, checksums and device acceptance are not preserved.";
            return;
        }

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
    private const string ChromeOsMagic = "CHROMEOS";
    private const long MaximumChromeOsProbeBytes = 64L * 1024 * 1024;

    /// <summary>Display name identifying this handler's format family.
    /// <para>标识此处理器格式家族的显示名称。</para></summary>
    /// <value>
    /// The fixed string <c>Android Boot / Vendor Boot Image</c>. <para>固定字符串 <c>Android Boot / Vendor Boot Image</c>。</para>
    /// </value>
    public string FormatName => "Android Boot / Vendor Boot Image";

    /// <summary>File-name extensions this handler claims.
    /// <para>此处理器声明的文件名扩展名。</para></summary>
    /// <value>Read-only list of dotless lowercase extensions: <c>img</c>, <c>boot</c>, <c>vendor_boot</c>.
    /// <para>不含点的小写扩展名只读列表：<c>img</c>、<c>boot</c>、<c>vendor_boot</c>。</para></value>
    public IReadOnlyList<string> FileExtensions => ["img", "boot", "vendor_boot"];

    /// <summary>
    /// Determines whether the file matches this format.
    /// <para>判断文件是否匹配此格式。</para>
    /// </summary>
    /// <param name="filePath">Path to probe; at most the first 64 bytes are read.
    /// <para>要探测的路径；至多读取前 64 字节。</para></param>
    /// <returns>
    /// <see langword="true"/> when the file exists and its header matches the Android boot, vendor boot, Tegra blob or ARM zImage magic; <see langword="false"/> for blank paths, missing files and any I/O error, which is swallowed.
    /// <para>当文件存在且其头部匹配 Android boot、vendor boot、Tegra blob 或 ARM zImage 魔数时为 <see langword="true"/>；对空路径、缺失文件以及任何被吞没的 I/O 错误返回 <see langword="false"/>。</para>
    /// </returns>
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
            var chromeOs = DetectChromeOsFile(filePath).IsChromeOs;
            return read >= 8 && (isAndroid || isVendor || chromeOs ||
                ChromeOsTool.DetectResource(header[..read].ToArray()) != ChromeOsTool.BootResourceType.Unknown ||
                TegraBlobTool.IsTegraBlob(header[..read]) ||
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
    /// <param name="filePath">Boot, vendor boot, Tegra blob or zImage file; the magic selects the parser, and the Tegra and zImage paths read the whole file.
    /// <para>boot、vendor boot、Tegra blob 或 zImage 文件；由魔数选择解析器，Tegra 与 zImage 路径会读取整个文件。</para></param>
    /// <param name="cancellationToken">
    /// Checked before the work starts and bound to the background task; cancelling before the delegate begins leaves the returned task in the canceled state.
    /// <para>在工作开始前检查并绑定到后台任务；若委托尚未开始即取消，返回的任务进入已取消状态。</para>
    /// </param>
    /// <returns>
    /// A task carrying a <see cref="FirmwareOpResult"/>. Success reports a summary, per-format metadata entries and component sizes; an unrecognized magic or any caught exception comes back as a failure result with the message, never as a throw.
    /// <para>返回携带 <see cref="FirmwareOpResult"/> 的任务。成功时给出摘要、各格式的元数据条目与组件大小；无法识别的魔数或任何被捕获的异常都以失败结果返回消息，而不抛出。</para>
    /// </returns>
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
                    var role = BootImageRoleResolver.Detect(h.HeaderVersion, h.KernelSize, h.RamdiskSize,
                        RepackMetadata.DetectPartitionName(Path.GetFileName(filePath)));
                    var summary = $"Boot image v{(int)h.HeaderVersion} parsed.\n" +
                                  $"Page size: {h.PageSize}\nKernel: {h.KernelSize} bytes\n" +
                                  $"Ramdisk: {h.RamdiskSize} bytes\nCmdline: {h.CommandLine}";
                    return FirmwareOpResult.Ok(summary, metadata: new Dictionary<string, string>
                    {
                        ["Type"] = "boot",
                        ["Role"] = role.ToString(),
                        ["HeaderVersion"] = ((int)h.HeaderVersion).ToString(),
                        ["PageSize"] = h.PageSize.ToString(),
                        ["HeaderSize"] = h.HeaderSize.ToString(),
                        ["ProductName"] = h.ProductName,
                        ["KernelSize"] = h.KernelSize.ToString(),
                        ["RamdiskSize"] = h.RamdiskSize.ToString(),
                        ["SecondSize"] = h.SecondSize.ToString(),
                        ["DtbSize"] = h.DtbSize.ToString(),
                        ["OsVersion"] = h.OsVersionString,
                        ["CommandLine"] = h.CommandLine,
                        ["KernelCompression"] = h.KernelCompression.ToString(),
                        ["RamdiskCompression"] = h.RamdiskCompression.ToString(),
                        ["OsVersionRaw"] = $"0x{h.OsVersion:X8}"
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
                        ["VendorCmdline"] = h.VendorCmdline,
                        ["VendorName"] = h.VendorName,
                        ["HeaderSize"] = h.VendorHeaderSize.ToString(),
                        ["KernelLoadAddress"] = $"0x{h.KernelLoadAddress:X8}",
                        ["RamdiskLoadAddress"] = $"0x{h.RamdiskLoadAddress:X8}",
                        ["TagsLoadAddress"] = $"0x{h.TagsLoadAddress:X8}",
                        ["DtbAddress"] = $"0x{h.DtbAddress:X16}"
                    });
                }

                var prefix = ReadPrefix(filePath, 4096);
                var chrome = DetectChromeOsFile(filePath);
                if (chrome.IsChromeOs)
                {
                    var chromeMetadata = new Dictionary<string, string>
                    {
                        ["Type"] = chrome.ImageType.ToString(),
                        ["StructureSize"] = chrome.StructureSize.ToString(),
                        ["SignatureValidation"] = "not-implemented",
                        ["KernelExtraction"] = "not-implemented"
                    };
                    if (chrome.ImageType is ChromeOsTool.ChromeOsImageType.Keyblock
                        or ChromeOsTool.ChromeOsImageType.Vblock
                        or ChromeOsTool.ChromeOsImageType.Kernel)
                    {
                        var data = File.ReadAllBytes(filePath);
                        var keyblock = ChromeOsTool.ParseKeyblock(data);
                        chromeMetadata["KeyblockVersion"] =
                            $"{keyblock.HeaderVersionMajor}.{keyblock.HeaderVersionMinor}";
                        chromeMetadata["KeyblockSize"] = keyblock.KeyblockSize.ToString();
                        chromeMetadata["DataKeyOffset"] = keyblock.DataKey.Offset.ToString();
                        chromeMetadata["DataKeySize"] = keyblock.DataKey.Size.ToString();
                        if (chrome.ImageType is ChromeOsTool.ChromeOsImageType.Vblock
                            or ChromeOsTool.ChromeOsImageType.Kernel)
                        {
                            var vblock = ChromeOsTool.ParseVblock(data);
                            chromeMetadata["PreambleVersion"] =
                                $"{vblock.PreambleVersionMajor}.{vblock.PreambleVersionMinor}";
                            chromeMetadata["PreambleOffset"] = vblock.PreambleOffset.ToString();
                            chromeMetadata["PreambleSize"] = vblock.PreambleSize.ToString();
                            chromeMetadata["BodySize"] = vblock.BodySignature.DataSize.ToString();
                            chromeMetadata["BodyPresent"] =
                                (chrome.ImageType == ChromeOsTool.ChromeOsImageType.Kernel).ToString();
                        }
                    }

                    return FirmwareOpResult.Ok(chrome.Description,
                        metadata: chromeMetadata);
                }

                var resource = ChromeOsTool.DetectResource(prefix);
                if (resource != ChromeOsTool.BootResourceType.Unknown)
                {
                    if (resource == ChromeOsTool.BootResourceType.RockchipResource)
                    {
                        var rsce = ChromeOsTool.ReadRockchipResourceMetadata(filePath);
                        return FirmwareOpResult.Ok(
                            $"RSCE resource container with {rsce.Entries.Count} entries.",
                            entries: [.. rsce.Entries.Select(entry => entry.Name)],
                            metadata: new Dictionary<string, string>
                            {
                                ["Type"] = resource.ToString(),
                                ["FileCount"] = rsce.FileCount.ToString(),
                                ["PayloadParsing"] = "directory-only"
                            });
                    }

                    return FirmwareOpResult.Ok(
                        $"{resource} boot resource signature recognized; payload format is not parsed.",
                        metadata: new Dictionary<string, string>
                        {
                            ["Type"] = resource.ToString(),
                            ["PayloadParsing"] = "not-implemented",
                            ["Extraction"] = "not-implemented"
                        });
                }

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
    /// <param name="filePath">Image file dispatched by magic; boot components, Tegra kernel/ramdisk and the zImage compressed payload are written as separate files and empty components are skipped.
    /// <para>按魔数分派的镜像文件；boot 组件、Tegra 内核/ramdisk 与 zImage 压缩负载会写为独立文件，空组件被跳过。</para></param>
    /// <param name="outputDirectory">Directory created if missing and filled with the extracted files; result entries list the written paths.
    /// <para>缺失时会被创建并写入提取出的文件；结果条目列出已写入的路径。</para></param>
    /// <param name="options">Optional unpack options forwarded through the shared repack pipeline; <see langword="null"/> uses the defaults.
    /// <para>经由共享重打包管道转发的可选解包选项；<see langword="null"/> 使用默认值。</para></param>
    /// <param name="cancellationToken">
    /// Checked inside the background work and bound to the task; cancelling before the delegate begins leaves the returned task in the canceled state.
    /// <para>在后台工作中检查并绑定到任务；若委托尚未开始即取消，返回的任务进入已取消状态。</para>
    /// </param>
    /// <returns>
    /// A task carrying a <see cref="FirmwareOpResult"/> whose success message counts the extracted files; parse or I/O failures are returned as failure results with the exception message instead of throwing.
    /// <para>返回携带 <see cref="FirmwareOpResult"/> 的任务，其成功消息统计已提取文件数；解析或 I/O 失败以携带异常消息的失败结果返回，而不抛出。</para>
    /// </returns>
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

                var resource = ChromeOsTool.DetectResource(ReadPrefix(filePath, 4096));
                if (magic == ChromeOsMagic)
                {
                    return FirmwareOpResult.Fail(
                        "ChromeOS boot/signature extraction is not supported.");
                }

                if (resource == ChromeOsTool.BootResourceType.RockchipResource)
                {
                    var selected = options?.CreateSelectedEntrySet();
                    var count = ChromeOsTool.ExtractRockchipResource(
                        filePath, outputDirectory, selected, options?.Overwrite ?? true, cancellationToken);
                    return FirmwareOpResult.Ok(
                        $"Unpacked RSCE resource container ({count} files) to {outputDirectory}.");
                }

                if (resource != ChromeOsTool.BootResourceType.Unknown)
                {
                    return FirmwareOpResult.Fail(
                        "This startup resource format is recognized but extraction is not supported.");
                }

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
    /// <param name="inputPath">
    /// Directory of extracted components or a boot image file to re-pack. <para>已提取组件的目录，或要重新打包的 boot 镜像文件。</para>
    /// </param>
    /// <param name="outputPath">Destination image path written by the packer. <para>由打包器写入的目标镜像路径。</para></param>
    /// <param name="cancellationToken">
    /// Checked inside the background work and bound to the task; cancelling before the delegate begins leaves the returned task in the canceled state.
    /// <para>在后台工作中检查并绑定到任务；若委托尚未开始即取消，返回的任务进入已取消状态。</para>
    /// </param>
    /// <returns>
    /// A task carrying a <see cref="FirmwareOpResult"/>; this overload packs with the unspecified repack metadata context.
    /// <para>返回携带 <see cref="FirmwareOpResult"/> 的任务；此重载使用未指定的重打包元数据上下文。</para>
    /// </returns>
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
        => PackAsync(inputPath, outputPath, cancellationToken, RepackMetadataContext.Unspecified);

    /// <summary>Packs using a metadata context captured before background work.
    /// <para>后台执行前固定元数据上下文并打包。</para></summary>
    /// <param name="inputPath">Directory rebuilt through the repack configuration, or a single boot image file parsed and re-packed with its original structure.
    /// <para>通过重打包配置重建的目录，或解析后按原结构重新打包的单个 boot 镜像文件。</para></param>
    /// <param name="outputPath">Destination image path; a missing or blank input path is reported as a failure result.
    /// <para>目标镜像路径；输入路径缺失或为空时报告为失败结果。</para></param>
    /// <param name="cancellationToken">
    /// Checked inside the background work and bound to the task; cancelling before the delegate begins leaves the returned task in the canceled state.
    /// <para>在后台工作中检查并绑定到任务；若委托尚未开始即取消，返回的任务进入已取消状态。</para>
    /// </param>
    /// <param name="context">Repack metadata context snapshotted via capture before the work starts, so later UI mutations do not affect this run.
    /// <para>在工作开始前通过捕获固定的重打包元数据上下文，后续界面变化不会影响本次运行。</para></param>
    /// <returns>
    /// A task carrying a <see cref="FirmwareOpResult"/> whose success message names the output path (and header version for re-packed boot images); any caught exception comes back as a failure result with its message instead of throwing.
    /// <para>返回携带 <see cref="FirmwareOpResult"/> 的任务，其成功消息给出输出路径（重新打包的 boot 镜像还包含头部版本）；任何被捕获的异常都以携带其消息的失败结果返回，而不抛出。</para>
    /// </returns>
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
                    var metadata = RepackMetadata.Read(inputPath, context);
                    if (metadata?.Configuration["resource"]?.GetValue<string>() == "RSCE")
                    {
                        var count = ChromeOsTool.BuildRockchipResource(inputPath, outputPath, cancellationToken);
                        return FirmwareOpResult.Ok(
                            $"Packed {count} RSCE resources to {outputPath} (canonical unsigned).");
                    }

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

    private static ChromeOsTool.ChromeOsDetectionResult DetectChromeOsFile(string filePath)
    {
        using var fs = File.OpenRead(filePath);
        if (fs.Length > MaximumChromeOsProbeBytes || fs.Length > int.MaxValue)
        {
            return new ChromeOsTool.ChromeOsDetectionResult
            {
                ImageType = ChromeOsTool.ChromeOsImageType.Unknown,
                Description = "ChromeOS candidate exceeds bounded classifier size.",
                ErrorMessage = "The ChromeOS classifier does not materialize files larger than 64 MiB."
            };
        }

        var data = new byte[checked((int)fs.Length)];
        fs.ReadExactly(data);
        return ChromeOsTool.Detect(data);
    }
}