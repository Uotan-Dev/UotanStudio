using System.Text;
using System.Xml;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Streams;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.MediaTek;

/// <summary>
/// Detection, metadata reading and unpacking entry point for MediaTek firmware descriptions:
/// YAML scatter manifests, legacy XML scatter manifests and all-in-one download-agent containers.
/// DA security images (DA sec) are intentionally out of scope.
/// <para>MediaTek 固件描述的检测、元数据读取与解包入口：YAML scatter 清单、legacy XML scatter 清单，
/// 以及 all-in-one 下载代理容器。DA sec 安全镜像刻意不在范围内。</para>
/// </summary>
public sealed class MtkFormatHandler : IReadonlyFormatHandler
{
    private const long MaximumManifestBytes = 16L * 1024 * 1024;

    /// <summary>Display name of the MediaTek description formats.
    /// <para>MediaTek 描述格式的显示名称。</para></summary>
    /// <value>The constant <c>MediaTek Firmware (Scatter/DA)</c>, also the <see cref="FormatDetector"/> registration key.
    /// <para>常量 <c>MediaTek Firmware (Scatter/DA)</c>，同时作为 <see cref="FormatDetector"/> 的注册键。</para></value>
    public string FormatName => "MediaTek Firmware (Scatter/DA)";

    /// <summary>Extensions this handler claims before content inspection.
    /// <para>在内容检查之前本处理器声明认领的扩展名。</para></summary>
    /// <value><c>scatter</c>, <c>xml</c> and <c>bin</c>; the scatter YAML/XML markers and the
    /// <c>MTK_DOWNLOAD_AGENT</c> magic still decide acceptance. Plain <c>txt</c> is deliberately not
    /// claimed: it is a generic text extension and claiming it would misroute ordinary text documents
    /// away from the text editor.
    /// <para><c>scatter</c>、<c>xml</c> 与 <c>bin</c>；是否接受仍由 scatter YAML/XML 标记与
    /// <c>MTK_DOWNLOAD_AGENT</c> 魔数决定。刻意不认领 <c>txt</c>：它是通用文本扩展名，认领后会
    /// 把普通文本文档错误地路由出文本编辑器。</para></value>
    public IReadOnlyList<string> FileExtensions => ["scatter", "xml", "bin"];

    /// <summary>
    /// Determines whether the file is a MediaTek scatter manifest or download-agent container.
    /// <para>判断文件是 MediaTek scatter 清单还是下载代理容器。</para>
    /// </summary>
    /// <param name="filePath">Candidate file path. <para>候选文件路径。</para></param>
    /// <returns><see langword="true"/> when the content matches one of the supported MediaTek kinds;
    /// probe failures never throw here.
    /// <para>内容命中受支持的 MediaTek 种类之一时为 <see langword="true"/>；探测失败时此方法不抛出异常。</para></returns>
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            return Classify(filePath) != MtkKind.None;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads scatter partitions or download-agent directory metadata, including the chip/platform
    /// declaration when present.
    /// <para>读取 scatter 分区或下载代理目录元数据，存在时包含芯片/平台声明。</para>
    /// </summary>
    /// <param name="filePath">Manifest or container to inspect. <para>待检查的清单或容器。</para></param>
    /// <param name="cancellationToken">Cancels the read. <para>取消本次读取。</para></param>
    /// <returns>Outcome describing the detected kind, chip token and entry list.
    /// <para>结果对象说明检测到的种类、芯片标记与条目列表。</para></returns>
    /// <exception cref="OperationCanceledException">The token was canceled.
    /// <para>令牌被取消。</para></exception>
    public async Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            switch (Classify(filePath))
            {
                case MtkKind.Da:
                    {
                        await using var stream = File.OpenRead(filePath);
                        var container = MtkDaContainerReader.Read(stream, MtkDaDirectoryLayout.Auto, cancellationToken);
                        var codes = container.Entries
                            .Select(entry => $"0x{entry.HardwareCode:X4}/0x{entry.HardwareSubCode:X4}")
                            .ToArray();
                        return FirmwareOpResult.Ok(
                            $"MediaTek DA container ({container.Layout}): {codes.Length} agents",
                            entries: [.. codes],
                            metadata: new Dictionary<string, string>
                            {
                                ["Kind"] = "DA",
                                ["Layout"] = container.Layout.ToString(),
                                ["HeaderIndicatesV6"] = container.HeaderIndicatesV6.ToString(),
                                ["HardwareCodes"] = string.Join(", ", codes)
                            });
                    }

                case MtkKind.ScatterYaml:
                    {
                        await using var stream = File.OpenRead(filePath);
                        var document = await Task.Run(() => MtkScatterReader.Read(stream, cancellationToken), cancellationToken);
                        var partitions = document.Storages.SelectMany(storage => storage.Partitions)
                            .Where(partition => !string.Equals(partition.FileName, "NONE", StringComparison.Ordinal))
                            .Select(partition => $"{partition.Name} {partition.FileName}")
                            .ToArray();
                        return FirmwareOpResult.Ok(
                            $"MediaTek scatter ({document.Platform} / {document.Project}): {partitions.Length} images",
                            entries: partitions,
                            metadata: new Dictionary<string, string>
                            {
                                ["Kind"] = "ScatterYaml",
                                ["Chip"] = document.Platform,
                                ["Project"] = document.Project,
                                ["ConfigVersion"] = document.ConfigVersion
                            });
                    }

                case MtkKind.ScatterXml:
                    {
                        var (chip, project, images) = ReadXmlScatter(filePath, cancellationToken);
                        return FirmwareOpResult.Ok(
                            $"MediaTek XML scatter ({chip}): {images.Count} images",
                            entries: images,
                            metadata: new Dictionary<string, string>
                            {
                                ["Kind"] = "ScatterXml",
                                ["Chip"] = chip,
                                ["Project"] = project
                            });
                    }

                default:
                    return FirmwareOpResult.Fail("The file is not a MediaTek scatter manifest or DA container.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or XmlException or OverflowException)
        {
            return FirmwareOpResult.Fail(exception.Message);
        }
    }

    /// <summary>
    /// Unpacks the description: DA containers release their region payloads, scatter manifests are
    /// copied so image files referenced beside them can be resolved by callers.
    /// <para>解包描述文件：DA 容器释出各区域负载；scatter 清单被复制，供调用方解析其旁边引用的镜像文件。</para>
    /// </summary>
    /// <param name="filePath">Manifest or container to unpack; never modified.
    /// <para>待解包的清单或容器；不会被修改。</para></param>
    /// <param name="outputDirectory">Destination directory, created when absent.
    /// <para>目标目录，不存在时创建。</para></param>
    /// <param name="options">Overwrite policy and progress sink; <see langword="null"/> uses defaults.
    /// <para>覆盖策略与进度回调；传 <see langword="null"/> 时使用默认值。</para></param>
    /// <param name="cancellationToken">Cancels the extraction. <para>取消解包。</para></param>
    /// <returns>Outcome listing the written files with sizes.
    /// <para>结果对象列出已写入的文件及其大小。</para></returns>
    /// <exception cref="OperationCanceledException">The token was canceled.
    /// <para>令牌被取消。</para></exception>
    public async Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory,
        UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new UnpackOptions();
        var written = new List<string>();
        var components = new List<FirmwareComponent>();
        try
        {
            Directory.CreateDirectory(outputDirectory);
            var kind = Classify(filePath);
            switch (kind)
            {
                case MtkKind.Da:
                    {
                        await using var stream = File.OpenRead(filePath);
                        var container = MtkDaContainerReader.Read(stream, MtkDaDirectoryLayout.Auto, cancellationToken);
                        var index = 0;
                        foreach (var entry in container.Entries)
                        {
                            foreach (var region in entry.Regions)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                var name = $"da_{index:X2}_{entry.HardwareCode:X4}_{region.FileOffset:X8}.bin";
                                var destination = Path.Combine(outputDirectory, name);
                                if (!options.Overwrite && File.Exists(destination))
                                {
                                    throw new IOException($"Output already exists: {destination}");
                                }

                                await using (var output = File.Create(destination))
                                using (var range = new BoundedReadStream(stream, region.FileOffset, region.Length, leaveOpen: true))
                                {
                                    StreamCopy.CopyExactly(range, output, region.Length, cancellationToken);
                                }

                                written.Add(name);
                                components.Add(FirmwareComponent.Of(name, region.Length));
                                index++;
                            }
                        }

                        break;
                    }

                case MtkKind.ScatterYaml:
                case MtkKind.ScatterXml:
                    {
                        var name = Path.GetFileName(filePath);
                        if (string.IsNullOrEmpty(name))
                        {
                            name = "scatter.txt";
                        }

                        var destination = Path.Combine(outputDirectory, name);
                        if (!options.Overwrite && File.Exists(destination))
                        {
                            throw new IOException($"Output already exists: {destination}");
                        }

                        await using (var source = File.OpenRead(filePath))
                        await using (var output = File.Create(destination))
                        {
                            StreamCopy.CopyExactly(source, output, source.Length, cancellationToken);
                        }

                        written.Add(name);
                        components.Add(FirmwareComponent.Of(name, new FileInfo(destination).Length));
                        break;
                    }

                default:
                    return FirmwareOpResult.Fail("The file is not a MediaTek scatter manifest or DA container.");
            }

            return FirmwareOpResult.Ok($"Extracted {written.Count} files to {outputDirectory}",
                entries: written, components: components);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or XmlException or OverflowException)
        {
            return FirmwareOpResult.Fail(exception.Message);
        }
    }

    private enum MtkKind
    {
        None,
        ScatterYaml,
        ScatterXml,
        Da
    }

    private static MtkKind Classify(string filePath)
    {
        var information = new FileInfo(filePath);
        if (!information.Exists || information.Length > Math.Max(MaximumManifestBytes, 512L * 1024 * 1024))
        {
            return MtkKind.None;
        }

        using var stream = File.OpenRead(filePath);
        if (stream.Length >= 108)
        {
            Span<byte> daHeader = stackalloc byte[108];
            stream.ReadExactly(daHeader);
            if (daHeader.StartsWith("MTK_DOWNLOAD_AGENT"u8)
                && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(daHeader[100..]) == 0x22668899)
            {
                return MtkKind.Da;
            }
        }

        if (Path.GetExtension(filePath).Equals(".bin", StringComparison.OrdinalIgnoreCase))
        {
            return MtkKind.None;
        }

        var probeLength = (int)Math.Min(stream.Length, 64L * 1024 * 1024);
        var probe = new byte[probeLength];
        stream.Position = 0;
        stream.ReadExactly(probe, 0, probeLength);
        var text = Encoding.UTF8.GetString(probe);
        if (text.Contains("MTK_PLATFORM_CFG", StringComparison.Ordinal))
        {
            return MtkKind.ScatterYaml;
        }

        if (text.Contains("<scatter", StringComparison.OrdinalIgnoreCase))
        {
            return MtkKind.ScatterXml;
        }

        return MtkKind.None;
    }

    private static (string Chip, string Project, List<string> Images) ReadXmlScatter(string filePath,
        CancellationToken cancellationToken)
    {
        var information = new FileInfo(filePath);
        if (information.Length > MaximumManifestBytes)
        {
            throw new InvalidDataException("The XML scatter manifest exceeds the supported size limit.");
        }

        var chip = "";
        var project = "";
        var images = new List<string>();
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumManifestBytes * 2,
            MaxCharactersFromEntities = 0
        };
        using var stream = File.OpenRead(filePath);
        using var reader = XmlReader.Create(stream, settings);
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            switch (reader.LocalName)
            {
                case "platform":
                    chip = reader["platform"] ?? "";
                    project = reader["name"] ?? project;
                    break;
                case "image":
                    {
                        var file = reader["file_name"] ?? "";
                        if (!string.IsNullOrEmpty(file) && !string.Equals(file, "NONE", StringComparison.OrdinalIgnoreCase))
                        {
                            images.Add(file);
                        }

                        break;
                    }
            }
        }

        return (chip, project, images);
    }
}