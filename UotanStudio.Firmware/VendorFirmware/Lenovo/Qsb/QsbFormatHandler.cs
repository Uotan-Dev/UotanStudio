using System.Xml;
using UotanStudio.Firmware.Core;

namespace UotanStudio.Firmware.VendorFirmware.Lenovo.Qsb;

/// <summary>
/// Detection, metadata reading and chunk-merging unpacking entry point for Lenovo/ZUK QSB
/// rawprogram XML manifests.
/// <para>Lenovo/ZUK QSB rawprogram XML 清单的检测、元数据读取与分块合并解包入口。</para>
/// </summary>
internal sealed class QsbFormatHandler : IReadonlyFormatHandler
{
    /// <summary>Display name of the QSB rawprogram format.
    /// <para>QSB rawprogram 格式的显示名称。</para></summary>
    /// <value>The constant <c>Lenovo QSB Rawprogram</c>, also the <see cref="FormatDetector"/> registration key.
    /// <para>常量 <c>Lenovo QSB Rawprogram</c>，同时作为 <see cref="FormatDetector"/> 的注册键。</para></value>
    public string FormatName => "Lenovo QSB Rawprogram";

    /// <summary>Extensions this handler claims before content inspection.
    /// <para>在内容检查之前本处理器声明认领的扩展名。</para></summary>
    /// <value>The single extension <c>xml</c>; the rawprogram <c>program</c> elements still decide acceptance.
    /// <para>仅有 <c>xml</c> 一个扩展名；是否接受仍由 rawprogram 的 <c>program</c> 元素决定。</para></value>
    public IReadOnlyList<string> FileExtensions => ["xml"];

    /// <summary>
    /// Determines whether the file is a QSB rawprogram XML manifest.
    /// <para>判断文件是否为 QSB rawprogram XML 清单。</para>
    /// </summary>
    /// <param name="filePath">Candidate file path. <para>候选文件路径。</para></param>
    /// <returns><see langword="true"/> when the file parses as rawprogram XML declaring at least one
    /// chunked <c>program</c> element; parse failures never throw here.
    /// <para>文件可解析为 rawprogram XML 且声明了至少一个带 <c>program</c> 元素的分块时为 <see langword="true"/>；解析失败时此方法不抛出异常。</para></returns>
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            return QsbRawprogram.GroupChunks(ParseQuiet(filePath)).Count > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the rawprogram manifest metadata.
    /// <para>读取 rawprogram 清单元数据。</para>
    /// </summary>
    /// <param name="filePath">Manifest to inspect. <para>待检查的清单。</para></param>
    /// <param name="cancellationToken">Cancels the read. <para>取消本次读取。</para></param>
    /// <returns>Outcome listing program entries and the detected chunk groups.
    /// <para>结果对象列出 program 条目与检测到的分块组。</para></returns>
    public async Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = File.OpenRead(filePath);
            var programs = QsbRawprogram.Parse(stream, cancellationToken);
            var groups = QsbRawprogram.GroupChunks(programs);
            if (programs.Count == 0)
            {
                return FirmwareOpResult.Fail("The QSB rawprogram declares no program elements.");
            }

            return FirmwareOpResult.Ok(
                $"QSB rawprogram: {programs.Count} programs, {groups.Count} chunk groups",
                entries: [.. programs.Select(p => $"{p.Label} {p.Filename} @ {p.StartSector}+{p.NumPartitionSectors}")],
                metadata: new Dictionary<string, string>
                {
                    ["ProgramCount"] = programs.Count.ToString(),
                    ["ChunkGroups"] = string.Join(", ", groups.Select(g => $"{g.Prefix}({g.Members.Count})"))
                });
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
    /// Merges every chunk group declared by the manifest into <c>{prefix}.img</c> files.
    /// <para>将清单声明的每个分块组合并为 <c>{prefix}.img</c> 文件。</para>
    /// </summary>
    /// <param name="filePath">Rawprogram manifest; chunk files are resolved beside it.
    /// <para>rawprogram 清单；分块文件在其所在目录解析。</para></param>
    /// <param name="outputDirectory">Destination directory, created when absent.
    /// <para>目标目录，不存在时创建。</para></param>
    /// <param name="options">Output ceiling and overwrite policy; <see langword="null"/> uses defaults.
    /// <para>输出上限与覆盖策略；传 <see langword="null"/> 时使用默认值。</para></param>
    /// <param name="cancellationToken">Cancels the merge. <para>取消本次合并。</para></param>
    /// <returns>Outcome listing the merged images with sizes.
    /// <para>结果对象列出合并后的镜像及其大小。</para></returns>
    /// <exception cref="OperationCanceledException">The token was canceled.
    /// <para>令牌被取消。</para></exception>
    public async Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory,
        UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new UnpackOptions();
        try
        {
            Directory.CreateDirectory(outputDirectory);
            var groups = QsbRawprogram.GroupChunks(ParseQuiet(filePath));
            if (groups.Count == 0)
            {
                return FirmwareOpResult.Fail("The QSB rawprogram declares no chunked images.");
            }

            var selected = options.CreateSelectedEntrySet();
            var written = new List<string>();
            var components = new List<FirmwareComponent>();
            var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? ".";
            foreach (var (prefix, members) in groups)
            {
                var name = prefix + ".img";
                if (selected is not null && !selected.Contains(name))
                {
                    continue;
                }

                if (!options.Overwrite && File.Exists(Path.Combine(outputDirectory, name)))
                {
                    throw new IOException($"Output already exists: {Path.Combine(outputDirectory, name)}");
                }

                var destination = await QsbRawprogram.MergeGroupAsync(baseDirectory, prefix, members,
                    outputDirectory, options.MaximumOutputBytes, cancellationToken);
                written.Add(name);
                components.Add(FirmwareComponent.Of(name, new FileInfo(destination).Length));
            }

            return FirmwareOpResult.Ok($"Merged {written.Count} QSB images into {outputDirectory}",
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

    private static IReadOnlyList<QsbProgram> ParseQuiet(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return QsbRawprogram.Parse(stream);
    }
}