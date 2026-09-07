using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage;
using UotanStudio.Firmware.BoundedContexts.DeviceTree.Dt;

namespace UotanStudio.Firmware.SharedKernel;

/// <summary>
/// Records the source format and available packing configuration of an extraction directory.
/// <para>记录解包目录的来源格式和可用打包配置。</para>
/// </summary>
public sealed class RepackMetadata
{
    /// <summary>Names the directory metadata file.<para>目录元数据文件名。</para></summary>
    public const string FileName = ".uotanstudio-repack.json";
    private const string SchemaName = "UotanStudio.Firmware.Repack";
    private const long MaximumBytes = 32 * 1024 * 1024;

    /// <summary>Identifies this metadata schema.<para>标识元数据模式。</para></summary>
    public string Schema { get; set; } = SchemaName;
    /// <summary>Gets the schema version.<para>获取模式版本。</para></summary>
    public int Version { get; set; } = 1;
    /// <summary>Identifies the registered source format.<para>标识已注册的来源格式。</para></summary>
    public string FormatName { get; set; } = string.Empty;
    /// <summary>Gets the original filename without its directory.<para>获取不含目录的原文件名。</para></summary>
    public string SourceFileName { get; set; } = string.Empty;
    /// <summary>Gets the original file length.<para>获取原文件长度。</para></summary>
    public long SourceLength { get; set; }
    /// <summary>Gets the source parameters reported by the format reader.<para>获取格式读取器提供的来源参数。</para></summary>
    public Dictionary<string, string> SourceProperties { get; set; } = [];
    /// <summary>Gets options consumed by the directory packer.<para>获取目录打包器读取的选项。</para></summary>
    public Dictionary<string, string> PackingOptions { get; set; } = [];
    /// <summary>Gets the original format configuration consumed by the packer.<para>获取打包器读取的原始格式配置。</para></summary>
    public JsonObject Configuration { get; set; } = [];
    /// <summary>Indicates whether the current handler can pack this directory.<para>指示当前处理器能否打包此目录。</para></summary>
    public bool CanPackDirectory { get; set; }
    /// <summary>Describes the limits of the available packer.<para>描述现有打包器的限制。</para></summary>
    public string PackingNotes { get; set; } = string.Empty;

    /// <summary>
    /// Reads bounded, versioned metadata from an extraction directory, or returns null if absent.
    /// <para>读取解包目录中有界且带版本的元数据；不存在时返回 null。</para>
    /// </summary>
    public static RepackMetadata? Read(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path)) return null;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Repack metadata must not be a symbolic link.");
        using var input = File.OpenRead(path);
        if (input.Length > MaximumBytes) throw new InvalidDataException("Repack metadata exceeds 32 MiB.");
        var metadata = JsonSerializer.Deserialize(input, RepackJsonContext.Default.RepackMetadata);
        if (metadata is null || metadata.Schema != SchemaName || metadata.Version != 1
            || string.IsNullOrWhiteSpace(metadata.FormatName) || metadata.SourceLength < 0
            || metadata.SourceProperties is null || metadata.PackingOptions is null || metadata.Configuration is null)
            throw new InvalidDataException("Invalid or unsupported repack metadata.");
        return metadata;
    }

    internal static IReadOnlyDictionary<string, string> OptionsFor(string directory, string formatName)
    {
        var metadata = Read(directory);
        return metadata?.FormatName == formatName ? metadata.PackingOptions : new Dictionary<string, string>();
    }

    internal static int IntegerOption(IReadOnlyDictionary<string, string> options, string key, int fallback)
        => options.TryGetValue(key, out var value)
            ? int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : fallback;

    /// <summary>
    /// Identifies generated metadata files so directory packers can exclude them from payloads.
    /// <para>识别生成的元数据文件，供目录打包器从有效载荷中排除。</para>
    /// </summary>
    public static bool IsMetadataFile(string path)
    {
        if (!Path.GetFileName(path).Equals(FileName, StringComparison.OrdinalIgnoreCase)) return false;
        try { return Read(Path.GetDirectoryName(Path.GetFullPath(path))!) is not null; }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static async Task<FirmwareOpResult> UnpackAsync(IReadonlyFormatHandler handler, string sourcePath,
        string outputDirectory, UnpackOptions? options, CancellationToken cancellationToken, Func<Task<FirmwareOpResult>> unpack)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metadataPath = Path.Combine(outputDirectory, FileName);
            if (File.Exists(metadataPath) && (options is { Overwrite: false } || !IsMetadataFile(metadataPath)))
                throw new IOException($"Repack metadata output already exists: {metadataPath}");
            var result = await unpack().ConfigureAwait(false);
            if (!result.Success) return result;
            await WriteAsync(handler, sourcePath, outputDirectory, cancellationToken, result.Metadata).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return FirmwareOpResult.Fail(ex.Message); }
    }

    /// <summary>
    /// Writes source and packing metadata after a successful extraction through an application service.
    /// <para>应用服务成功解包后写入来源和打包元数据。</para>
    /// </summary>
    public static async Task WriteAsync(IReadonlyFormatHandler handler, string sourcePath, string outputDirectory,
        CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string>? extractionProperties = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.GetFullPath(Path.Combine(outputDirectory, FileName));
        if (File.Exists(path) && !IsMetadataFile(path))
            throw new IOException("The extracted image contains a file reserved for repack metadata.");
        var source = await handler.ReadMetadataAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var metadata = new RepackMetadata
        {
            FormatName = handler.FormatName,
            SourceFileName = Path.GetFileName(sourcePath),
            SourceLength = new FileInfo(sourcePath).Length,
            SourceProperties = source.Metadata.ToDictionary(pair => pair.Key, pair => pair.Value)
        };
        if (handler is BootImageFormatHandler)
            metadata.Configuration = BootRepackConfiguration.FromImage(sourcePath);
        else if (handler.FormatName is "EXT4" or "EROFS" or "F2FS" or "Android Sparse Image (simg)")
            metadata.Configuration = FileSystemRepackConfiguration.Read(sourcePath,
                handler.FormatName == "Android Sparse Image (simg)");
        else if (handler is DtFormatHandler)
        {
            using var input = File.OpenRead(sourcePath);
            Span<byte> magic = stackalloc byte[4];
            input.ReadExactly(magic);
            if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(magic) == FdtConstants.DtboMagic)
                metadata.Configuration = DtboFormat.CreateConfiguration(input);
        }
        if (extractionProperties is not null)
            foreach (var pair in extractionProperties) metadata.SourceProperties[pair.Key] = pair.Value;
        metadata.CanPackDirectory = handler.FormatName switch
        {
            "EXT4" or "EROFS" or "F2FS" or "CPIO/TAR Archive" or "NTPI Firmware Package"
                or "FIH Mobile NB0 Firmware" or "OnePlus OPS Firmware" or "Android OTA Payload (payload.bin)"
                or "Android Logical Partition (super)" => true,
            "Device Tree (DTB/DTBO)" => metadata.Configuration.Count > 0,
            "Android Sparse Image (simg)" => File.Exists(Path.Combine(outputDirectory,
                Path.GetFileNameWithoutExtension(sourcePath) + ".raw")),
            "Android Boot / Vendor Boot Image" => metadata.Configuration.Count > 0,
            _ => false
        };
        var optionKeys = handler.FormatName switch
        {
            "EXT4" => new[] { "BlockSize" },
            "EROFS" => ["BlockSize", "VolumeName"],
            "F2FS" => ["BlockSize", "SegmentSize", "VolumeName"],
            _ => []
        };
        foreach (var key in optionKeys)
            if (metadata.SourceProperties.TryGetValue(key, out var value)) metadata.PackingOptions[key] = value;
        metadata.PackingNotes = metadata.CanPackDirectory
            ? "Use packingOptions and configuration. Source properties are descriptive; other fields are not automatically restored."
            : "The current handler cannot rebuild this extraction directory. A format-specific packing workflow is required.";
        if (handler.FormatName == "CPIO/TAR Archive") metadata.PackingNotes += " The directory packer writes newc CPIO.";
        if (handler.FormatName == "Android OTA Payload (payload.bin)") metadata.PackingNotes += " The directory packer creates an unsigned full payload.";
        if (handler.FormatName == "Android Logical Partition (super)") metadata.PackingNotes += " The directory packer creates new default LP groups and geometry.";
        if (metadata.Configuration.ContainsKey("superblock"))
            metadata.PackingNotes += " configuration.superblock records the original filesystem metadata for inspection; only packingOptions are applied when rebuilding the filesystem.";
        if (!source.Success) metadata.PackingNotes += " Source metadata could not be read: " + source.Summary;
        await metadata.SaveAsync(outputDirectory, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Atomically saves bounded directory metadata.<para>原子保存有大小限制的目录元数据。</para></summary>
    public async Task SaveAsync(string outputDirectory, CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(Path.Combine(outputDirectory, FileName));
        if (File.Exists(path) && !IsMetadataFile(path))
            throw new IOException("The output contains a file reserved for repack metadata.");
        Directory.CreateDirectory(outputDirectory);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(output, this, RepackJsonContext.Default.RepackMetadata, cancellationToken).ConfigureAwait(false);
                if (output.Length > MaximumBytes) throw new InvalidDataException("Repack metadata exceeds 32 MiB.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RepackMetadata))]
internal partial class RepackJsonContext : JsonSerializerContext;
