using System.Globalization;
using UotanStudio.Firmware.Core;

namespace UotanStudio.Firmware.VendorFirmware.Allwinner.CardImage;

/// <summary>
/// Detection, metadata reading and unpacking entry point for Allwinner SD-card images: the GPT layout the vendor
/// <c>sys_partition.fex</c> describes for card products, with a pre-partition boot region the vendor boot ROM uses.
/// Unpacking carves every partition into the vendor <c>.fex</c> name so the result matches what the vendor desktop
/// tooling produces, and reports the boot region and <c>boot-resource</c> content as structure rather than files.
/// <para>全志 SD 卡镜像的检测、元数据读取与解包入口：即厂商 <c>sys_partition.fex</c> 为卡类产品描述的 GPT 布局，
/// 并带有一个供厂商引导 ROM 使用的分区前引导区。解包按厂商 <c>.fex</c> 名称切出每个分区，使结果与厂商桌面工具
/// 一致；引导区与 <c>boot-resource</c> 内容则以结构而非文件形式上报。</para>
/// </summary>
public sealed class SunxiCardImageFormatHandler : IReadonlyFormatHandler, IFormatProbeHints
{
    /// <summary>Byte window the structural probe needs: the eGON loader magic at LBA 16 is the furthest marker.
    /// <para>结构探测所需的字节窗口：LBA 16 处的 eGON 引导魔数是最远的标记。</para></summary>
    private const int ProbeWindow = (int)SunxiCardImageFormat.EgonOffset + 12;

    /// <summary>Display name published with every operation result of this handler.
    /// <para>本处理器每次操作结果随附的显示名称。</para>
    /// </summary>
    /// <value>The constant <c>Allwinner SD Card Image</c>, also the <see cref="FormatDetector"/> registration key.
    /// <para>常量 <c>Allwinner SD Card Image</c>，同时作为 <see cref="FormatDetector"/> 的注册键。</para></value>
    public string FormatName => "Allwinner SD Card Image";

    /// <summary>Extensions this handler claims before content inspection.
    /// <para>在内容检查之前本处理器声明认领的扩展名。</para>
    /// </summary>
    /// <value><c>img</c> only; the structural probe still decides acceptance.
    /// <para>仅 <c>img</c>；是否接受仍由结构探测决定。</para></value>
    public IReadOnlyList<string> FileExtensions => ["img"];

    /// <summary>
    /// Confirms a card image from a pre-read header without any file access.
    /// <para>从预先读取的头部确认卡镜像，不访问文件。</para>
    /// </summary>
    /// <param name="header">Leading bytes the caller already read. <para>调用方预先读入的起始字节。</para></param>
    /// <param name="path">Read-from path; unused here, never opened. <para>读取来源路径；此处不使用，绝不打开。</para></param>
    /// <returns><see langword="true"/> when the MBR, GPT and eGON markers are all present.
    /// <para>MBR、GPT 与 eGON 标记全部存在时为 <see langword="true"/>。</para></returns>
    bool IFormatProbeHints.MatchesMagic(ReadOnlySpan<byte> header, string path)
        => SunxiCardImageFormat.MatchesMagic(header);

    /// <summary>
    /// Determines whether the file is an Allwinner SD-card image by checking the three structural markers.
    /// <para>通过检查三个结构标记判断文件是否为全志 SD 卡镜像。</para>
    /// </summary>
    /// <param name="filePath">Candidate image. <para>候选镜像。</para></param>
    /// <returns><see langword="true"/> when the MBR signature, the GPT header at LBA 1 and the <c>eGON.BT0</c>
    /// loader magic at LBA 16 are all present; <see langword="false"/> for blank, missing or unreadable files.
    /// <para>MBR 签名、LBA 1 的 GPT 头部与 LBA 16 的 <c>eGON.BT0</c> 引导魔数全部存在时为 <see langword="true"/>；
    /// 空白、缺失或不可读文件为 <see langword="false"/>。</para></returns>
    public bool CanHandle(string filePath)
    {
        return HeaderProbe.ProbeMagic(filePath, ProbeWindow, 510, (ReadOnlySpan<byte>)[0x55, 0xAA])
            && HeaderProbe.ProbeMagic(filePath, ProbeWindow, SunxiCardImageFormat.BlockSize, "EFI PART"u8)
            && HeaderProbe.ProbeMagic(filePath, ProbeWindow, (int)SunxiCardImageFormat.EgonOffset + 4,
                SunxiEgonHeader.Magic);
    }

    /// <summary>
    /// Reads the card image structure without carving any payload.
    /// <para>读取卡镜像结构，不切出任何负载。</para>
    /// </summary>
    /// <param name="filePath">Image to inspect. <para>待检查的镜像。</para></param>
    /// <param name="cancellationToken">Cancels the read. <para>取消本次读取。</para></param>
    /// <returns>Outcome carrying the partition table, the boot-region components, the U-Boot environment and the
    /// <c>boot-resource</c> file list. <para>结果对象携带分区表、引导区组件、U-Boot 环境与 <c>boot-resource</c> 文件清单。</para></returns>
    public async Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = File.OpenRead(filePath);
            var image = SunxiCardImageFormat.Read(stream, cancellationToken);
            return FirmwareOpResult.Ok(
                $"Allwinner card image: {image.Partitions.Count} partitions, {image.BlockCount} x {image.BlockSize}B blocks.",
                entries: [.. image.Partitions.Select(DescribePartition)
                    .Concat(image.BootRegion.BootPackage?.Items.Select(DescribePackageItem)
                        ?? Enumerable.Empty<string>())
                    .Concat(image.Environments.Keys.Select(name => $"environment:{name}"))
                    .Concat(image.BootResourceFiles.Select(DescribeBootResourceFile))],
                metadata: BuildMetadata(image),
                components: [.. image.Partitions.Select(partition =>
                    FirmwareComponent.Of(partition.ExportName, partition.ContentLength))]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException
            or OverflowException or NotSupportedException)
        {
            return FirmwareOpResult.Fail(exception.Message);
        }
    }

    /// <summary>Carves every selected partition into the output directory under its vendor <c>.fex</c> name, trimmed
    /// to the significant payload length.
    /// <para>把每个选中的分区以其厂商 <c>.fex</c> 名称切到输出目录，并截断到有效负载长度。</para></summary>
    /// <param name="filePath">Image to unpack; never modified. <para>待解包的镜像；不会被修改。</para></param>
    /// <param name="outputDirectory">Destination directory, created when absent. <para>目标目录，不存在时创建。</para></param>
    /// <param name="options">Entry selection and overwrite policy; selection compares partition names
    /// case-insensitively. <see langword="null"/> extracts everything. <see cref="UnpackOptions.MetadataOnly"/>
    /// is rejected because this handler declares no support for it.
    /// <para>条目选择与覆盖策略；选择按分区名忽略大小写比较。传 <see langword="null"/> 时全部解出。
    /// <see cref="UnpackOptions.MetadataOnly"/> 会被拒绝，因为本处理器未声明支持该模式。</para></param>
    /// <param name="cancellationToken">Cancels extraction. <para>取消解包。</para></param>
    /// <returns>Outcome listing the written files with sizes. <para>结果对象列出已写入的文件及其大小。</para></returns>
    /// <exception cref="OperationCanceledException">The token was canceled. <para>令牌被取消。</para></exception>
    public async Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory,
        UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new UnpackOptions();
        if (options.MetadataOnly)
        {
            return FirmwareOpResult.Fail(
                "The Allwinner card image handler does not support metadata-only unpacking.");
        }

        var selected = options.CreateSelectedEntrySet();
        var written = new List<string>();
        var components = new List<FirmwareComponent>();
        try
        {
            Directory.CreateDirectory(outputDirectory);
            await using var stream = File.OpenRead(filePath);
            var image = SunxiCardImageFormat.Read(stream, cancellationToken);
            foreach (var partition in image.Partitions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (partition.ContentLength <= 0
                    || (selected is not null && !selected.Contains(partition.Name)))
                {
                    continue;
                }

                var destination = FileExtraction.ResolvePath(outputDirectory, partition.ExportName);
                if (!options.Overwrite && File.Exists(destination))
                {
                    throw new IOException($"Output already exists: {destination}");
                }

                await FileExtraction.WriteAtomicAsync(destination, output => CopyPartitionAsync(stream, partition,
                    output, cancellationToken), options.Overwrite, cancellationToken).ConfigureAwait(false);
                written.Add(partition.ExportName);
                components.Add(FirmwareComponent.Of(partition.ExportName, partition.ContentLength));
                options.Progress?.Report(written.Count * 100.0 / image.Partitions.Count, partition.ExportName);
            }

            return FirmwareOpResult.Ok($"Extracted {written.Count} partitions to {outputDirectory}",
                entries: written, components: components);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException
            or OverflowException or NotSupportedException)
        {
            return FirmwareOpResult.Fail(exception.Message);
        }
    }

    private static async Task CopyPartitionAsync(Stream source, SunxiCardPartition partition, Stream output,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var remaining = partition.ContentLength;
        source.Position = partition.ByteOffset;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wanted = (int)Math.Min(buffer.Length, remaining);
            await source.ReadExactlyAsync(buffer.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);
            await output.WriteAsync(buffer.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);
            remaining -= wanted;
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string DescribePartition(SunxiCardPartition partition) => string.Create(CultureInfo.InvariantCulture,
        $"{partition.Name} LBA {partition.FirstLba}-{partition.LastLba} ({partition.ContentLength} B of {partition.ByteLength} B) {partition.Payload}");

    private static string DescribeBootResourceFile(SunxiBootResourceFile file) => string.Create(CultureInfo.InvariantCulture,
        $"boot-resource:{(file.LongName.Length > 0 ? file.LongName : file.ShortName)} {file.SizeBytes} B at cluster {file.FirstCluster}");

    private static string DescribePackageItem(SunxiPackageItem item) => string.Create(CultureInfo.InvariantCulture,
        $"boot-package:{item.Name} offset=0x{item.Offset:X} length=0x{item.Length:X} run=0x{item.RunningAddress:X} {item.Content}");

    private static IReadOnlyDictionary<string, string> BuildMetadata(SunxiCardImage image)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["BlockSize"] = image.BlockSize.ToString(CultureInfo.InvariantCulture),
            ["BlockCount"] = image.BlockCount.ToString(CultureInfo.InvariantCulture),
            ["DiskId"] = image.DiskId.ToString(),
            ["FirstUsableLba"] = image.FirstUsableLba.ToString(CultureInfo.InvariantCulture),
            ["LastUsableLba"] = image.LastUsableLba.ToString(CultureInfo.InvariantCulture),
            ["PartitionCount"] = image.Partitions.Count.ToString(CultureInfo.InvariantCulture),
            ["Boot0Offset"] = $"0x{image.BootRegion.Boot0Offset:X}"
        };

        if (image.BootRegion.Boot0 is { } egon)
        {
            metadata["Boot0ChecksumWord"] = $"0x{egon.ChecksumWord:X8}";
            metadata["Boot0Version"] = $"0x{egon.Version:X}";
            metadata["Boot0Platform"] = egon.PlatformText.Length > 0
                ? egon.PlatformText
                : $"0x{egon.Platform:X8}";
            metadata["Boot0VersionText"] = egon.VersionText;
            metadata["Boot0DeviceTreeWord"] = $"0x{egon.DeviceTreeWord:X}";
        }

        if (image.BootRegion.BootPackage is { } package)
        {
            metadata["BootPackageOffset"] = $"0x{image.BootRegion.BootPackageOffset!.Value:X}";
            metadata["BootPackageItems"] = package.ItemCount.ToString(CultureInfo.InvariantCulture);
            metadata["BootPackageLength"] = $"0x{package.TotalLength:X}";
            foreach (var item in package.Items)
            {
                metadata[$"BootPackageItem:{item.Name}"] =
                    $"offset=0x{item.Offset:X} length=0x{item.Length:X} run=0x{item.RunningAddress:X} type={item.Type} content={item.Content}";
            }
        }

        if (image.BootImage is { } boot)
        {
            metadata["BootImagePartition"] = boot.PartitionName;
            metadata["BootImagePageSize"] = boot.PageSize.ToString(CultureInfo.InvariantCulture);
            metadata["BootImageKernelSize"] = $"0x{boot.KernelSize:X}";
            metadata["BootImageRamdiskSize"] = $"0x{boot.RamdiskSize:X}";
            metadata["BootImageSecondSize"] = $"0x{boot.SecondSize:X}";
            metadata["BootImageDeviceTreeSize"] = $"0x{boot.DeviceTreeSize:X}";
            metadata["BootImageKernelAddress"] = $"0x{boot.KernelAddress:X8}";
            metadata["BootImageRamdiskAddress"] = $"0x{boot.RamdiskAddress:X8}";
            metadata["BootImageTagsAddress"] = $"0x{boot.TagsAddress:X8}";
            metadata["BootImageName"] = boot.Name;
            metadata["BootImageKernelContent"] = boot.KernelContent.ToString();
            if (boot.CommandLine.Length > 0)
            {
                metadata["BootImageCommandLine"] = boot.CommandLine;
            }
        }

        foreach (var (name, environment) in image.Environments)
        {
            metadata[$"Env:{name}:Flags"] = $"0x{environment.Flags:X2}";
            metadata[$"Env:{name}:CrcMatches"] = environment.CrcMatches ? "true" : "false";
            metadata[$"Env:{name}:VariableCount"] = environment.Variables.Count.ToString(CultureInfo.InvariantCulture);
            foreach (var variable in new[] { "bootcmd", "bootdelay", "root_partition", "boot_partition", "mmc_root" })
            {
                if (environment.Variables.TryGetValue(variable, out var value))
                {
                    metadata[$"Env:{name}:{variable}"] = value;
                }
            }
        }

        foreach (var file in image.BootResourceFiles)
        {
            var name = file.LongName.Length > 0 ? file.LongName : file.ShortName;
            metadata[$"BootResource:{name}"] = string.Create(CultureInfo.InvariantCulture,
                $"cluster={file.FirstCluster} size={file.SizeBytes} offset=0x{file.Offset:X} attribute=0x{file.Attributes:X2}");
        }

        return metadata;
    }
}