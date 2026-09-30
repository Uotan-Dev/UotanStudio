using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.IO;

namespace UotanStudio.Firmware.Core.Repacking;

/// <summary>
/// Records the source format and packing configuration of an extraction directory.
/// Project operations store metadata in the owning sub-project's .uotan/metadata directory;
/// explicit local operations store it in the extraction directory, without cross-fallback.
/// <para>记录解包目录的来源格式与打包配置。工程操作把元数据存放在所属子项目的 <c>.uotan/metadata</c> 目录；
/// 显式本地操作存放在解包目录自身，且不交叉回退。</para>
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

    /// <summary>
    /// Process-wide default metadata root consulted when no operation scope is active;
    /// an operation scope from <see cref="UseContext"/> takes precedence across awaits and background work.
    /// <para>无操作作用域生效时使用的进程级默认元数据根；<see cref="UseContext"/> 的操作作用域
    /// 在 await 与后台工作中优先于它。</para>
    /// </summary>
    /// <value>
    /// Absolute root directory for consolidated metadata, or <see langword="null"/> when metadata
    /// is stored inside each extraction directory.
    /// <para>统一元数据的绝对根目录；为 <see langword="null"/> 时元数据存放在各解包目录内部。</para>
    /// </value>
    public static string? DefaultMetadataRoot
    {
        get => CurrentScope.Value is { } scope ? scope.Root : _defaultMetadataRoot;
        set => _defaultMetadataRoot = value;
    }

    /// <summary>
    /// Fixes metadata ownership for nested handlers and background work in this operation.
    /// <para>为本操作中的嵌套处理器与后台工作固定元数据归属。</para>
    /// </summary>
    /// <param name="context">
    /// Context whose captured state resolves the write root for this operation.
    /// <para>其捕获状态用于解析本操作写入根目录的上下文。</para>
    /// </param>
    /// <returns>
    /// A disposable scope; disposing it restores the previous metadata ownership.
    /// <para>可释放作用域；释放后恢复先前的元数据归属。</para>
    /// </returns>
    public static IDisposable UseContext(RepackMetadataContext context)
        => new MetadataScope(context.Capture().ResolveWriteRoot(null));

    /// <summary>
    /// Indicates that this asynchronous operation has already fixed its metadata ownership.
    /// <para>表示当前异步操作已经固定了其元数据归属。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> while a scope from <see cref="UseContext"/> is active in the current
    /// asynchronous flow; otherwise <see langword="false"/>.
    /// <para>当前异步流中存在生效的 <see cref="UseContext"/> 作用域时为 <see langword="true"/>，否则为 <see langword="false"/>。</para>
    /// </value>
    public static bool HasContext => CurrentScope.Value is not null;

    /// <summary>
    /// Gets the application-supplied packing-option overrides for the current operation, or <c>null</c>.
    /// When set, exact-restore is skipped and filesystem packers merge these options over the on-disk
    /// packing options so the packing-settings panel can override the unpacked metadata.
    /// <para>获取当前操作的应用层打包选项覆盖，未设置时为 <c>null</c>。
    /// 设置时目录打包器跳过精确还原，并将这些选项合并到磁盘打包选项之上，
    /// 使打包设置面板能够覆盖解包时产出的元数据。</para>
    /// </summary>
    /// <value>
    /// The override map for the current asynchronous flow, or <see langword="null"/> when no overrides are applied.
    /// <para>当前异步流的覆盖映射；未应用覆盖时为 <see langword="null"/>。</para>
    /// </value>
    public static IReadOnlyDictionary<string, string>? PackingOverrides => CurrentPackingOverrides.Value;

    /// <summary>
    /// Indicates whether packing overrides are active for the current operation.
    /// <para>指示当前操作是否启用了打包设置覆盖。</para>
    /// </summary>
    /// <value>
    /// <see langword="true"/> while a scope from <see cref="UsePackingOverrides"/> holds a non-null map.
    /// <para><see cref="UsePackingOverrides"/> 的作用域持有非空映射期间为 <see langword="true"/>。</para>
    /// </value>
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
    /// <value>The fixed schema id; reads reject any other value. <para>固定模式标识；读取会拒绝其他值。</para></value>
    public string Schema { get; set; } = SchemaName;
    /// <summary>Gets the schema version.<para>获取模式版本。</para></summary>
    /// <value>Schema revision; reads accept only <c>1</c>. <para>模式版本；读取仅接受 <c>1</c>。</para></value>
    public int Version { get; set; } = 1;
    /// <summary>Identifies the registered source format.<para>标识已注册的来源格式。</para></summary>
    /// <value>
    /// Handler format name; blank values are rejected when the file is read back.
    /// <para>处理器格式名；回读时空白值会被拒绝。</para>
    /// </value>
    public string FormatName { get; set; } = string.Empty;
    /// <summary>Gets the original filename without its directory.<para>获取不含目录的原文件名。</para></summary>
    /// <value>
    /// Captured base name of the source image; it drives partition-name detection.
    /// <para>捕获的源镜像基本文件名；用于推导分区名。</para>
    /// </value>
    public string SourceFileName { get; set; } = string.Empty;
    /// <summary>Gets the original file length.<para>获取原文件长度。</para></summary>
    /// <value>
    /// Byte length of the source image at capture time; negative values are rejected on read.
    /// <para>捕获时源镜像的字节长度；回读时负值会被拒绝。</para>
    /// </value>
    public long SourceLength { get; set; }
    /// <summary>Gets the source parameters reported by the format reader.<para>获取格式读取器提供的来源参数。</para></summary>
    /// <value>
    /// Descriptive key/value pairs merged from the format reader and extraction properties;
    /// the packer does not restore them automatically.
    /// <para>由格式读取器与解包属性合并而来的描述性键值对；打包器不会自动还原它们。</para>
    /// </value>
    public Dictionary<string, string> SourceProperties { get; set; } = [];
    /// <summary>Gets options consumed by the directory packer.<para>获取目录打包器读取的选项。</para></summary>
    /// <value>
    /// Packer options stored in the JSON; active <see cref="PackingOverrides"/> merge over these values.
    /// <para>存储在 JSON 中的打包器选项；生效的 <see cref="PackingOverrides"/> 会合并到这些值之上。</para>
    /// </value>
    public Dictionary<string, string> PackingOptions { get; set; } = [];
    /// <summary>Gets the original format configuration consumed by the packer.<para>获取打包器读取的原始格式配置。</para></summary>
    /// <value>
    /// Format-owned JSON tree written by the provider and read back during rebuild.
    /// <para>由提供方写入、重建时读回的格式专属 JSON 树。</para>
    /// </value>
    public JsonObject Configuration { get; set; } = [];
    /// <summary>Indicates whether the current handler can pack this directory.<para>指示当前处理器能否打包此目录。</para></summary>
    /// <value>
    /// Set by the format provider; it selects the capability wording prepended to <see cref="PackingNotes"/>.
    /// <para>由格式提供方设置；决定 <see cref="PackingNotes"/> 前缀的能力描述措辞。</para>
    /// </value>
    public bool CanPackDirectory { get; set; }
    /// <summary>Describes the limits of the available packer.<para>描述现有打包器的限制。</para></summary>
    /// <value>
    /// Free-text notes: a common capability sentence, then format-supplied notes,
    /// then a warning when source metadata could not be read.
    /// <para>自由文本说明：先通用能力句，再格式提供方追加的说明，最后是来源元数据读取失败的警告。</para>
    /// </value>
    public string PackingNotes { get; set; } = string.Empty;
    /// <summary>
    /// Identifies the private filesystem image and verified host projection retained for exact recovery.
    /// <para>标识为精确还原而保留的私有文件系统镜像及已校验的主机投影。</para>
    /// </summary>
    /// <value>
    /// Preservation identifier written by the provider, or <see langword="null"/> when the format keeps no artifacts.
    /// <para>由提供方写入的保留标识；格式无保留产物时为 <see langword="null"/>。</para>
    /// </value>
    public string? FileSystemPreservationId { get; set; }
    /// <summary>
    /// Integrity checksum of the preservation manifest; editing its parsed snapshot is not an implicit rebuild instruction.
    /// <para>保留清单的完整性校验和；编辑其解析快照不构成隐式重建指令。</para>
    /// </summary>
    /// <value>
    /// Hash recorded alongside <see cref="FileSystemPreservationId"/>, or <see langword="null"/> when nothing was captured.
    /// <para>与 <see cref="FileSystemPreservationId"/> 一同记录的哈希；未捕获产物时为 <see langword="null"/>。</para>
    /// </value>
    public string? FileSystemPreservationHash { get; set; }

    /// <summary>
    /// Detects the partition name from a source image filename (strips extension).
    /// <para>从源镜像文件名检测分区名（去除扩展名）。</para>
    /// </summary>
    /// <param name="sourceFileName">
    /// Filename to derive from; a trailing <c>.raw</c> after the extension is also stripped.
    /// <para>用于推导的文件名；扩展名之后再以 <c>.raw</c> 结尾的部分也会被去除。</para>
    /// </param>
    /// <returns>
    /// The stripped base name, or <c>unknown</c> when the result would be blank.
    /// <para>去除后缀的基础名；结果为空白时返回 <c>unknown</c>。</para>
    /// </returns>
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
    /// <param name="extractionOutputDirectory">
    /// Extraction output path accepted for call-site convenience; the returned path does not depend on it.
    /// <para>为方便调用方而保留的解包输出路径；返回路径并不依赖它。</para>
    /// </param>
    /// <param name="projectRoot">
    /// Project root holding the <c>.uotan</c> state directory. <para>持有 <c>.uotan</c> 状态目录的项目根。</para>
    /// </param>
    /// <param name="partitionName">
    /// Partition name used as the final directory segment. <para>作为最后一级目录段的分区名。</para>
    /// </param>
    /// <returns>
    /// The consolidated metadata directory path; the directory itself is not created.
    /// <para>统一元数据目录路径；不会创建该目录。</para>
    /// </returns>
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
    /// <param name="directory">
    /// Extraction directory whose metadata is read. <para>要读取其元数据的解包目录。</para>
    /// </param>
    /// <returns>
    /// The validated metadata, or <see langword="null"/> when no metadata file exists at the resolved location.
    /// <para>校验通过的元数据；解析位置不存在元数据文件时返回 <see langword="null"/>。</para>
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// The stored file is a symbolic link, exceeds 32 MiB, or fails schema validation.
    /// <para>存储文件为符号链接、超过 32 MiB 或未通过模式校验。</para>
    /// </exception>
    /// <exception cref="JsonException">
    /// The stored file is not valid JSON. <para>存储文件不是合法 JSON。</para>
    /// </exception>
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
    /// <param name="directory">
    /// Extraction directory whose metadata is read. <para>要读取其元数据的解包目录。</para>
    /// </param>
    /// <param name="context">
    /// Explicit context resolving the metadata path at call time. <para>在调用时解析元数据路径的显式上下文。</para>
    /// </param>
    /// <returns>
    /// The validated metadata, or <see langword="null"/> when the context resolves no existing path.
    /// <para>校验通过的元数据；上下文解析出的路径不存在时返回 <see langword="null"/>。</para>
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// The stored file is a symbolic link, exceeds 32 MiB, or fails schema validation.
    /// <para>存储文件为符号链接、超过 32 MiB 或未通过模式校验。</para>
    /// </exception>
    /// <exception cref="JsonException">
    /// The stored file is not valid JSON. <para>存储文件不是合法 JSON。</para>
    /// </exception>
    public static RepackMetadata? Read(string directory, RepackMetadataContext context)
    {
        var path = context.ResolveMetadataPath(directory);
        return path is null ? null : ReadFromAbsolute(path);
    }

    /// <summary>
    /// Reads metadata from a specific absolute path (used after pointer resolution and by tests).
    /// <para>从指定绝对路径读取元数据（指针解析后及测试使用）。</para>
    /// </summary>
    /// <param name="absolutePath">
    /// Absolute path of the metadata file; a missing file yields <see langword="null"/>.
    /// <para>元数据文件的绝对路径；文件不存在时返回 <see langword="null"/>。</para>
    /// </param>
    /// <returns>
    /// The validated metadata, or <see langword="null"/> when the file does not exist.
    /// <para>校验通过的元数据；文件不存在时返回 <see langword="null"/>。</para>
    /// </returns>
    /// <exception cref="InvalidDataException">
    /// The file is a symbolic link, exceeds 32 MiB, or fails schema, version, format-name or field validation.
    /// <para>文件为符号链接、超过 32 MiB，或未通过模式、版本、格式名与字段校验。</para>
    /// </exception>
    /// <exception cref="JsonException">
    /// The file content is not valid JSON. <para>文件内容不是合法 JSON。</para>
    /// </exception>
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
    /// <param name="extractionDirectory">
    /// Extraction directory to resolve for. <para>要解析的解包目录。</para>
    /// </param>
    /// <returns>
    /// Absolute path of the existing metadata file, or <see langword="null"/> when none is found.
    /// <para>已存在的元数据文件绝对路径；未找到时返回 <see langword="null"/>。</para>
    /// </returns>
    public static string? ResolveActualMetadataPath(string extractionDirectory)
        => ResolveMetadataPath(extractionDirectory);

    /// <summary>
    /// Resolves the actual metadata file path under an explicit context. Explicit contexts are
    /// evaluated immediately and never re-read <see cref="DefaultMetadataRoot"/> after awaits.
    /// <para>在显式上下文下解析实际元数据文件路径。显式上下文立即求值，
    /// await 之后绝不重读 <see cref="DefaultMetadataRoot"/>。</para>
    /// </summary>
    /// <param name="extractionDirectory">
    /// Extraction directory to resolve for. <para>要解析的解包目录。</para>
    /// </param>
    /// <param name="context">
    /// Explicit context evaluated at call time. <para>在调用时求值的显式上下文。</para>
    /// </param>
    /// <returns>
    /// The metadata path resolved by the context, or <see langword="null"/> when it resolves nothing.
    /// <para>上下文解析出的元数据路径；未解析到任何路径时返回 <see langword="null"/>。</para>
    /// </returns>
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

    /// <summary>
    /// Identifies generated metadata files so directory packers can exclude them from payloads.
    /// Recognizes both direct metadata and pointer files.
    /// <para>识别生成的元数据文件（含指针文件），供目录打包器从有效载荷中排除。</para>
    /// </summary>
    /// <param name="path">
    /// File to inspect; only files named <see cref="FileName"/> (case-insensitively) are considered.
    /// <para>要检查的文件；只有文件名（不区分大小写）等于 <see cref="FileName"/> 的文件才会被判定。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> for a readable, non-reparse pointer file or valid repack metadata within the size
    /// bound; <see langword="false"/> for any other file, including unreadable or malformed ones.
    /// <para>可读、非重解析点、在大小限制内的指针文件或有效元数据返回 <see langword="true"/>；其他文件（包括不可读或格式错误的）返回 <see langword="false"/>。</para>
    /// </returns>
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
    /// <param name="handler">
    /// Format handler whose name and source metadata are published with the record.
    /// <para>其格式名与来源元数据会随记录一起发布的格式处理器。</para>
    /// </param>
    /// <param name="sourcePath">
    /// Source image path recorded in the metadata. <para>记录进元数据的源镜像路径。</para>
    /// </param>
    /// <param name="outputDirectory">
    /// Extraction output directory. <para>解包输出目录。</para>
    /// </param>
    /// <param name="options">
    /// Unpack options; when <c>Overwrite</c> is disabled an existing metadata file fails the operation
    /// before extraction, otherwise a pre-existing file must already be recognized metadata. When
    /// <c>MetadataOnly</c> is set and the handler supports that mode, <paramref name="unpack"/> is never
    /// invoked and only the metadata is published; unsupported handlers fail instead of extracting files.
    /// <para>解包选项；<c>Overwrite</c> 关闭时已存在的元数据文件会在解包前使操作失败，否则已存在文件必须本身是可识别的元数据。
    /// 设置 <c>MetadataOnly</c> 且处理器支持该模式时不会调用 <paramref name="unpack"/>，仅发布元数据；
    /// 不支持的处理器会直接失败而不是写出文件。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Checked before extraction; cancellation is rethrown to the caller and never converted to a failure result.
    /// <para>解包前检查；取消异常会重新抛给调用方，绝不转换为失败结果。</para>
    /// </param>
    /// <param name="unpack">
    /// Extraction delegate to run; its failure result is returned without writing metadata.
    /// <para>要运行的解包委托；其失败结果会直接返回且不写入元数据。</para>
    /// </param>
    /// <param name="metadataRoot">
    /// Metadata root override forwarded to the write step; <see langword="null"/> uses ambient resolution.
    /// <para>转发给写入步骤的元数据根覆盖；<see langword="null"/> 表示使用环境解析。</para>
    /// </param>
    /// <returns>
    /// The extraction result after metadata is published on success; a failure result carrying the exception
    /// message when the pre-check or write step throws.
    /// <para>成功发布元数据后的解包结果；预检查或写入步骤抛出异常时返回携带异常信息的失败结果。</para>
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> is signaled before or during the operation.
    /// <para><paramref name="cancellationToken"/> 在操作前或操作中被触发。</para>
    /// </exception>
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
    /// <param name="handler">
    /// Format handler whose name and source metadata are published with the record.
    /// <para>其格式名与来源元数据会随记录一起发布的格式处理器。</para>
    /// </param>
    /// <param name="sourcePath">
    /// Source image path recorded in the metadata. <para>记录进元数据的源镜像路径。</para>
    /// </param>
    /// <param name="outputDirectory">
    /// Extraction output directory. <para>解包输出目录。</para>
    /// </param>
    /// <param name="options">
    /// Unpack options; when <c>Overwrite</c> is disabled an existing metadata file fails the operation
    /// before extraction, otherwise a pre-existing file must already be recognized metadata. When
    /// <c>MetadataOnly</c> is set and the handler supports that mode, <paramref name="unpack"/> is never
    /// invoked and only the metadata is published; unsupported handlers fail instead of extracting files.
    /// <para>解包选项；<c>Overwrite</c> 关闭时已存在的元数据文件会在解包前使操作失败，否则已存在文件必须本身是可识别的元数据。
    /// 设置 <c>MetadataOnly</c> 且处理器支持该模式时不会调用 <paramref name="unpack"/>，仅发布元数据；
    /// 不支持的处理器会直接失败而不是写出文件。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Checked before extraction; cancellation is rethrown to the caller and never converted to a failure result.
    /// <para>解包前检查；取消异常会重新抛给调用方，绝不转换为失败结果。</para>
    /// </param>
    /// <param name="unpack">
    /// Extraction delegate to run; its failure result is returned without writing metadata.
    /// <para>要运行的解包委托；其失败结果会直接返回且不写入元数据。</para>
    /// </param>
    /// <param name="context">
    /// Metadata context captured at call time; an explicit context decides the write root and never
    /// consults <see cref="DefaultMetadataRoot"/> after awaits.
    /// <para>在调用时固定的元数据上下文；显式上下文决定写入根，await 之后绝不读取 <see cref="DefaultMetadataRoot"/>。</para>
    /// </param>
    /// <param name="metadataRoot">
    /// Metadata root override consulted only when the context is unspecified.
    /// <para>仅在上下文未指定时参考的元数据根覆盖。</para>
    /// </param>
    /// <returns>
    /// The extraction result after metadata is published on success; a failure result carrying the exception
    /// message when the pre-check or write step throws.
    /// <para>成功发布元数据后的解包结果；预检查或写入步骤抛出异常时返回携带异常信息的失败结果。</para>
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> is signaled before or during the operation.
    /// <para><paramref name="cancellationToken"/> 在操作前或操作中被触发。</para>
    /// </exception>
    public static async Task<FirmwareOpResult> UnpackAsync(IReadonlyFormatHandler handler, string sourcePath,
        string outputDirectory, UnpackOptions? options, CancellationToken cancellationToken, Func<Task<FirmwareOpResult>> unpack,
        RepackMetadataContext context, string? metadataRoot = null)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metadataOnly = options?.MetadataOnly ?? false;
            if (metadataOnly && handler is not IRepackMetadataProvider { SupportsMetadataOnlyUnpack: true })
            {
                return FirmwareOpResult.Fail($"{handler.FormatName} cannot publish repack metadata without extracting its partition files.");
            }

            var metadataPath = Path.Combine(outputDirectory, FileName);
            if (File.Exists(metadataPath) && (options is { Overwrite: false } || !IsMetadataFile(metadataPath)))
                throw new IOException($"Repack metadata output already exists: {metadataPath}");
            if (!metadataOnly)
            {
                var result = await unpack().ConfigureAwait(false);
                if (!result.Success) return result;
                await WriteAsync(handler, sourcePath, outputDirectory, context, cancellationToken, result.Metadata, metadataRoot).ConfigureAwait(false);
                return result;
            }

            await WriteAsync(handler, sourcePath, outputDirectory, context, cancellationToken, null, metadataRoot).ConfigureAwait(false);
            return FirmwareOpResult.Ok($"Published repack metadata for {Path.GetFileName(sourcePath)} without extracting partition files.");
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
    /// <param name="handler">
    /// Format handler whose name and source metadata are published with the record.
    /// <para>其格式名与来源元数据会随记录一起发布的格式处理器。</para>
    /// </param>
    /// <param name="sourcePath">
    /// Source image path recorded in the metadata. <para>记录进元数据的源镜像路径。</para>
    /// </param>
    /// <param name="outputDirectory">
    /// Extraction output directory; the metadata file is placed here unless a root applies.
    /// <para>解包输出目录；未应用元数据根时元数据文件写入此目录。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels before and during publication; a cancelled operation leaves the previous target untouched.
    /// <para>在发布前与发布中取消；被取消的操作不会改动原有目标文件。</para>
    /// </param>
    /// <param name="extractionProperties">
    /// Additional key/value pairs merged over the handler-reported source properties;
    /// <see langword="null"/> adds none. <para>合并到处理器所报来源属性之上的附加键值对；<see langword="null"/> 表示不追加。</para>
    /// </param>
    /// <param name="metadataRoot">
    /// Metadata root override; when <see langword="null"/> the ambient <see cref="DefaultMetadataRoot"/> applies.
    /// <para>元数据根覆盖；为 <see langword="null"/> 时应用环境 <see cref="DefaultMetadataRoot"/>。</para>
    /// </param>
    /// <returns>
    /// A task completed once the metadata JSON is published. <para>元数据 JSON 发布完成后结束的任务。</para>
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> is signaled. <para><paramref name="cancellationToken"/> 被触发。</para>
    /// </exception>
    /// <exception cref="IOException">
    /// The target already holds a non-metadata file reserved for repack metadata, or publishing fails.
    /// <para>目标位置已有被打包元数据保留的非元数据文件，或发布失败。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The serialized metadata exceeds 32 MiB. <para>序列化后的元数据超过 32 MiB。</para>
    /// </exception>
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
    /// <param name="handler">
    /// Format handler whose name and source metadata are published with the record.
    /// <para>其格式名与来源元数据会随记录一起发布的格式处理器。</para>
    /// </param>
    /// <param name="sourcePath">
    /// Source image path recorded in the metadata. <para>记录进元数据的源镜像路径。</para>
    /// </param>
    /// <param name="outputDirectory">
    /// Extraction output directory; the metadata file is placed here unless a root applies.
    /// <para>解包输出目录；未应用元数据根时元数据文件写入此目录。</para>
    /// </param>
    /// <param name="context">
    /// Metadata context captured at call time; it resolves the write root and is never re-evaluated after awaits.
    /// <para>在调用时固定的元数据上下文；由它解析写入根，await 之后绝不重新求值。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels before and during publication; a cancelled operation leaves the previous target untouched.
    /// <para>在发布前与发布中取消；被取消的操作不会改动原有目标文件。</para>
    /// </param>
    /// <param name="extractionProperties">
    /// Additional key/value pairs merged over the handler-reported source properties;
    /// <see langword="null"/> adds none. <para>合并到处理器所报来源属性之上的附加键值对；<see langword="null"/> 表示不追加。</para>
    /// </param>
    /// <param name="metadataRoot">
    /// Metadata root consulted only when the context is unspecified.
    /// <para>仅在上下文未指定时参考的元数据根。</para>
    /// </param>
    /// <returns>
    /// A task completed once the metadata JSON is published. <para>元数据 JSON 发布完成后结束的任务。</para>
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> is signaled. <para><paramref name="cancellationToken"/> 被触发。</para>
    /// </exception>
    /// <exception cref="IOException">
    /// The target already holds a non-metadata file reserved for repack metadata, or publishing fails.
    /// <para>目标位置已有被打包元数据保留的非元数据文件，或发布失败。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The serialized metadata exceeds 32 MiB. <para>序列化后的元数据超过 32 MiB。</para>
    /// </exception>
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
        if (extractionProperties is not null)
        {
            foreach (var pair in extractionProperties)
            {
                metadata.SourceProperties[pair.Key] = pair.Value;
            }
        }

        var provider = handler as IRepackMetadataProvider;
        provider?.ConfigureRepackMetadata(metadata, sourcePath, outputDirectory);
        metadata.PackingNotes = (metadata.CanPackDirectory
            ? "Use packingOptions and configuration. Source properties are descriptive; other fields are not automatically restored."
            : "The current handler cannot rebuild this extraction directory. A format-specific packing workflow is required.")
            + metadata.PackingNotes;
        if (!source.Success)
        {
            metadata.PackingNotes += " Source metadata could not be read: " + source.Summary;
        }

        var partition = DetectPartitionName(Path.GetFileName(sourcePath));
        var directory = string.IsNullOrWhiteSpace(effectiveRoot) ? outputDirectory : Path.Combine(effectiveRoot, partition);
        if (provider is not null)
        {
            await provider.CaptureRepackMetadataAsync(metadata, sourcePath, outputDirectory, directory, cancellationToken).ConfigureAwait(false);
        }
        await metadata.SaveAsync(directory, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Atomically saves bounded directory metadata.<para>原子保存有大小限制的目录元数据。</para></summary>
    /// <param name="outputDirectory">
    /// Directory receiving <see cref="FileName"/>; it is created when missing.
    /// <para>接收 <see cref="FileName"/> 的目录；不存在时会被创建。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels serialization or the final move; a cancelled operation leaves the previous target file intact.
    /// <para>取消序列化或最终移动；被取消的操作保留原有目标文件。</para>
    /// </param>
    /// <returns>
    /// A task completed after the temporary file has replaced the target in a single move.
    /// <para>临时文件经单次移动替换目标文件后结束的任务。</para>
    /// </returns>
    /// <exception cref="IOException">
    /// The target exists and is not recognized metadata, or the write or move fails.
    /// <para>目标已存在且不是可识别的元数据，或写入或移动失败。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The serialized metadata exceeds 32 MiB. <para>序列化后的元数据超过 32 MiB。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> is signaled. <para><paramref name="cancellationToken"/> 被触发。</para>
    /// </exception>
    public async Task SaveAsync(string outputDirectory, CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(Path.Combine(outputDirectory, FileName));
        if (File.Exists(path) && !IsMetadataFile(path))
            throw new IOException("The output contains a file reserved for repack metadata.");
        Directory.CreateDirectory(outputDirectory);
        var temporary = TempFileHelper.CreateAdjacentPath(path);
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(output, this, RepackJsonContext.Default.RepackMetadata, cancellationToken).ConfigureAwait(false);
                if (output.Length > MaximumBytes) throw new InvalidDataException("Repack metadata exceeds 32 MiB.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            AtomicPublish.Publish(temporary, path);
        }
        finally { File.Delete(temporary); }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RepackMetadata))]
internal partial class RepackJsonContext : JsonSerializerContext;