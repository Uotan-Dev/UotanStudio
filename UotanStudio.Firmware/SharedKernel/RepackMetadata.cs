using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage;
using UotanStudio.Firmware.BoundedContexts.DeviceTree.Dt;

namespace UotanStudio.Firmware.SharedKernel;

/// <summary>
/// Records the source format and packing configuration of an extraction directory.
/// Project operations store metadata in the owning sub-project's .uotan/metadata directory;
/// explicit local operations store it in the extraction directory, without cross-fallback.
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

    private static string? _defaultMetadataRoot;
    private static readonly AsyncLocal<MetadataScope?> CurrentScope = new();
    private static readonly AsyncLocal<IReadOnlyDictionary<string, string>?> CurrentPackingOverrides = new();

    /// <summary>Legacy default metadata root. An operation scope takes precedence across awaits and background work.</summary>
    public static string? DefaultMetadataRoot
    {
        get => CurrentScope.Value is { } scope ? scope.Root : _defaultMetadataRoot;
        set => _defaultMetadataRoot = value;
    }

    /// <summary>Fixes metadata ownership for nested legacy handlers and background work in this operation.</summary>
    public static IDisposable UseContext(RepackMetadataContext context)
        => new MetadataScope(context.Capture().ResolveWriteRoot(null));

    /// <summary>Indicates that this asynchronous operation has already fixed its metadata ownership.</summary>
    public static bool HasContext => CurrentScope.Value is not null;

    /// <summary>
    /// Gets the application-supplied packing-option overrides for the current operation, or <c>null</c>.
    /// When set, exact-restore is skipped and filesystem packers merge these options over the on-disk
    /// packing options so the packing-settings panel can override the unpacked metadata.
    /// <para>获取当前操作的应用层打包选项覆盖，未设置时为 <c>null</c>。
    /// 设置时目录打包器跳过精确还原，并将这些选项合并到磁盘打包选项之上，
    /// 使打包设置面板能够覆盖解包时产出的元数据。</para>
    /// </summary>
    public static IReadOnlyDictionary<string, string>? PackingOverrides => CurrentPackingOverrides.Value;

    /// <summary>
    /// Indicates whether packing overrides are active for the current operation.
    /// <para>指示当前操作是否启用了打包设置覆盖。</para>
    /// </summary>
    public static bool HasPackingOverrides => CurrentPackingOverrides.Value is not null;

    /// <summary>
    /// Applies the given options as packing overrides for the current packaging operation. The returned
    /// scope restores the previous value on dispose; dispose it before leaving the packaging call so the
    /// override never leaks into unrelated work. Passing <c>null</c> clears the override.
    /// <para>将给定选项作为当前打包操作的打包覆盖应用。返回的作用域在释放时恢复先前值；
    /// 应在打包调用结束前释放，避免覆盖泄漏到无关工作。传入 <c>null</c> 清除覆盖。</para>
    /// </summary>
    /// <param name="overrides">Override options merged over the on-disk packing options.
    /// <para>合并到磁盘打包选项之上的覆盖选项。</para></param>
    /// <returns>Disposable scope restoring the previous overrides. <para>释放后恢复先前覆盖的可释放作用域。</para></returns>
    public static IDisposable UsePackingOverrides(IReadOnlyDictionary<string, string>? overrides)
    {
        var previous = CurrentPackingOverrides.Value;
        CurrentPackingOverrides.Value = overrides;
        return new PackingOverrideScope(previous);
    }

    private sealed class PackingOverrideScope : IDisposable
    {
        private readonly IReadOnlyDictionary<string, string>? _previous;
        public PackingOverrideScope(IReadOnlyDictionary<string, string>? previous) => _previous = previous;
        public void Dispose() => CurrentPackingOverrides.Value = _previous;
    }

    private sealed class MetadataScope : IDisposable
    {
        private readonly MetadataScope? _previous;
        public string? Root { get; }

        public MetadataScope(string? root)
        {
            Root = root;
            _previous = CurrentScope.Value;
            CurrentScope.Value = this;
        }

        public void Dispose()
        {
            if (ReferenceEquals(CurrentScope.Value, this)) CurrentScope.Value = _previous;
        }
    }

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
    /// <summary>Identifies the private filesystem image and verified host projection retained for exact recovery.</summary>
    public string? FileSystemPreservationId { get; set; }
    /// <summary>Integrity checksum of the preservation manifest; editing its parsed snapshot is not an implicit rebuild instruction.</summary>
    public string? FileSystemPreservationHash { get; set; }

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
    /// Reads bounded metadata for an extraction directory under an explicit context. Explicit
    /// project-root / local-directory contexts never consult
    /// <see cref="DefaultMetadataRoot"/> and never cross-fallback.
    /// <para>在显式上下文下读取解包目录的有界元数据。显式工程根／本地目录上下文绝不读取
    /// <see cref="DefaultMetadataRoot"/>，也绝不交叉回退。</para>
    /// </summary>
    public static RepackMetadata? Read(string directory, RepackMetadataContext context)
    {
        var path = context.ResolveMetadataPath(directory);
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
    /// Resolves the actual metadata file path under an explicit context. Explicit contexts are
    /// evaluated immediately and never re-read <see cref="DefaultMetadataRoot"/> after awaits.
    /// <para>在显式上下文下解析实际元数据文件路径。显式上下文立即求值，
    /// await 之后绝不重读 <see cref="DefaultMetadataRoot"/>。</para>
    /// </summary>
    public static string? ResolveActualMetadataPath(string extractionDirectory, RepackMetadataContext context)
        => context.ResolveMetadataPath(extractionDirectory);

    /// <summary>
    /// Single resolution point for metadata reads: the consolidated location when
    /// <see cref="DefaultMetadataRoot"/> is set, the extraction directory otherwise.
    /// <para>元数据读取的统一解析点：设置了 <see cref="DefaultMetadataRoot"/> 时使用统一位置，
    /// 否则使用解包目录自身。</para>
    /// </summary>
    private static string? ResolveMetadataPath(string extractionDirectory)
        => ResolveAmbientMetadataPath(extractionDirectory);

    /// <summary>
    /// Ambient resolution used by <see cref="RepackMetadataContext"/> for the unspecified kind:
    /// the consolidated location when <see cref="DefaultMetadataRoot"/> is set, the extraction
    /// directory otherwise.
    /// <para>供 <see cref="RepackMetadataContext"/> 在未指定状态下使用的环境解析：
    /// 设置了 <see cref="DefaultMetadataRoot"/> 时使用统一位置，否则使用解包目录自身。</para>
    /// </summary>
    internal static string? ResolveAmbientMetadataPath(string extractionDirectory)
    {
        if (!string.IsNullOrWhiteSpace(DefaultMetadataRoot))
        {
            var directoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(extractionDirectory)));
            if (string.IsNullOrWhiteSpace(directoryName)) return null;
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
    internal static string? DiscoverMetadataFile(string directory, string suffix, string? sourceFileName = null)
    {
        try
        {
            var metadataPath = ResolveActualMetadataPath(directory);
            if (metadataPath is null) return null;
            var metadataDir = Path.GetDirectoryName(Path.GetFullPath(metadataPath));
            if (metadataDir is null || !Directory.Exists(metadataDir)) return null;
            sourceFileName ??= ReadFromAbsolute(metadataPath)?.SourceFileName;
            if (string.IsNullOrWhiteSpace(sourceFileName)) return null;
            var path = Path.Combine(metadataDir, DetectPartitionName(sourceFileName) + suffix);
            return File.Exists(path) ? path : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static IReadOnlyDictionary<string, string> OptionsFor(string directory, string formatName)
    {
        var metadata = Read(directory);
        if (metadata is null
            || (metadata.FormatName != formatName && metadata.FormatName is not ("EXT4" or "EROFS" or "F2FS")))
            return new Dictionary<string, string>();
        var options = new Dictionary<string, string>(metadata.PackingOptions, StringComparer.Ordinal);
        if (CurrentPackingOverrides.Value is { } overrides)
            foreach (var pair in overrides) options[pair.Key] = pair.Value;
        try
        {
            var metadataDirectory = Path.GetDirectoryName(ResolveActualMetadataPath(directory)) ?? directory;
            var document = FileSystemMetadataOverrides.TryLoadDocument(metadataDirectory, metadata.SourceFileName, metadata.FileSystemPreservationId);
            if (document is { PartitionInfo.Count: > 0 })
            {
                foreach (var (key, value) in document.PartitionInfo)
                {
                    if (!options.ContainsKey(key)) options[key] = value;
                }
            }
            else
            {
                var infoPath = DiscoverMetadataFile(directory, "_info", metadata.SourceFileName);
                if (infoPath is not null)
                {
                    foreach (var (key, value) in FileSystemMetadataOverrides.LoadPartitionInfo(infoPath))
                    {
                        if (!options.ContainsKey(key)) options[key] = value;
                    }
                }
            }

            if (!options.ContainsKey("FsConfigPath"))
                options["FsConfigPath"] = DiscoverMetadataFile(directory, "_fs_config", metadata.SourceFileName) ?? string.Empty;
            if (!options.ContainsKey("FileContextsPath"))
                options["FileContextsPath"] = DiscoverMetadataFile(directory, "_file_contexts", metadata.SourceFileName) ?? string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
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
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
            using var input = File.OpenRead(path);
            if (input.Length > MaximumBytes) return false;
            var doc = JsonNode.Parse(input);
            if (doc is null) return false;
            var schema = doc["schema"]?.GetValue<string>();
            if (schema == PointerSchemaName) return true;
            return ReadFromAbsolute(path) is not null;
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
        => await UnpackAsync(handler, sourcePath, outputDirectory, options, cancellationToken, unpack,
            RepackMetadataContext.Unspecified, metadataRoot).ConfigureAwait(false);

    /// <summary>
    /// Runs an extraction delegate and writes unified repack metadata afterwards using an explicit
    /// context. The context is captured at call time: explicit project-root / local-directory
    /// kinds never consult <see cref="DefaultMetadataRoot"/> after awaits, and the legacy
    /// <paramref name="metadataRoot"/> parameter is ignored when the context is explicit.
    /// <para>以显式上下文运行解包委托并随后写入统一打包元数据。上下文在调用时固定：
    /// 显式工程根／本地目录在 await 之后绝不读取 <see cref="DefaultMetadataRoot"/>；
    /// 上下文为显式状态时忽略旧 <paramref name="metadataRoot"/> 参数。</para>
    /// </summary>
    public static async Task<FirmwareOpResult> UnpackAsync(IReadonlyFormatHandler handler, string sourcePath,
        string outputDirectory, UnpackOptions? options, CancellationToken cancellationToken, Func<Task<FirmwareOpResult>> unpack,
        RepackMetadataContext context, string? metadataRoot = null)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metadataPath = Path.Combine(outputDirectory, FileName);
            if (File.Exists(metadataPath) && (options is { Overwrite: false } || !IsMetadataFile(metadataPath)))
                throw new IOException($"Repack metadata output already exists: {metadataPath}");
            var result = await unpack().ConfigureAwait(false);
            if (!result.Success) return result;
            await WriteAsync(handler, sourcePath, outputDirectory, context, cancellationToken, result.Metadata, metadataRoot).ConfigureAwait(false);
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
        await WriteAsyncCore(handler, sourcePath, outputDirectory, cancellationToken, extractionProperties, effectiveRoot).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes source and packing metadata using an explicit context. When the context is an
    /// explicit project root, metadata is stored under <c>{root}/{partitionName}/</c> exclusively;
    /// when it is an explicit local mode, metadata is written directly into
    /// <paramref name="outputDirectory"/> even if an ambient root is set. The context is captured
    /// at call time and never re-read <see cref="DefaultMetadataRoot"/> after awaits.
    /// <para>以显式上下文写入来源和打包元数据。显式工程根时元数据只存放在
    /// <c>{root}/{分区名}/</c>；显式本地模式时即使环境根已设置也直接写入
    /// <paramref name="outputDirectory"/>。上下文在调用时固定，await 之后绝不重读
    /// <see cref="DefaultMetadataRoot"/>。</para>
    /// </summary>
    /// <remarks>
    /// When <paramref name="context"/> is unspecified (default), <paramref name="metadataRoot"/>
    /// keeps its historical meaning and the ambient <see cref="DefaultMetadataRoot"/> applies;
    /// an explicit context supersedes <paramref name="metadataRoot"/> (mutually exclusive).
    /// <para><paramref name="context"/> 未指定（默认）时 <paramref name="metadataRoot"/> 保持原有
    /// 语义并应用环境 <see cref="RepackMetadata.DefaultMetadataRoot"/>；显式上下文优先于
    /// <paramref name="metadataRoot"/>（两者互斥）。</para>
    /// </remarks>
    public static async Task WriteAsync(IReadonlyFormatHandler handler, string sourcePath, string outputDirectory,
        RepackMetadataContext context, CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? extractionProperties = null, string? metadataRoot = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var effectiveRoot = context.ResolveWriteRoot(metadataRoot);
        await WriteAsyncCore(handler, sourcePath, outputDirectory, cancellationToken, extractionProperties, effectiveRoot).ConfigureAwait(false);
    }

    private static async Task WriteAsyncCore(IReadonlyFormatHandler handler, string sourcePath, string outputDirectory,
        CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? extractionProperties, string? effectiveRoot)
    {
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
            metadata.PackingNotes += metadata.Configuration["fileSystem"]?.GetValue<string>() == "EROFS"
                ? " EROFS edits inherit codec parameters, UUID, volume and inode attributes from the verified original image; compression level is not recorded on disk. Physical layout is regenerated."
                : " configuration.superblock records the original filesystem metadata for inspection; only packingOptions are applied when rebuilding the filesystem.";
        if (!source.Success) metadata.PackingNotes += " Source metadata could not be read: " + source.Summary;

        if (handler is BoundedContexts.FileSystem.Ext4.Ext4FormatHandler
            or BoundedContexts.FileSystem.Erofs.ErofsFormatHandler
            or BoundedContexts.FileSystem.F2fs.F2fsFormatHandler
            || handler is BoundedContexts.FileSystem.Sparse.SparseFormatHandler
                && metadata.Configuration["fileSystem"]?.GetValue<string>() is "EXT4" or "EROFS" or "F2FS")
        {
            var partition = DetectPartitionName(Path.GetFileName(sourcePath));
            var directory = string.IsNullOrWhiteSpace(effectiveRoot) ? outputDirectory : Path.Combine(effectiveRoot, partition);
            Directory.CreateDirectory(directory);
            GenerateFileSystemMetadata(directory, partition, handler, sourcePath, metadata.Configuration);
            metadata.CanPackDirectory = true;
            metadata.PackingNotes = "Retains the original image and verifies the extracted projection. Unmodified images restore exactly; unsupported semantic edits fail before output publication.";
            await FileSystemPreservation.CaptureAsync(sourcePath, outputDirectory, directory, metadata, cancellationToken).ConfigureAwait(false);
            await metadata.SaveAsync(directory, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!string.IsNullOrWhiteSpace(effectiveRoot))
        {
            var partitionName = DetectPartitionName(Path.GetFileName(sourcePath));
            var metaDir = Path.Combine(effectiveRoot, partitionName);
            await metadata.SaveAsync(metaDir, cancellationToken).ConfigureAwait(false);
            if (handler.FormatName is "EXT4" or "EROFS" or "F2FS" or "Android Sparse Image (simg)")
                GenerateFileSystemMetadata(metaDir, partitionName, handler, sourcePath, metadata.Configuration);
        }
        else
        {
            await metadata.SaveAsync(outputDirectory, cancellationToken).ConfigureAwait(false);
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
            if (handler.FormatName == "Android Sparse Image (simg)")
            {
                using var image = BoundedContexts.FileSystem.Sparse.SparseReader.FromImageFile(sourcePath);
                using var logical = new BoundedContexts.FileSystem.Sparse.SparseReadStream(image);
                switch (configuration["fileSystem"]?.GetValue<string>())
                {
                    case "EXT4":
                        using (var parser = new BoundedContexts.FileSystem.Ext4.Ext4Parser(logical))
                            FileSystemMetadataGenerator.GenerateForExt4(metadataDirectory, partitionName, parser, superblock);
                        break;
                    case "EROFS":
                        using (var parser = new BoundedContexts.FileSystem.Erofs.ErofsParser(logical))
                            FileSystemMetadataGenerator.GenerateForErofs(metadataDirectory, partitionName, parser, superblock);
                        break;
                    case "F2FS":
                        using (var parser = new BoundedContexts.FileSystem.F2fs.F2fsParser(logical))
                            FileSystemMetadataGenerator.GenerateForF2fs(metadataDirectory, partitionName, parser, superblock);
                        break;
                }
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
            System.Diagnostics.Debug.WriteLine($"Failed to generate filesystem metadata: {ex.Message}");
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RepackMetadata))]
internal partial class RepackJsonContext : JsonSerializerContext;