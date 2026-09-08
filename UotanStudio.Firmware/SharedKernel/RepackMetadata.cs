using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage;
using UotanStudio.Firmware.BoundedContexts.DeviceTree.Dt;

namespace UotanStudio.Firmware.SharedKernel;

/// <summary>
/// Records the source format and available packing configuration of an extraction directory.
/// Metadata is stored in a consolidated directory (<c>.uotan/metadata/&lt;partition&gt;/</c>); a
/// lightweight pointer file is left in the extraction directory for discovery and backward compatibility.
/// <para>记录解包目录的来源格式和可用打包配置。元数据存放在统一目录（<c>.uotan/metadata/&lt;partition&gt;/</c>）；
/// 解包目录中保留轻量级指针文件用于发现和向后兼容。</para>
/// </summary>
public sealed class RepackMetadata
{
    /// <summary>Names the directory metadata file.<para>目录元数据文件名。</para></summary>
    public const string FileName = ".uotanstudio-repack.json";
    /// <summary>Names the consolidated metadata subdirectory under <c>.uotan/</c>.<para>统一元数据子目录名（位于 <c>.uotan/</c> 下）。</para></summary>
    public const string MetadataFolderName = "metadata";
    private const string SchemaName = "UotanStudio.Firmware.Repack";
    private const string PointerSchemaName = "UotanStudio.Firmware.RepackPointer";
    private const long MaximumBytes = 32 * 1024 * 1024;
    private const string DefaultPartitionName = "unknown";

    /// <summary>
    /// Ambient metadata root directory set by the application layer before unpacking.
    /// Format handlers in the Firmware layer use this when no explicit <c>metadataRoot</c>
    /// is passed to <see cref="WriteAsync"/>. Thread-safe for sequential desktop usage.
    /// <para>应用层在解包前设置的环境元数据根目录。固件层格式处理器在未显式传递
    /// <c>metadataRoot</c> 时使用此值。桌面顺序使用场景下线程安全。</para>
    /// </summary>
    public static string? DefaultMetadataRoot { get; set; }

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
    /// Detects the partition name from a source image filename (strips extension).
    /// <para>从源镜像文件名检测分区名（去除扩展名）。</para>
    /// </summary>
    public static string DetectPartitionName(string sourceFileName)
    {
        var name = Path.GetFileNameWithoutExtension(sourceFileName);
        if (name.EndsWith(".raw", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return string.IsNullOrWhiteSpace(name) ? DefaultPartitionName : name;
    }

    /// <summary>
    /// Resolves the consolidated metadata directory for a given extraction output path and project root.
    /// Returns <c>{projectRoot}/.uotan/metadata/{partitionName}</c>.
    /// <para>为给定解包输出路径和项目根目录解析统一元数据目录。返回 <c>{projectRoot}/.uotan/metadata/{partitionName}</c>。</para>
    /// </summary>
    public static string ResolveMetadataDirectory(string extractionOutputDirectory, string projectRoot, string partitionName)
    {
        var stateDir = Path.Combine(projectRoot, ".uotan");
        return Path.Combine(stateDir, MetadataFolderName, partitionName);
    }

    /// <summary>
    /// Reads bounded, versioned metadata for an extraction directory. When an ambient metadata
    /// root is set (project open), the metadata is read exclusively from the consolidated
    /// location; otherwise it is read from the extraction directory itself. Returns null if absent.
    /// <para>读取解包目录的有界且带版本的元数据。设置了环境元数据根（项目打开）时只从统一位置读取；
    /// 否则只从解包目录读取。不存在时返回 null。</para>
    /// </summary>
    public static RepackMetadata? Read(string directory)
    {
        var path = ResolveMetadataPath(directory);
        return path is null ? null : ReadFromAbsolute(path);
    }

    /// <summary>
    /// Reads metadata from a specific absolute path (used after pointer resolution and by tests).
    /// <para>从指定绝对路径读取元数据（指针解析后及测试使用）。</para>
    /// </summary>
    public static RepackMetadata? ReadFromAbsolute(string absolutePath)
    {
        if (!File.Exists(absolutePath)) return null;
        if ((File.GetAttributes(absolutePath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Repack metadata must not be a symbolic link.");
        using var input = File.OpenRead(absolutePath);
        if (input.Length > MaximumBytes) throw new InvalidDataException("Repack metadata exceeds 32 MiB.");
        var metadata = JsonSerializer.Deserialize(input, RepackJsonContext.Default.RepackMetadata);
        if (metadata is null || metadata.Schema != SchemaName || metadata.Version != 1
            || string.IsNullOrWhiteSpace(metadata.FormatName) || metadata.SourceLength < 0
            || metadata.SourceProperties is null || metadata.PackingOptions is null || metadata.Configuration is null)
            throw new InvalidDataException("Invalid or unsupported repack metadata.");
        return metadata;
    }

    /// <summary>
    /// Resolves the actual metadata file path for an extraction directory. When an ambient
    /// metadata root is set (project open), the consolidated location is used exclusively;
    /// otherwise the extraction directory itself is checked. Returns <c>null</c> if absent.
    /// <para>解析解包目录的实际元数据文件路径。设置了环境元数据根（项目打开）时只使用统一位置；
    /// 否则只检查解包目录自身。不存在时返回 <c>null</c>。</para>
    /// </summary>
    public static string? ResolveActualMetadataPath(string extractionDirectory)
        => ResolveMetadataPath(extractionDirectory);

    /// <summary>
    /// Single resolution point for metadata reads: the consolidated location when
    /// <see cref="DefaultMetadataRoot"/> is set, the extraction directory otherwise.
    /// <para>元数据读取的统一解析点：设置了 <see cref="DefaultMetadataRoot"/> 时使用统一位置，
    /// 否则使用解包目录自身。</para>
    /// </summary>
    private static string? ResolveMetadataPath(string extractionDirectory)
    {
        if (!string.IsNullOrWhiteSpace(DefaultMetadataRoot))
        {
            var directoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(extractionDirectory)));
            if (string.IsNullOrWhiteSpace(directoryName)) return null;
            // Extraction directories are usually named after the source image (e.g. "system.img"),
            // while metadata is keyed by partition name ("system") — try both candidates.
            // 解包目录通常以源镜像命名（如 "system.img"），而元数据按键（分区名 "system"）存放——两个候选都尝试。
            foreach (var candidate in new[] { directoryName, DetectPartitionName(directoryName) })
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                var metaPath = Path.Combine(DefaultMetadataRoot, candidate, FileName);
                if (File.Exists(metaPath)) return metaPath;
            }

            return null;
        }

        var localPath = Path.Combine(extractionDirectory, FileName);
        return File.Exists(localPath) ? localPath : null;
    }

    /// <summary>
    /// Discovers a generated metadata sidecar file (e.g. <c>&lt;partition&gt;_fs_config</c>) in the
    /// consolidated metadata directory resolved for an extraction directory.
    /// Returns <c>null</c> when no metadata directory or matching file exists.
    /// <para>在解包目录对应的统一元数据目录中查找生成的元数据附属文件
    /// （如 <c>&lt;partition&gt;_fs_config</c>）。不存在时返回 <c>null</c>。</para>
    /// </summary>
    internal static string? DiscoverMetadataFile(string directory, string suffix)
    {
        try
        {
            var metadataDir = Path.GetDirectoryName(Path.GetFullPath(ResolveActualMetadataPath(directory) ?? directory));
            if (metadataDir is null || !Directory.Exists(metadataDir)) return null;
            return Directory.EnumerateFiles(metadataDir, "*" + suffix).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static IReadOnlyDictionary<string, string> OptionsFor(string directory, string formatName)
    {
        var metadata = Read(directory);
        if (metadata?.FormatName != formatName) return new Dictionary<string, string>();
        var options = new Dictionary<string, string>(metadata.PackingOptions, StringComparer.Ordinal);
        // Backfill superblock keys from partition_info and expose fs_config/file_contexts paths;
        // packingOptions always win, discovered files only fill absent keys.
        // 从 partition_info 回填超级块键值并暴露 fs_config/file_contexts 路径；
        // packingOptions 优先，发现的文件仅补缺。
        try
        {
            var infoPath = DiscoverMetadataFile(directory, "_info");
            if (infoPath is not null)
            {
                foreach (var (key, value) in FileSystemMetadataOverrides.LoadPartitionInfo(infoPath))
                {
                    if (!options.ContainsKey(key)) options[key] = value;
                }
            }

            if (!options.ContainsKey("FsConfigPath"))
                options["FsConfigPath"] = DiscoverMetadataFile(directory, "_fs_config") ?? string.Empty;
            if (!options.ContainsKey("FileContextsPath"))
                options["FileContextsPath"] = DiscoverMetadataFile(directory, "_file_contexts") ?? string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Backfill is best-effort; packingOptions alone remain usable.
            // 回填尽力而为；仅 packingOptions 时打包仍可用。
        }

        return options;
    }

    internal static int IntegerOption(IReadOnlyDictionary<string, string> options, string key, int fallback)
        => options.TryGetValue(key, out var value)
            ? int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : fallback;

    /// <summary>Gets an option as a nullable path: empty values (sidecar file absent) become null.
    /// <para>以可空路径形式获取选项：空值（附属文件不存在）转换为 <c>null</c>。</para></summary>
    internal static string? NullIfEmpty(IReadOnlyDictionary<string, string> options, string key)
        => options.TryGetValue(key, out var value) && value.Length > 0 ? value : null;

    /// <summary>
    /// Identifies generated metadata files so directory packers can exclude them from payloads.
    /// Recognizes both direct metadata and pointer files.
    /// <para>识别生成的元数据文件（含指针文件），供目录打包器从有效载荷中排除。</para>
    /// </summary>
    public static bool IsMetadataFile(string path)
    {
        if (!Path.GetFileName(path).Equals(FileName, StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var input = File.OpenRead(path);
            var doc = JsonNode.Parse(input);
            if (doc is null) return false;
            var schema = doc["schema"]?.GetValue<string>();
            // Valid pointer file
            if (schema == PointerSchemaName) return true;
            // Valid direct metadata
            return Read(Path.GetDirectoryName(Path.GetFullPath(path))!) is not null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Runs an extraction delegate and writes unified repack metadata afterwards.
    /// Checks for existing metadata before running the extraction to fail fast.
    /// <para>运行解包委托并随后写入统一打包元数据。在运行解包前检查已有元数据以快速失败。</para>
    /// </summary>
    public static async Task<FirmwareOpResult> UnpackAsync(IReadonlyFormatHandler handler, string sourcePath,
        string outputDirectory, UnpackOptions? options, CancellationToken cancellationToken, Func<Task<FirmwareOpResult>> unpack,
        string? metadataRoot = null)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metadataPath = Path.Combine(outputDirectory, FileName);
            if (File.Exists(metadataPath) && (options is { Overwrite: false } || !IsMetadataFile(metadataPath)))
                throw new IOException($"Repack metadata output already exists: {metadataPath}");
            var result = await unpack().ConfigureAwait(false);
            if (!result.Success) return result;
            await WriteAsync(handler, sourcePath, outputDirectory, cancellationToken, result.Metadata, metadataRoot: metadataRoot).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return FirmwareOpResult.Fail(ex.Message); }
    }

    /// <summary>
    /// Writes source and packing metadata after a successful extraction through an application service.
    /// When <paramref name="metadataRoot"/> is provided, metadata is stored under
    /// <c>{metadataRoot}/{partitionName}/</c> and the extraction directory is left untouched.
    /// Otherwise, metadata is written directly into <paramref name="outputDirectory"/> (no-project fallback).
    /// <para>应用服务成功解包后写入来源和打包元数据。提供 <paramref name="metadataRoot"/> 时，
    /// 元数据存放在 <c>{metadataRoot}/{partitionName}/</c>，解包目录不写入任何元数据文件；
    /// 否则直接写入解包目录（无项目回退）。</para>
    /// </summary>
    public static async Task WriteAsync(IReadonlyFormatHandler handler, string sourcePath, string outputDirectory,
        CancellationToken cancellationToken = default, IReadOnlyDictionary<string, string>? extractionProperties = null,
        string? metadataRoot = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var effectiveRoot = metadataRoot ?? DefaultMetadataRoot;
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

        if (!string.IsNullOrWhiteSpace(effectiveRoot))
        {
            var partitionName = DetectPartitionName(Path.GetFileName(sourcePath));
            var metaDir = Path.Combine(effectiveRoot, partitionName);
            await metadata.SaveAsync(metaDir, cancellationToken).ConfigureAwait(false);
            // Generate Android-style metadata files for filesystem images
            if (handler.FormatName is "EXT4" or "EROFS" or "F2FS" or "Android Sparse Image (simg)")
                GenerateFileSystemMetadata(metaDir, partitionName, handler, sourcePath, metadata.Configuration);
        }
        else
        {
            await metadata.SaveAsync(outputDirectory, cancellationToken).ConfigureAwait(false);
            // Generate filesystem metadata alongside the metadata file when no project context is available.
            if (handler.FormatName is "EXT4" or "EROFS" or "F2FS" or "Android Sparse Image (simg)")
            {
                var partitionName = DetectPartitionName(Path.GetFileName(sourcePath));
                GenerateFileSystemMetadata(outputDirectory, partitionName, handler, sourcePath, metadata.Configuration);
            }
        }
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

    private static void GenerateFileSystemMetadata(string metadataDirectory, string partitionName,
        IReadonlyFormatHandler handler, string sourcePath, JsonObject configuration)
    {
        try
        {
            var superblock = configuration.ContainsKey("superblock") ? configuration["superblock"] as JsonObject : null;
            // For sparse images, we need to open the raw file inside the extraction directory
            if (handler.FormatName == "Android Sparse Image (simg)")
            {
                // Sparse: use directory-based fallback since we don't have a parser here
                FileSystemMetadataGenerator.GenerateFromDirectory(metadataDirectory, partitionName, metadataDirectory);
                return;
            }

            using var stream = File.OpenRead(sourcePath);
            switch (handler.FormatName)
            {
                case "EXT4":
                    using (var parser = new BoundedContexts.FileSystem.Ext4.Ext4Parser(stream))
                    {
                        FileSystemMetadataGenerator.GenerateForExt4(metadataDirectory, partitionName, parser, superblock);
                    }

                    break;
                case "EROFS":
                    using (var parser = new BoundedContexts.FileSystem.Erofs.ErofsParser(stream))
                    {
                        FileSystemMetadataGenerator.GenerateForErofs(metadataDirectory, partitionName, parser, superblock);
                    }

                    break;
                case "F2FS":
                    using (var parser = new BoundedContexts.FileSystem.F2fs.F2fsParser(stream))
                    {
                        FileSystemMetadataGenerator.GenerateForF2fs(metadataDirectory, partitionName, parser, superblock);
                    }

                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            // Non-fatal: metadata generation is best-effort; log and continue
            System.Diagnostics.Debug.WriteLine($"Failed to generate filesystem metadata: {ex.Message}");
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RepackMetadata))]
internal partial class RepackJsonContext : JsonSerializerContext;
