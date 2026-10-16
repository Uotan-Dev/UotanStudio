using System.Text;
using UotanStudio.Firmware.BootSecurity.BootImage.Components;
using UotanStudio.Firmware.BootSecurity.BootImage.Formats;
using UotanStudio.Firmware.BootSecurity.BootImage.Models;
using UotanStudio.Firmware.BootSecurity.BootImage.Parser;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;

namespace UotanStudio.Firmware.BootSecurity.BootImage;

/// <summary>
/// Format handler that routes Android boot, vendor boot, MTK boot, Samsung PXA, DHTB, Rockchip resource,
/// Tegra blob and ARM zImage files to their parsers for metadata reading, unpacking and repacking.
/// <para>格式处理器，将 Android boot、vendor boot、MTK boot、Samsung PXA、DHTB、Rockchip 资源、
/// Tegra blob 与 ARM zImage 文件分派到各自的解析器，完成元数据读取、解包与重打包。</para>
/// </summary>
public sealed class BootImageFormatHandler : IFormatHandler, IRepackMetadataProvider
{
    void IRepackMetadataProvider.ConfigureRepackMetadata(RepackMetadata metadata, string sourcePath, string outputDirectory)
    {
        if (File.Exists(sourcePath) &&
            RockchipResourceImage.DetectResource(ReadPrefix(sourcePath, 4096)) ==
            RockchipResourceImage.BootResourceType.RockchipResource)
        {
            metadata.CanPackDirectory = true;
            metadata.Configuration["resource"] = "RSCE";
            metadata.PackingNotes = "Creates a canonical unsigned RSCE container; original OEM fields, checksums and device acceptance are not preserved.";
            return;
        }

        if (File.Exists(sourcePath) &&
            BootImageDetector.Detect(ReadPrefix(sourcePath, 4096)).Format
            is not (BootImageDetector.ImageFormat.AospBoot or BootImageDetector.ImageFormat.VendorBoot))
        {
            metadata.CanPackDirectory = false;
            metadata.PackingNotes = " This boot-family variant is extracted only; the repack workflow accepts ANDROID! and VNDRBOOT headers, so the directory cannot be rebuilt into an image yet.";
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
    /// <see langword="true"/> when the file exists and its header matches the Android boot, vendor boot, MTK boot, DHTB, ChromeOS, Rockchip resource, Tegra blob or ARM zImage magic; <see langword="false"/> for blank paths, missing files and any I/O error, which is swallowed.
    /// <para>当文件存在且其头部匹配 Android boot、vendor boot、MTK boot、DHTB、ChromeOS、Rockchip 资源、Tegra blob 或 ARM zImage 魔数时为 <see langword="true"/>；对空路径、缺失文件以及任何被吞没的 I/O 错误返回 <see langword="false"/>。</para>
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
                MtkBootParser.IsMtkFormat(header[..read].ToArray()) ||
                DhtbTool.IsDhtbFormat(header[..read].ToArray()) ||
                RockchipResourceImage.DetectResource(header[..read].ToArray()) != RockchipResourceImage.BootResourceType.Unknown ||
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
                return ReadMetadataCore(filePath);
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail(ex.Message);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Dispatches a boot image file to the metadata reader for its concrete format, ordered by the
    /// authoritative magic values and then by prefix probes for container and kernel layouts.
    /// <para>把启动镜像文件分派到对应格式的元数据读取器：先按权威魔数判定，再按前缀探测容器与内核布局。</para>
    /// </summary>
    private static FirmwareOpResult ReadMetadataCore(string filePath)
    {
        var magic = ReadMagic(filePath);
        if (magic == BootMagic)
        {
            return ReadBootImageMetadata(filePath);
        }

        if (magic == VendorBootMagic)
        {
            return ReadVendorBootMetadata(filePath);
        }

        var prefix = ReadPrefix(filePath, 4096);
        var chrome = DetectChromeOsFile(filePath);
        if (chrome.IsChromeOs)
        {
            return ReadChromeOsMetadata(filePath, chrome);
        }

        var resource = RockchipResourceImage.DetectResource(prefix);
        if (resource != RockchipResourceImage.BootResourceType.Unknown)
        {
            return ReadBootResourceMetadata(filePath, resource);
        }

        if (TegraBlobTool.IsTegraBlob(prefix))
        {
            return ReadTegraBlobMetadata(filePath);
        }

        if (ZImageTool.IsZImage(prefix))
        {
            return ReadZImageMetadata(filePath);
        }

        return FirmwareOpResult.Fail($"Not a boot image (magic: {magic ?? "<none>"}).");
    }

    /// <summary>
    /// Reads Android boot image v0-v4 metadata, including the detected boot role and component sizes.
    /// <para>读取 Android boot 镜像 v0-v4 元数据，含探测出的启动角色与组件大小。</para>
    /// </summary>
    private static FirmwareOpResult ReadBootImageMetadata(string filePath)
    {
        var bootHeader = BootImageParser.ReadHeaderFile(filePath);
        var role = BootImageRoleResolver.Detect(bootHeader.HeaderVersion, bootHeader.KernelSize, bootHeader.RamdiskSize,
            RepackMetadata.DetectPartitionName(Path.GetFileName(filePath)));
        var summary = $"Boot image v{(int)bootHeader.HeaderVersion} parsed.\n" +
                      $"Page size: {bootHeader.PageSize}\nKernel: {bootHeader.KernelSize} bytes\n" +
                      $"Ramdisk: {bootHeader.RamdiskSize} bytes\nCmdline: {bootHeader.CommandLine}";
        return FirmwareOpResult.Ok(summary, metadata: new Dictionary<string, string>
        {
            ["Type"] = "boot",
            ["Role"] = role.ToString(),
            ["HeaderVersion"] = ((int)bootHeader.HeaderVersion).ToString(),
            ["PageSize"] = bootHeader.PageSize.ToString(),
            ["HeaderSize"] = bootHeader.HeaderSize.ToString(),
            ["ProductName"] = bootHeader.ProductName,
            ["KernelSize"] = bootHeader.KernelSize.ToString(),
            ["RamdiskSize"] = bootHeader.RamdiskSize.ToString(),
            ["SecondSize"] = bootHeader.SecondSize.ToString(),
            ["DtbSize"] = bootHeader.DtbSize.ToString(),
            ["OsVersion"] = bootHeader.OsVersionString,
            ["CommandLine"] = bootHeader.CommandLine,
            ["KernelCompression"] = bootHeader.KernelCompression.ToString(),
            ["RamdiskCompression"] = bootHeader.RamdiskCompression.ToString(),
            ["OsVersionRaw"] = $"0x{bootHeader.OsVersion:X8}"
        }, components:
        [
            FirmwareComponent.Of("kernel", bootHeader.KernelSize),
            FirmwareComponent.Of("ramdisk", bootHeader.RamdiskSize),
            FirmwareComponent.Of("second", bootHeader.SecondSize),
            FirmwareComponent.Of("dtb", bootHeader.DtbSize)
        ]);
    }

    /// <summary>
    /// Reads vendor boot image metadata, including the vendor ramdisk table entry count.
    /// <para>读取 vendor boot 镜像元数据，含 vendor ramdisk 表的条目数。</para>
    /// </summary>
    private static FirmwareOpResult ReadVendorBootMetadata(string filePath)
    {
        using var parser = new VendorBootParser();
        parser.ParseFile(filePath);
        var vendorHeader = parser.Header;
        var summary = $"Vendor boot image v{vendorHeader.HeaderVersion} parsed.\n" +
                      $"Vendor ramdisk: {vendorHeader.VendorRamdiskSize} bytes\n" +
                      $"DTB: {vendorHeader.DtbSize} bytes\nRamdisk entries: {parser.RamdiskTable.Count}";
        return FirmwareOpResult.Ok(summary, metadata: new Dictionary<string, string>
        {
            ["Type"] = "vendor_boot",
            ["HeaderVersion"] = vendorHeader.HeaderVersion.ToString(),
            ["PageSize"] = vendorHeader.PageSize.ToString(),
            ["VendorRamdiskSize"] = vendorHeader.VendorRamdiskSize.ToString(),
            ["DtbSize"] = vendorHeader.DtbSize.ToString(),
            ["RamdiskTableEntryCount"] = parser.RamdiskTable.Count.ToString(),
            ["VendorCmdline"] = vendorHeader.VendorCmdline,
            ["VendorName"] = vendorHeader.VendorName,
            ["HeaderSize"] = vendorHeader.VendorHeaderSize.ToString(),
            ["KernelLoadAddress"] = $"0x{vendorHeader.KernelLoadAddress:X8}",
            ["RamdiskLoadAddress"] = $"0x{vendorHeader.RamdiskLoadAddress:X8}",
            ["TagsLoadAddress"] = $"0x{vendorHeader.TagsLoadAddress:X8}",
            ["DtbAddress"] = $"0x{vendorHeader.DtbAddress:X16}"
        });
    }

    /// <summary>
    /// Reads ChromeOS keyblock/vboot metadata; signature validation and kernel extraction stay unimplemented
    /// and are reported as such in the metadata dictionary.
    /// <para>读取 ChromeOS keyblock/vboot 元数据；签名校验与内核提取尚未实现，并在元数据字典中如实标注。</para>
    /// </summary>
    private static FirmwareOpResult ReadChromeOsMetadata(string filePath, ChromeOsTool.ChromeOsDetectionResult chrome)
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

    /// <summary>
    /// Reads boot resource container metadata; only the Rockchip RSCE directory payload is parsed,
    /// other recognized resource signatures report their payload format as unparsed.
    /// <para>读取启动资源容器的元数据；仅解析 Rockchip RSCE 目录负载，其余已识别的资源签名如实
    /// 报告其负载格式未解析。</para>
    /// </summary>
    private static FirmwareOpResult ReadBootResourceMetadata(string filePath, RockchipResourceImage.BootResourceType resource)
    {
        if (resource == RockchipResourceImage.BootResourceType.RockchipResource)
        {
            var rsce = RockchipResourceImage.ReadRockchipResourceMetadata(filePath);
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

    /// <summary>
    /// Reads Tegra blob header metadata with kernel and ramdisk component sizes.
    /// <para>读取 Tegra blob 头元数据，含内核与 ramdisk 组件大小。</para>
    /// </summary>
    private static FirmwareOpResult ReadTegraBlobMetadata(string filePath)
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

    /// <summary>
    /// Reads ARM zImage metadata with the compressed payload location and detected compressor.
    /// <para>读取 ARM zImage 元数据，含压缩负载位置与探测出的压缩器。</para>
    /// </summary>
    private static FirmwareOpResult ReadZImageMetadata(string filePath)
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
        => RepackMetadata.UnpackAsync(this, filePath, outputDirectory, options,
            cancellationToken, () => UnpackCoreAsync(filePath, outputDirectory, options, cancellationToken));

    private Task<FirmwareOpResult> UnpackCoreAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var detection = BootImageDetector.Detect(ReadPrefix(filePath, 4096));
                switch (detection.Format)
                {
                    case BootImageDetector.ImageFormat.MtkBoot:
                        return UnpackMtkBoot(filePath, outputDirectory);
                    case BootImageDetector.ImageFormat.SamsungPxa:
                        return UnpackSamsungBoot(filePath, outputDirectory);
                    case BootImageDetector.ImageFormat.DhtbBoot:
                        return UnpackDhtbBoot(filePath, outputDirectory);
                }

                var magic = ReadMagic(filePath);
                if (magic == BootMagic)
                    return UnpackBoot(filePath, outputDirectory);
                if (magic == VendorBootMagic)
                    return UnpackVendorBoot(filePath, outputDirectory);

                var resource = RockchipResourceImage.DetectResource(ReadPrefix(filePath, 4096));
                if (magic == ChromeOsMagic)
                {
                    return FirmwareOpResult.Fail(
                        "ChromeOS boot/signature extraction is not supported.");
                }

                if (resource == RockchipResourceImage.BootResourceType.RockchipResource)
                {
                    var selected = options?.CreateSelectedEntrySet();
                    var count = RockchipResourceImage.ExtractRockchipResource(
                        filePath, outputDirectory, selected, options?.Overwrite ?? true, cancellationToken);
                    return FirmwareOpResult.Ok(
                        $"Unpacked RSCE resource container ({count} files) to {outputDirectory}.");
                }

                if (resource != RockchipResourceImage.BootResourceType.Unknown)
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
                        var count = RockchipResourceImage.BuildRockchipResource(inputPath, outputPath, cancellationToken);
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
        var header = parser.Header;

        var entries = new List<string>();

        WriteIfNonEmpty(outputDirectory, "kernel", parser.Kernel, entries);
        WriteIfNonEmpty(outputDirectory, "ramdisk", parser.Ramdisk, entries);
        WriteIfNonEmpty(outputDirectory, "second", parser.Second, entries);
        WriteIfNonEmpty(outputDirectory, "dtb", parser.Dtb, entries);
        WriteIfNonEmpty(outputDirectory, "dtbo", parser.RecoveryDtbo, entries);
        WriteIfNonEmpty(outputDirectory, "signature", parser.Signature, entries);

        return FirmwareOpResult.Ok(
            $"Unpacked boot image v{(int)header.HeaderVersion} ({entries.Count} files) to {outputDirectory}",
            entries: entries);
    }

    private static FirmwareOpResult UnpackVendorBoot(string filePath, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        using var parser = new VendorBootParser();
        parser.ParseFile(filePath);
        var header = parser.Header;

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
            $"Unpacked vendor boot image v{header.HeaderVersion} ({entries.Count} files) to {outputDirectory}",
            entries: entries);
    }

    private static FirmwareOpResult UnpackMtkBoot(string filePath, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        using var parser = new MtkBootParser();
        parser.ParseFile(filePath);

        var entries = new List<string>();
        WriteIfNonEmpty(outputDirectory, "kernel", parser.Kernel, entries);
        WriteRamdiskWithCpio(outputDirectory, parser.Ramdisk, entries);
        WriteIfNonEmpty(outputDirectory, "second", parser.Second, entries);
        WriteIfNonEmpty(outputDirectory, "dtb", parser.Dtb, entries);

        return FirmwareOpResult.Ok(
            $"Unpacked MTK boot image ({entries.Count} files) to {outputDirectory}",
            entries: entries);
    }

    private static FirmwareOpResult UnpackSamsungBoot(string filePath, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        using var parser = new SamsungBootParser();
        parser.ParseFile(filePath);

        var entries = new List<string>();
        WriteIfNonEmpty(outputDirectory, "kernel", parser.Kernel, entries);
        WriteRamdiskWithCpio(outputDirectory, parser.Ramdisk, entries);
        WriteIfNonEmpty(outputDirectory, "second", parser.Second, entries);
        WriteIfNonEmpty(outputDirectory, "extra", parser.Extra, entries);

        return FirmwareOpResult.Ok(
            $"Unpacked Samsung PXA boot image ({entries.Count} files) to {outputDirectory}",
            entries: entries);
    }

    private static FirmwareOpResult UnpackDhtbBoot(string filePath, string outputDirectory)
    {
        var parsed = DhtbTool.ParseFile(filePath);
        if (!parsed.IsValid)
        {
            return FirmwareOpResult.Fail(parsed.ErrorMessage ?? "Failed to parse DHTB image.");
        }

        Directory.CreateDirectory(outputDirectory);

        var entries = new List<string>();
        WriteIfNonEmpty(outputDirectory, "kernel", parsed.Kernel, entries);
        WriteRamdiskWithCpio(outputDirectory, parsed.Ramdisk, entries);
        WriteIfNonEmpty(outputDirectory, "second", parsed.Second, entries);

        return FirmwareOpResult.Ok(
            $"Unpacked DHTB boot image ({entries.Count} files) to {outputDirectory}",
            entries: entries);
    }

    private static void WriteRamdiskWithCpio(string directory, byte[] ramdisk, List<string> entries)
    {
        WriteIfNonEmpty(directory, "ramdisk", ramdisk, entries);
        if (ramdisk.Length == 0)
        {
            return;
        }

        var compression = BootCompressor.Detect(ramdisk);
        var cpio = compression == CompressionType.None
            ? ramdisk
            : BootCompressor.TryDecompress(ramdisk, compression) ?? ramdisk;
        WriteIfNonEmpty(directory, "ramdisk.cpio", cpio, entries);
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