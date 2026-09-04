using UotanStudio.Firmware.Common;

namespace UotanStudio.Firmware.Arsc;

/// <summary>
/// <para>Android Resource Table (resources.arsc) 格式处理器：识别 ARSC 文件、读取元数据与解包。</para>
/// Android Resource Table (resources.arsc) format handler: detects ARSC files, reads metadata and unpacks.
/// </summary>
public sealed class ArscFormatHandler : IFormatHandler
{
    /// <inheritdoc />
    public string FormatName => "Android Resource Table (ARSC)";

    /// <inheritdoc />
    public IReadOnlyList<string> FileExtensions { get; } = ["arsc"];

    /// <inheritdoc />
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            var header = FormatDetector.ReadHeader(filePath, 12);
            return ArscParser.IsArsc(header);
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
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

            System.IO.File.WriteAllText(outputPath, text);
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

    /// <inheritdoc />
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
    {
        // ARSC 重建：解析模型后由 ArscWriter 重新序列化（支持值级编辑后的写回）。
        // ARSC rebuild: parse the model, then re-serialize via ArscWriter (supports value-level edits).
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
            System.IO.File.WriteAllBytes(outputPath, bytes);
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
                    foreach (var entry in type.Entries)
                    {
                        if (entry is null) continue;
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