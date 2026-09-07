using System.Text;
using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.AXml;

public sealed class AXmlFormatHandler : IFormatHandler
{
    public string FormatName => "Android Binary XML (AXML)";

    public IReadOnlyList<string> FileExtensions { get; } = ["xml"];

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
            var header = FormatDetector.ReadHeader(filePath, 8);
            return AXmlParser.IsAxml(header);
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

    /// <summary>
    /// Extracts the firmware contents asynchronously.
    /// <para>异步提取固件内容。</para>
    /// </summary>
    public async Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
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

    /// <summary>
    /// Packs the input into firmware asynchronously.
    /// <para>异步将输入打包为固件。</para>
    /// </summary>
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