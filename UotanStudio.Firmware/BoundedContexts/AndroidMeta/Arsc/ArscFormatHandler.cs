using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Arsc.Models;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Arsc.Parsing;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Arsc.Writing;
using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Arsc;

public sealed class ArscFormatHandler : IFormatHandler
{
    public string FormatName => "Android Resource Table (ARSC)";

    public IReadOnlyList<string> FileExtensions { get; } = ["arsc"];

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
            var header = FormatDetector.ReadHeader(filePath, 12);
            return ArscParser.IsArsc(header);
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

    /// <summary>
    /// Extracts the firmware contents asynchronously.
    /// <para>异步提取固件内容。</para>
    /// </summary>
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
        => UotanStudio.Firmware.SharedKernel.Repacking.RepackMetadata.UnpackAsync(this, filePath, outputDirectory, options,
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