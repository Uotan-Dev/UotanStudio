using UotanStudio.Firmware.Core;

namespace UotanStudio.Firmware.AndroidMeta.Arsc;

/// <summary>
/// Format handler that detects, describes, unpacks and rebuilds Android resource table files such as <c>resources.arsc</c>.
/// <para>格式处理器：检测、描述并解包/重建 Android 资源表文件，例如 <c>resources.arsc</c>。</para>
/// </summary>
public sealed class ArscFormatHandler : IFormatHandler
{
    /// <summary>Display name of the handled format. <para>所处理格式的显示名称。</para></summary>
    /// <value>The constant label "Android Resource Table (ARSC)" this handler reports as its format identity. <para>该处理器作为格式标识上报的固定标签 "Android Resource Table (ARSC)"。</para></value>
    public string FormatName => "Android Resource Table (ARSC)";

    /// <summary>File extension this format is associated with; detection itself relies on the table chunk type, not the name. <para>该格式关联的文件扩展名；实际检测依赖资源表块类型而非文件名。</para></summary>
    /// <value>A read-only list containing the single extension <c>arsc</c>. <para>仅包含扩展名 <c>arsc</c> 的只读列表。</para></value>
    public IReadOnlyList<string> FileExtensions { get; } = ["arsc"];

    /// <summary>
    /// Determines whether the file matches this format.
    /// <para>判断文件是否匹配此格式。</para>
    /// </summary>
    /// <param name="filePath">
    /// Path of the file to sniff; blank or missing paths are rejected without opening.
    /// <para>要探测的文件路径；空白或不存在的路径直接判定不匹配且不会打开文件。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the file is at least 12 bytes and its leading little-endian chunk type equals the resource-table type 0x0002; read errors yield <see langword="false"/>.
    /// <para>文件不少于 12 字节且起始的小端块类型等于资源表类型 0x0002 时返回 <see langword="true"/>；读取错误返回 <see langword="false"/>。</para>
    /// </returns>
    public bool CanHandle(string filePath)
    {
        return HeaderProbe.ProbeMagic(filePath, 12, 0, [0x02, 0x00]);
    }

    /// <summary>Reads the firmware metadata asynchronously.<para>异步读取固件元数据。</para></summary>
    /// <param name="filePath">Resource table file parsed in full before summarising.
    /// <para>要完整解析后再摘要的资源表文件。</para></param>
    /// <param name="cancellationToken">Cancels before parsing starts; the parse itself is synchronous.
    /// <para>在解析开始前取消；解析本身为同步执行。</para></param>
    /// <returns>A completed task whose result is <c>Ok</c> with the table's description, or <c>Fail</c> prefixed <c>ARSC parse failed:</c> when the file cannot be read or parsed.
    /// <para>已完成的 Task，其结果为 <c>Ok</c> 且携带资源表描述，或在文件无法读取或解析时返回以 <c>ARSC parse failed:</c> 为前缀的 <c>Fail</c>。</para></returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已被触发。</para></exception>
    public Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var table = Load(filePath);
            return Task.FromResult(FirmwareOpResult.Ok(table.Describe()));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail($"ARSC parse failed: {ex.Message}"));
        }
    }

    /// <summary>Extracts the firmware contents asynchronously.<para>异步提取固件内容。</para></summary>
    /// <param name="filePath">Resource table to render as a readable text dump of packages, types and entry values.
    /// <para>要渲染为可读文本转储的资源表，内容包含包、类型与条目值。</para></param>
    /// <param name="outputDirectory">Destination directory, created on demand; the output file is named after the source with a <c>.txt</c> extension, or <c>arsc.txt</c> when the source name is blank.
    /// <para>目标目录，按需创建；输出文件以源文件同名加 <c>.txt</c> 扩展名保存，源文件名为空白时使用 <c>arsc.txt</c>。</para></param>
    /// <param name="options">Extraction options; <see langword="null"/> uses the defaults. Only <c>Overwrite</c> is consulted — an existing output with overwriting disabled fails the operation.
    /// <para>解包选项；<see langword="null"/> 表示使用默认值。此处仅读取 <c>Overwrite</c>——输出已存在且禁用覆盖时操作失败。</para></param>
    /// <param name="cancellationToken">Cancels before parsing and writing; cancellation is rethrown rather than converted to a failed result.
    /// <para>在解析与写入前取消；取消异常会重新抛出，而不是转换为失败结果。</para></param>
    /// <returns>A task whose result is <c>Ok</c> naming the written <c>.txt</c> file after unified repack metadata has been written, or <c>Fail</c> prefixed <c>ARSC unpack failed:</c> on read, parse or output conflicts; the shared wrapper turns an unusable pre-existing repack metadata file into a failure before any extraction runs.
    /// <para>任务结果：成功写入统一重打包元数据后为 <c>Ok</c> 并指明写出的 <c>.txt</c> 文件；读取、解析或输出冲突时返回以 <c>ARSC unpack failed:</c> 为前缀的 <c>Fail</c>；共享包装器会在解包运行前把不可用的既有重打包元数据文件直接判为失败。</para></returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled; extraction errors are reported as failed results instead.
    /// <para><paramref name="cancellationToken"/> 已被触发；提取错误则以失败结果报告。</para></exception>
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
        => Core.Repacking.RepackMetadata.UnpackAsync(this, filePath, outputDirectory, options,
            cancellationToken, () => UnpackCoreAsync(filePath, outputDirectory, options, cancellationToken));

    private Task<FirmwareOpResult> UnpackCoreAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                return Task.FromResult(FirmwareOpResult.Fail("Output directory is empty"));
            }

            var table = Load(filePath);
            var text = FormatTable(table);
            Directory.CreateDirectory(outputDirectory);

            var name = Path.GetFileNameWithoutExtension(filePath);
            var safeName = string.IsNullOrWhiteSpace(name) ? "arsc" : name;
            var outputPath = Path.Combine(outputDirectory, safeName + ".txt");

            var overwrite = options?.Overwrite ?? true;
            if (File.Exists(outputPath) && !overwrite)
            {
                return Task.FromResult(FirmwareOpResult.Fail($"Output exists and overwrite is disabled: {outputPath}"));
            }

            File.WriteAllText(outputPath, text);
            return Task.FromResult(FirmwareOpResult.Ok($"Unpacked ARSC to {outputPath}", [outputPath]));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail($"ARSC unpack failed: {ex.Message}"));
        }
    }

    /// <summary>Packs the input into firmware asynchronously.<para>异步将输入打包为固件。</para></summary>
    /// <param name="inputPath">Binary resource table re-read through the parser before rebuilding; the text dump produced by unpacking is not accepted and yields a failed result.
    /// <para>重建前经解析器重新读取的二进制资源表；解包生成的文本转储不被接受，只会得到失败结果。</para></param>
    /// <param name="outputPath">Destination file; its parent directory is created on demand and any existing output is overwritten in a single write, without the temporary-file commit used elsewhere.
    /// <para>目标文件；按需创建其父目录，已有输出在单次写入中被直接覆盖，不使用别处的临时文件提交流程。</para></param>
    /// <param name="cancellationToken">Cancels before the rebuild starts; writing itself is synchronous.
    /// <para>在重建开始前取消；写入本身为同步执行。</para></param>
    /// <returns>A task whose result is <c>Ok</c> naming the written file, or <c>Fail</c> prefixed <c>ARSC rebuild failed:</c> when the source cannot be parsed, the output path is blank or the write fails.
    /// <para>任务结果为 <c>Ok</c> 并指明写出的文件；源无法解析、输出路径为空白或写入失败时返回以 <c>ARSC rebuild failed:</c> 为前缀的 <c>Fail</c>。</para></returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已被触发。</para></exception>
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
    {


        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                return Task.FromResult(FirmwareOpResult.Fail("Output path is empty"));
            }

            var table = Load(inputPath);
            var bytes = ArscWriter.Write(table);
            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllBytes(outputPath, bytes);
            return Task.FromResult(FirmwareOpResult.Ok($"Rebuilt ARSC written to {outputPath}", [outputPath]));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail($"ARSC rebuild failed: {ex.Message}"));
        }
    }

    private static ResourceTable Load(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ArscParser.Parse(fs);
    }

    private static string FormatTable(ResourceTable table)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(table.Describe()).AppendLine();

        foreach (var pkg in table.Packages)
        {
            sb.AppendLine();
            sb.Append("package ").Append(pkg.Name).Append(" (0x").Append(pkg.Id.ToString("x2")).Append(')').AppendLine();
            var typesById = pkg.Types.GroupBy(t => t.Id);
            foreach (var group in typesById)
            {
                var typeName = group.First().Id switch
                {
                    1 => "attr",
                    2 => "string",
                    3 => "style",
                    4 => "color",
                    5 => "dimen",
                    6 => "drawable",
                    7 => "layout",
                    8 => "anim",
                    9 => "mipmap",
                    10 => "menu",
                    _ => "type" + group.Key
                };
                sb.Append("  type ").Append(group.Key).Append(" (").Append(typeName).Append(')').AppendLine();
                foreach (var type in group)
                {
                    sb.Append("    ").Append(type.ConfigDescription).AppendLine();
                    foreach (var entry in type.EnumerateEntries())
                    {
                        var key = pkg.KeyStrings.GetString(entry.KeyIndex);
                        var value = entry.Value?.FormatValue(table.GlobalPool, pkg.Id) ?? string.Empty;
                        sb.Append("      ").Append(key).Append(" = ").Append(value).AppendLine();
                    }
                }
            }
        }
        return sb.ToString().TrimEnd();
    }
}