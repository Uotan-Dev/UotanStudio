using System.Text;
using UotanStudio.Firmware.AndroidMeta.IO;
using UotanStudio.Firmware.Core;

namespace UotanStudio.Firmware.AndroidMeta.AXml;

/// <summary>
/// Format handler that detects, describes, unpacks and rebuilds Android binary XML files such as <c>AndroidManifest.xml</c> inside resource packages.
/// <para>格式处理器：检测、描述并解包/重建 Android 二进制 XML 文件，例如资源包内的 <c>AndroidManifest.xml</c>。</para>
/// </summary>
public sealed class AXmlFormatHandler : IFormatHandler
{
    /// <summary>Display name of the handled format. <para>所处理格式的显示名称。</para></summary>
    /// <value>The constant label "Android Binary XML (AXML)" this handler reports as its format identity. <para>该处理器作为格式标识上报的固定标签 "Android Binary XML (AXML)"。</para></value>
    public string FormatName => "Android Binary XML (AXML)";

    /// <summary>File extension this format is associated with; detection itself relies on the chunk header, not the name. <para>该格式关联的文件扩展名；实际检测依赖块头而非文件名。</para></summary>
    /// <value>A read-only list containing the single extension <c>xml</c>. <para>仅包含扩展名 <c>xml</c> 的只读列表。</para></value>
    public IReadOnlyList<string> FileExtensions { get; } = ["xml"];

    /// <summary>
    /// Determines whether the file matches this format.
    /// <para>判断文件是否匹配此格式。</para>
    /// </summary>
    /// <param name="filePath">
    /// Path of the file to sniff; blank or missing paths are rejected without opening.
    /// <para>要探测的文件路径；空白或不存在的路径直接判定不匹配且不会打开文件。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the first 8 bytes carry the AXML root chunk header (type 0x0003, header size 8); read errors yield <see langword="false"/>.
    /// <para>文件前 8 字节带有 AXML 根块头（类型 0x0003、头长度 8）时为 <see langword="true"/>；读取错误返回 <see langword="false"/>。</para>
    /// </returns>
    public bool CanHandle(string filePath)
    {
        return HeaderProbe.ProbeMagic(filePath, 8, 0, [0x03, 0x00])
               && HeaderProbe.ProbeMagic(filePath, 8, 2, [0x08, 0x00]);
    }

    /// <summary>Reads the firmware metadata asynchronously.<para>异步读取固件元数据。</para></summary>
    /// <param name="filePath">Path of the binary XML file to parse in full before summarising.
    /// <para>要完整解析后再摘要的二进制 XML 文件路径。</para></param>
    /// <param name="cancellationToken">Cancels before parsing starts; the parse itself is synchronous.
    /// <para>在解析开始前取消；解析本身为同步执行。</para></param>
    /// <returns>A task whose result is <c>Ok</c> with a one-line description, or <c>Fail</c> when parsing or reading fails.
    /// <para>任务结果：成功为 <c>Ok</c> 并携带单行描述，读取或解析失败为 <c>Fail</c>。</para></returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 已被触发。</para></exception>
    public Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var doc = Load(filePath);
            return Task.FromResult(FirmwareOpResult.Ok(Describe(doc)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail($"AXML parse failed: {ex.Message}"));
        }
    }

    /// <summary>Extracts the firmware contents asynchronously.<para>异步提取固件内容。</para></summary>
    /// <param name="filePath">Binary XML source file that is parsed and written out as decoded XML text.
    /// <para>将被解析并以解码后的 XML 文本写出的二进制 XML 源文件。</para></param>
    /// <param name="outputDirectory">Destination directory, created on demand; output reuses the source name with an XML extension.
    /// <para>目标目录，按需创建；输出沿用源文件名并改用 XML 扩展名。</para></param>
    /// <param name="options">Extraction options; only <c>Overwrite</c> is consulted and <see langword="null"/> uses its defaults.
    /// <para>解包选项；仅读取 <c>Overwrite</c>，为 <see langword="null"/> 时使用默认值。</para></param>
    /// <param name="cancellationToken">Cancels before parsing and during the output write; cancellation is rethrown.
    /// <para>在解析前与写入输出期间取消；取消异常会重新抛出。</para></param>
    /// <returns>A task naming the written XML file, or reporting a failure prefixed <c>AXML unpack failed:</c>.
    /// <para>任务指明写出的 XML 文件，或以 <c>AXML unpack failed:</c> 前缀报告失败。</para></returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled; extraction errors are reported as failed results instead.
    /// <para><paramref name="cancellationToken"/> 已被触发；提取错误则以失败结果报告。</para></exception>
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
        => Core.Repacking.RepackMetadata.UnpackAsync(this, filePath, outputDirectory, options,
            cancellationToken, () => UnpackCoreAsync(filePath, outputDirectory, options, cancellationToken));

    private async Task<FirmwareOpResult> UnpackCoreAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                return FirmwareOpResult.Fail("Output directory is empty");
            }

            var doc = Load(filePath);
            var text = doc.ToXml();
            Directory.CreateDirectory(outputDirectory);

            var name = Path.GetFileNameWithoutExtension(filePath);
            var safeName = string.IsNullOrWhiteSpace(name) ? "axml" : name;
            var outputPath = Path.Combine(outputDirectory, safeName + ".xml");

            var overwrite = options?.Overwrite ?? true;
            if (File.Exists(outputPath) && !overwrite)
            {
                return FirmwareOpResult.Fail($"Output exists and overwrite is disabled: {outputPath}");
            }

            await File.WriteAllTextAsync(outputPath, text, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            return FirmwareOpResult.Ok($"Unpacked AXML to {outputPath}", [outputPath]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return FirmwareOpResult.Fail($"AXML unpack failed: {ex.Message}");
        }
    }

    /// <summary>Packs the input into firmware asynchronously.<para>异步将输入打包为固件。</para></summary>
    /// <param name="inputPath">Path of the binary XML source; plain-text XML is rejected with a failed result.
    /// <para>二进制 XML 源路径；纯文本 XML 会以失败结果被拒绝。</para></param>
    /// <param name="outputPath">Destination file, committed atomically through a temporary file after a full write.
    /// <para>目标文件；完整写入后经临时文件原子提交。</para></param>
    /// <param name="cancellationToken">Cancels before the rebuild starts; writing itself is synchronous.
    /// <para>在重建开始前取消；写入本身为同步执行。</para></param>
    /// <returns>A task naming the written file, or reporting a failure prefixed <c>AXML rebuild failed:</c>.
    /// <para>任务指明写出的文件，或以 <c>AXML rebuild failed:</c> 前缀报告失败。</para></returns>
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

            var doc = Load(inputPath);
            var bytes = AXmlWriter.Write(doc);
            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            FileExtraction.WriteAtomic(outputPath, stream => stream.Write(bytes));
            return Task.FromResult(FirmwareOpResult.Ok($"Rebuilt AXML written to {outputPath}", [outputPath]));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail($"AXML rebuild failed: {ex.Message}"));
        }
    }

    private static AXmlDocument Load(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return AXmlParser.Parse(AndroidMetadataReader.Read(stream));
    }

    private static string Describe(AXmlDocument doc)
    {
        var root = doc.Root;
        var attrCount = 0;
        var nodeCount = 1;
        var stack = new Stack<AXmlElement>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var el = stack.Pop();
            attrCount += el.Attributes.Count;
            foreach (var child in el.Children)
            {
                nodeCount++;
                stack.Push(child);
            }
        }

        var sb = new StringBuilder();
        sb.Append("Android Binary XML (AXML)");
        sb.Append(" | root: <").Append(root.Name).Append('>');
        sb.Append(" | elements: ").Append(nodeCount);
        sb.Append(" | attributes: ").Append(attrCount);
        sb.Append(" | strings: ").Append(doc.StringPool.Count);
        if (doc.Namespaces.Count > 0)
        {
            sb.Append(" | namespaces: ").Append(doc.Namespaces.Count);
        }
        return sb.ToString();
    }
}