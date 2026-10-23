using System.Buffers;
using System.Text;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Projects.Schemes;

/// <summary>
/// Availability of one declared scheme asset.
/// <para>单个声明 Scheme 资产的可用状态。</para>
/// </summary>
public enum SchemeAssetState
{
    /// <summary>The asset was not inspected yet. <para>尚未检查该资产。</para></summary>
    NotChecked,

    /// <summary>The asset exists and matches its declared length and digest. <para>资产存在且与其声明的长度和摘要一致。</para></summary>
    Valid,

    /// <summary>The asset file does not exist. <para>资产文件不存在。</para></summary>
    Missing,

    /// <summary>The asset length differs from its declared length. <para>资产长度与声明长度不一致。</para></summary>
    LengthMismatch,

    /// <summary>The asset digest differs from its declared digest. <para>资产摘要与声明摘要不一致。</para></summary>
    HashMismatch,

    /// <summary>The declared relative path escapes the scheme directory or is malformed.
    /// <para>声明的相对路径越出 Scheme 目录或格式非法。</para></summary>
    UnsafePath,

    /// <summary>The asset exceeds the per-file size bound. <para>资产超过单文件大小上限。</para></summary>
    TooLarge
}

/// <summary>
/// Result of inspecting one declared scheme asset without copying it.
/// <para>不复制文件的情况下检查单个声明 Scheme 资产的结果。</para>
/// </summary>
public sealed class SchemeAssetCheck
{
    /// <summary>Identifier of the inspected asset. <para>被检查资产的标识符。</para></summary>
    /// <value>The declared <see cref="SchemeAsset.Id"/>. <para>声明的 <see cref="SchemeAsset.Id"/>。</para></value>
    public required string AssetId { get; init; }

    /// <summary>Availability of the asset. <para>资产的可用状态。</para></summary>
    /// <value>One of the <see cref="SchemeAssetState"/> values. <para><see cref="SchemeAssetState"/> 中的一个值。</para></value>
    public required SchemeAssetState State { get; init; }

    /// <summary>Absolute source path considered for the asset. <para>为该资产考虑的绝对源路径。</para></summary>
    /// <value>The resolved path, or <see langword="null"/> when the declaration cannot be resolved.
    /// <para>解析得到的路径；声明无法解析时为 <see langword="null"/>。</para></value>
    public string? SourcePath { get; init; }

    /// <summary>Technical failure description, or <see langword="null"/> when the asset is valid.
    /// <para>技术性失败说明；资产有效时为 <see langword="null"/>。</para></summary>
    /// <value>One English sentence describing why the asset is unusable.
    /// <para>一句说明资产不可用原因的英文。</para></value>
    public string? Error { get; init; }

    /// <summary>Whether the asset can be materialized. <para>资产是否可以被物化。</para></summary>
    /// <value><see langword="true"/> when <see cref="State"/> is <see cref="SchemeAssetState.Valid"/>.
    /// <para><see cref="State"/> 为 <see cref="SchemeAssetState.Valid"/> 时为 <see langword="true"/>。</para></value>
    public bool IsValid => State == SchemeAssetState.Valid;
}

/// <summary>
/// Copied project asset together with its absolute destination path.
/// <para>已复制的工程资产及其绝对目标路径。</para>
/// </summary>
/// <param name="Asset">Asset declaration carrying the digest and length of the copied bytes.
/// <para>携带已复制字节的摘要与长度的资产声明。</para></param>
/// <param name="Path">Absolute path of the copied file. <para>已复制文件的绝对路径。</para></param>
public sealed record SchemeAssetImport(SchemeAsset Asset, string Path);

/// <summary>
/// Copies, validates and resolves declared scheme assets into a project or run directory.
/// <para>把声明的 Scheme 资产复制、校验并解析到工程目录或运行目录。</para>
/// </summary>
/// <remarks>
/// Every declared asset is addressed by a relative, non-escaping path. Copies are bounded per file and in total,
/// staged beside the destination and published with an atomic rename; a same-named file with different content is
/// never overwritten. A store created with delete-on-dispose removes the destination directory it owns.
/// <para>每个声明资产都以不可越界的相对路径寻址。复制受单文件与总量上限约束，先暂存在目标同目录再原子改名发布；
/// 同名但内容不同的文件绝不会被覆盖。以释放时删除方式创建的存储会移除其拥有的目标目录。</para>
/// </remarks>
public sealed class SchemeAssetStore : IDisposable
{
    /// <summary>Maximum number of assets one scheme may declare. <para>单个 Scheme 允许声明的资产数量上限。</para></summary>
    public const int MaximumAssetCount = 256;

    /// <summary>Maximum size of one asset file in bytes. <para>单个资产文件的字节上限。</para></summary>
    public const long MaximumAssetLength = 512L * 1024 * 1024;

    /// <summary>Maximum total size of all materialized assets in bytes. <para>全部已物化资产的字节总量上限。</para></summary>
    public const long MaximumTotalLength = 2L * 1024 * 1024 * 1024;

    private const int CopyBufferSize = 81920;
    private const int MaximumRelativePathLength = 512;
    private const int MaximumFileNameLength = 128;

    private readonly bool _deleteRootOnDispose;
    private bool _disposed;

    private SchemeAssetStore(string root, Dictionary<string, string> paths, bool deleteRootOnDispose)
    {
        Root = root;
        Paths = paths;
        _deleteRootOnDispose = deleteRootOnDispose;
    }

    /// <summary>Absolute directory that received the materialized assets. <para>接收已物化资产的绝对目录。</para></summary>
    /// <value>The destination directory passed to <see cref="MaterializeAsync"/>.
    /// <para>传给 <see cref="MaterializeAsync"/> 的目标目录。</para></value>
    public string Root { get; }

    /// <summary>Absolute paths keyed by declared asset id. <para>按声明资产 ID 索引的绝对路径。</para></summary>
    /// <value>One entry per materialized asset; ordinal keys.
    /// <para>每个已物化资产一条记录；键按序数比较。</para></value>
    public IReadOnlyDictionary<string, string> Paths { get; }

    /// <summary>
    /// Resolves a declared asset id to its materialized absolute path.
    /// <para>把声明的资产 ID 解析为已物化的绝对路径。</para>
    /// </summary>
    /// <param name="assetId">Declared asset id to look up. <para>要查找的声明资产 ID。</para></param>
    /// <returns>The absolute path, or <see langword="null"/> when the asset was not materialized.
    /// <para>绝对路径；资产未被物化时为 <see langword="null"/>。</para></returns>
    public string? Resolve(string? assetId) =>
        assetId is not null && Paths.TryGetValue(assetId, out var path) ? path : null;

    /// <summary>
    /// Gets the project-owned asset directory <c>.uotan/scheme-assets/&lt;scheme-id&gt;</c>.
    /// <para>获取工程拥有的资产目录 <c>.uotan/scheme-assets/&lt;scheme-id&gt;</c>。</para>
    /// </summary>
    /// <param name="stateDirectory">The sub-project <c>.uotan</c> directory. <para>子项目的 <c>.uotan</c> 目录。</para></param>
    /// <param name="schemeId">Scheme identifier used as the directory name; unsafe characters are replaced.
    /// <para>用作目录名的 Scheme 标识符；不安全的字符会被替换。</para></param>
    /// <returns>Absolute asset directory path; the directory is not created.
    /// <para>绝对资产目录路径；不会创建目录。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="stateDirectory"/> is empty or only whitespace.
    /// <para><paramref name="stateDirectory"/> 为空或仅包含空白。</para></exception>
    public static string GetProjectAssetDirectory(string stateDirectory, string schemeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        return Path.Combine(Path.GetFullPath(stateDirectory), "scheme-assets", SanitizeSegment(schemeId));
    }

    /// <summary>
    /// Creates a unique run asset directory below the supplied parent or the host temp directory.
    /// <para>在给定父目录或主机临时目录下创建唯一的运行资产目录。</para>
    /// </summary>
    /// <param name="parentDirectory">Parent directory, or <see langword="null"/> to use the host temp directory.
    /// <para>父目录；为 <see langword="null"/> 时使用主机临时目录。</para></param>
    /// <param name="schemeId">Scheme identifier embedded in the directory name. <para>嵌入目录名的 Scheme 标识符。</para></param>
    /// <returns>The created absolute directory path. <para>已创建的绝对目录路径。</para></returns>
    public static string CreateRunDirectory(string? parentDirectory, string schemeId)
    {
        var parent = string.IsNullOrWhiteSpace(parentDirectory)
            ? Path.Combine(Path.GetTempPath(), "UotanStudio", "scheme-assets")
            : Path.GetFullPath(parentDirectory);
        var directory = Path.Combine(parent,
            SanitizeSegment(schemeId) + "-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// Converts an absolute file path into a relative path contained by a directory.
    /// <para>把绝对文件路径转换为某个目录内包含的相对路径。</para>
    /// </summary>
    /// <param name="directory">Containing directory. <para>包含目录。</para></param>
    /// <param name="filePath">Absolute file path to shorten. <para>要缩短的绝对文件路径。</para></param>
    /// <returns>A forward-slash relative path, or <see langword="null"/> when the file is outside the directory.
    /// <para>正斜杠相对路径；文件位于目录之外时为 <see langword="null"/>。</para></returns>
    public static string? TryGetContainedRelativePath(string directory, string filePath)
    {
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var full = Path.GetFullPath(filePath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, comparison))
        {
            return null;
        }
        var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
        if (relative.Length == 0 || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            return null;
        }
        return relative;
    }

    /// <summary>
    /// Inspects every declared asset against a source directory without copying anything.
    /// <para>在源目录中检查全部声明资产，不复制任何文件。</para>
    /// </summary>
    /// <param name="package">Scheme whose declared assets are inspected. <para>要检查其声明资产的 Scheme。</para></param>
    /// <param name="sourceDirectory">Directory the declared relative paths resolve against, normally the scheme directory.
    /// <para>声明相对路径所基于的目录，通常是 Scheme 目录。</para></param>
    /// <param name="fallbackDirectory">Optional second directory tried when the file is absent from the primary directory.
    /// <para>主目录中缺少文件时尝试的可选第二目录。</para></param>
    /// <param name="cancellationToken">Cancels hashing and file probing. <para>取消哈希计算与文件探测。</para></param>
    /// <returns>One check per declared asset, in declaration order.
    /// <para>按声明顺序为每个声明资产返回一条检查结果。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="package"/> is <see langword="null"/>.
    /// <para><paramref name="package"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="sourceDirectory"/> is empty or only whitespace.
    /// <para><paramref name="sourceDirectory"/> 为空或仅包含空白。</para></exception>
    /// <exception cref="InvalidDataException">The scheme declares more than <see cref="MaximumAssetCount"/> assets.
    /// <para>Scheme 声明的资产超过 <see cref="MaximumAssetCount"/> 个。</para></exception>
    public static async Task<IReadOnlyList<SchemeAssetCheck>> InspectAsync(SchemePackage package,
        string sourceDirectory, string? fallbackDirectory = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        package.Normalize();
        EnsureAssetCount(package);
        List<SchemeAssetCheck> checks = new(package.Assets.Count);
        foreach (var asset in package.Assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            checks.Add(await CheckSourceAsync(asset, sourceDirectory, fallbackDirectory, cancellationToken)
                .ConfigureAwait(false));
        }
        return checks;
    }

    /// <summary>
    /// Verifies declared assets against an explicit asset-id to path map.
    /// <para>按显式的资产 ID 到路径映射校验声明资产。</para>
    /// </summary>
    /// <param name="package">Scheme whose declared assets are verified. <para>要校验其声明资产的 Scheme。</para></param>
    /// <param name="paths">Absolute or relative file paths keyed by declared asset id.
    /// <para>按声明资产 ID 索引的绝对或相对文件路径。</para></param>
    /// <param name="cancellationToken">Cancels hashing. <para>取消哈希计算。</para></param>
    /// <returns>One check per declared asset, in declaration order.
    /// <para>按声明顺序为每个声明资产返回一条检查结果。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="package"/> or <paramref name="paths"/> is <see langword="null"/>.
    /// <para><paramref name="package"/> 或 <paramref name="paths"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">The scheme declares more than <see cref="MaximumAssetCount"/> assets.
    /// <para>Scheme 声明的资产超过 <see cref="MaximumAssetCount"/> 个。</para></exception>
    public static async Task<IReadOnlyList<SchemeAssetCheck>> ValidateAsync(SchemePackage package,
        IReadOnlyDictionary<string, string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(paths);
        package.Normalize();
        EnsureAssetCount(package);
        List<SchemeAssetCheck> checks = new(package.Assets.Count);
        foreach (var asset in package.Assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!paths.TryGetValue(asset.Id, out var path) || string.IsNullOrWhiteSpace(path))
            {
                checks.Add(new SchemeAssetCheck
                {
                    AssetId = asset.Id,
                    State = SchemeAssetState.Missing,
                    Error = $"Scheme asset '{asset.Id}' has no materialized file path."
                });
                continue;
            }
            try
            {
                checks.Add(await CheckFileAsync(asset, Path.GetFullPath(path), cancellationToken)
                    .ConfigureAwait(false));
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException
                                              or PathTooLongException)
            {
                checks.Add(new SchemeAssetCheck
                {
                    AssetId = asset.Id,
                    State = SchemeAssetState.Missing,
                    Error = $"Scheme asset '{asset.Id}' has an invalid file path: {error.Message}"
                });
            }
        }
        return checks;
    }

    /// <summary>
    /// Copies every declared asset into the destination directory after validating length and digest.
    /// <para>在校验长度与摘要后，把全部声明资产复制到目标目录。</para>
    /// </summary>
    /// <param name="package">Scheme whose assets are materialized. <para>要物化其资产的 Scheme。</para></param>
    /// <param name="sourceDirectory">Directory the declared relative paths resolve against, normally the scheme directory.
    /// <para>声明相对路径所基于的目录，通常是 Scheme 目录。</para></param>
    /// <param name="destinationDirectory">Directory that receives the copies; created when missing.
    /// <para>接收副本的目录；缺失时创建。</para></param>
    /// <param name="fallbackDirectory">Optional second source directory tried when the file is absent from the primary directory.
    /// <para>主目录中缺少文件时尝试的可选第二源目录。</para></param>
    /// <param name="deleteOnDispose">Whether disposing the store deletes <paramref name="destinationDirectory"/> and its content.
    /// <para>释放存储时是否删除 <paramref name="destinationDirectory"/> 及其内容。</para></param>
    /// <param name="cancellationToken">Cancels validation and copying; a partial destination is removed.
    /// <para>取消校验与复制；已产生的部分目标会被移除。</para></param>
    /// <returns>A store resolving each declared asset id to its absolute destination path.
    /// <para>把每个声明资产 ID 解析为绝对目标路径的存储。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="package"/> is <see langword="null"/>.
    /// <para><paramref name="package"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="sourceDirectory"/> or <paramref name="destinationDirectory"/> is empty or only whitespace.
    /// <para><paramref name="sourceDirectory"/> 或 <paramref name="destinationDirectory"/> 为空或仅包含空白。</para></exception>
    /// <exception cref="InvalidDataException">An asset is missing, malformed, too large, or would overwrite different content.
    /// <para>资产缺失、格式非法、超过上限，或会覆盖不同内容。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested while validating or copying.
    /// <para>在校验或复制期间请求了取消。</para></exception>
    public static async Task<SchemeAssetStore> MaterializeAsync(SchemePackage package, string sourceDirectory,
        string destinationDirectory, string? fallbackDirectory = null, bool deleteOnDispose = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        package.Normalize();
        EnsureAssetCount(package);
        var checks = await InspectAsync(package, sourceDirectory, fallbackDirectory, cancellationToken)
            .ConfigureAwait(false);
        var invalid = checks.FirstOrDefault(check => !check.IsValid);
        if (invalid is not null)
        {
            throw new InvalidDataException(invalid.Error ?? $"Scheme asset '{invalid.AssetId}' is not available.");
        }
        var root = Path.GetFullPath(destinationDirectory);
        var createdRoot = !Directory.Exists(root);
        Directory.CreateDirectory(root);
        Dictionary<string, string> paths = new(StringComparer.Ordinal);
        long totalLength = 0;
        try
        {
            for (var index = 0; index < package.Assets.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var asset = package.Assets[index];
                var check = checks[index];
                var relative = NormalizeRelativePath(asset.RelativePath);
                var target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!IsContained(root, target))
                {
                    throw new InvalidDataException($"Scheme asset '{asset.Id}' leaves the destination directory.");
                }
                if (File.Exists(target))
                {
                    var existing = await CheckFileAsync(asset, target, cancellationToken).ConfigureAwait(false);
                    if (!existing.IsValid
                        || !await FilesEqualAsync(check.SourcePath!, target, cancellationToken).ConfigureAwait(false))
                    {
                        throw new InvalidDataException(
                            $"Scheme asset '{asset.Id}' would overwrite different content at '{target}'.");
                    }
                    totalLength += new FileInfo(target).Length;
                }
                else
                {
                    var directory = Path.GetDirectoryName(target)
                        ?? throw new InvalidDataException($"Scheme asset '{asset.Id}' has no target directory.");
                    Directory.CreateDirectory(directory);
                    totalLength += await CopyAtomicAsync(asset.Id, check.SourcePath!, target, asset.Length,
                        asset.Sha256, cancellationToken).ConfigureAwait(false);
                }
                if (totalLength > MaximumTotalLength)
                {
                    throw new InvalidDataException(
                        $"Scheme assets exceed the total size limit of {MaximumTotalLength} bytes.");
                }
                paths[asset.Id] = target;
            }
            return new SchemeAssetStore(root, paths, deleteOnDispose);
        }
        catch
        {
            if (createdRoot)
            {
                TryDeleteDirectory(root);
            }
            throw;
        }
    }

    /// <summary>
    /// Copies one external file into a destination directory, replacing nothing with different content.
    /// <para>把一个外部文件复制到目标目录，绝不用不同内容替换已有文件。</para>
    /// </summary>
    /// <param name="sourcePath">Existing file to copy; must be a regular file within the size bound.
    /// <para>要复制的现有文件；必须是尺寸上限内的常规文件。</para></param>
    /// <param name="destinationDirectory">Directory that receives the copy; created when missing.
    /// <para>接收副本的目录；缺失时创建。</para></param>
    /// <param name="assetId">Identifier recorded on the returned declaration. <para>记录在返回声明上的标识符。</para></param>
    /// <param name="cancellationToken">Cancels copying. <para>取消复制。</para></param>
    /// <returns>The copied declaration, carrying the digest and length of the copied bytes, and the absolute path.
    /// <para>已复制的声明，携带已复制字节的摘要与长度，以及绝对路径。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="sourcePath"/>, <paramref name="destinationDirectory"/> or
    /// <paramref name="assetId"/> is empty or only whitespace.
    /// <para><paramref name="sourcePath"/>、<paramref name="destinationDirectory"/> 或 <paramref name="assetId"/> 为空或仅包含空白。</para></exception>
    /// <exception cref="FileNotFoundException"><paramref name="sourcePath"/> does not exist.
    /// <para><paramref name="sourcePath"/> 不存在。</para></exception>
    /// <exception cref="InvalidDataException">The file exceeds <see cref="MaximumAssetLength"/> or the target holds different content.
    /// <para>文件超过 <see cref="MaximumAssetLength"/>，或目标位置存放了不同内容。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested while copying.
    /// <para>在复制期间请求了取消。</para></exception>
    public static async Task<SchemeAssetImport> ImportAsync(string sourcePath, string destinationDirectory,
        string assetId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);
        var source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("Scheme asset source file does not exist.", source);
        }
        if (new FileInfo(source).Length > MaximumAssetLength)
        {
            throw new InvalidDataException($"Scheme asset '{assetId}' exceeds the size limit.");
        }
        var root = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(root);
        var fileName = SanitizeFileName(Path.GetFileName(source));
        var target = Path.GetFullPath(Path.Combine(root, fileName));
        if (!IsContained(root, target))
        {
            throw new InvalidDataException($"Scheme asset '{assetId}' has an unusable file name.");
        }
        long length;
        if (File.Exists(target))
        {
            if (!await FilesEqualAsync(source, target, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException(
                    $"Scheme asset '{assetId}' would overwrite different content at '{target}'.");
            }
            length = new FileInfo(target).Length;
        }
        else
        {
            length = await CopyAtomicAsync(assetId, source, target, 0, null, cancellationToken).ConfigureAwait(false);
        }
        var digest = await ComputeSha256Async(target, cancellationToken).ConfigureAwait(false);
        var asset = new SchemeAsset
        {
            Id = assetId,
            Name = Path.GetFileNameWithoutExtension(fileName),
            Sha256 = digest,
            Length = length
        };
        return new SchemeAssetImport(asset, target);
    }

    private static async Task<SchemeAssetCheck> CheckSourceAsync(SchemeAsset asset, string sourceDirectory,
        string? fallbackDirectory, CancellationToken cancellationToken)
    {
        string relative;
        try
        {
            relative = NormalizeRelativePath(asset.RelativePath);
        }
        catch (InvalidDataException error)
        {
            return new SchemeAssetCheck
            {
                AssetId = asset.Id,
                State = SchemeAssetState.UnsafePath,
                Error = error.Message
            };
        }
        var primary = Path.GetFullPath(Path.Combine(sourceDirectory,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsContained(Path.GetFullPath(sourceDirectory), primary))
        {
            return new SchemeAssetCheck
            {
                AssetId = asset.Id,
                State = SchemeAssetState.UnsafePath,
                SourcePath = primary,
                Error = $"Scheme asset '{asset.Id}' leaves the scheme directory."
            };
        }
        var resolved = primary;
        if (!File.Exists(resolved) && !string.IsNullOrWhiteSpace(fallbackDirectory))
        {
            var fallbackRoot = Path.GetFullPath(fallbackDirectory);
            var candidate = Path.GetFullPath(Path.Combine(fallbackRoot,
                relative.Replace('/', Path.DirectorySeparatorChar)));
            if (IsContained(fallbackRoot, candidate) && File.Exists(candidate))
            {
                resolved = candidate;
            }
        }
        return await CheckFileAsync(asset, resolved, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<SchemeAssetCheck> CheckFileAsync(SchemeAsset asset, string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new SchemeAssetCheck
            {
                AssetId = asset.Id,
                State = SchemeAssetState.Missing,
                SourcePath = path,
                Error = $"Scheme asset '{asset.Id}' is missing at '{path}'."
            };
        }
        var length = new FileInfo(path).Length;
        if (length > MaximumAssetLength)
        {
            return new SchemeAssetCheck
            {
                AssetId = asset.Id,
                State = SchemeAssetState.TooLarge,
                SourcePath = path,
                Error = $"Scheme asset '{asset.Id}' exceeds the size limit of {MaximumAssetLength} bytes."
            };
        }
        if (asset.Length > 0 && length != asset.Length)
        {
            return new SchemeAssetCheck
            {
                AssetId = asset.Id,
                State = SchemeAssetState.LengthMismatch,
                SourcePath = path,
                Error = $"Scheme asset '{asset.Id}' is {length} bytes but declares {asset.Length} bytes."
            };
        }
        if (!string.IsNullOrWhiteSpace(asset.Sha256))
        {
            var declared = asset.Sha256.Trim();
            if (!IsCanonicalSha256(declared))
            {
                return new SchemeAssetCheck
                {
                    AssetId = asset.Id,
                    State = SchemeAssetState.HashMismatch,
                    SourcePath = path,
                    Error = $"Scheme asset '{asset.Id}' declares a malformed SHA-256 digest."
                };
            }
            var actual = await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actual, declared, StringComparison.OrdinalIgnoreCase))
            {
                return new SchemeAssetCheck
                {
                    AssetId = asset.Id,
                    State = SchemeAssetState.HashMismatch,
                    SourcePath = path,
                    Error = $"Scheme asset '{asset.Id}' does not match its declared SHA-256 digest."
                };
            }
        }
        return new SchemeAssetCheck { AssetId = asset.Id, State = SchemeAssetState.Valid, SourcePath = path };
    }

    private static async Task<long> CopyAtomicAsync(string assetId, string sourcePath, string targetPath,
        long declaredLength, string? declaredSha256, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidDataException($"Scheme asset '{assetId}' has no target directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory,
            "." + Path.GetFileName(targetPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            long copied = 0;
            var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
            try
            {
                using var hash = Sha256.CreateAccumulator();
                await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                                 FileShare.Read, CopyBufferSize,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var staged = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                                 FileShare.None, CopyBufferSize, FileOptions.Asynchronous))
                {
                    int read;
                    while ((read = await source.ReadAsync(buffer.AsMemory(0, CopyBufferSize), cancellationToken)
                               .ConfigureAwait(false)) > 0)
                    {
                        copied += read;
                        if (copied > MaximumAssetLength)
                        {
                            throw new InvalidDataException(
                                $"Scheme asset '{assetId}' exceeds the size limit of {MaximumAssetLength} bytes.");
                        }
                        if (declaredLength > 0 && copied > declaredLength)
                        {
                            throw new InvalidDataException(
                                $"Scheme asset '{assetId}' exceeds its declared length of {declaredLength} bytes.");
                        }
                        hash.Append(buffer.AsSpan(0, read));
                        await staged.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    }
                    await staged.FlushAsync(cancellationToken).ConfigureAwait(false);
                    staged.Flush(flushToDisk: true);
                }
                if (declaredLength > 0 && copied != declaredLength)
                {
                    throw new InvalidDataException(
                        $"Scheme asset '{assetId}' is {copied} bytes but declares {declaredLength} bytes.");
                }
                if (!string.IsNullOrWhiteSpace(declaredSha256))
                {
                    var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
                    if (!string.Equals(actual, declaredSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            $"Scheme asset '{assetId}' does not match its declared SHA-256 digest.");
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
            try
            {
                File.Move(temporary, targetPath);
            }
            catch (IOException) when (File.Exists(targetPath))
            {
                if (!await FilesEqualAsync(sourcePath, targetPath, cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidDataException(
                        $"Scheme asset '{assetId}' would overwrite different content at '{targetPath}'.");
                }
            }
            return copied;
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    private static async Task<bool> FilesEqualAsync(string leftPath, string rightPath,
        CancellationToken cancellationToken)
    {
        if (new FileInfo(leftPath).Length != new FileInfo(rightPath).Length)
        {
            return false;
        }
        var left = await ComputeSha256Async(leftPath, cancellationToken).ConfigureAwait(false);
        var right = await ComputeSha256Async(rightPath, cancellationToken).ConfigureAwait(false);
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            using var hash = Sha256.CreateAccumulator();
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            long total = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, CopyBufferSize), cancellationToken)
                       .ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > MaximumAssetLength)
                {
                    throw new InvalidDataException(
                        $"Scheme asset at '{path}' exceeds the size limit of {MaximumAssetLength} bytes.");
                }
                hash.Append(buffer.AsSpan(0, read));
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static string NormalizeRelativePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidDataException("Scheme asset declares no relative path.");
        }
        var candidate = relativePath.Trim().Replace('\\', '/');
        if (candidate.Length > MaximumRelativePathLength || Path.IsPathRooted(relativePath)
            || candidate.StartsWith('/') || candidate.Contains(':') || candidate.Contains('\0'))
        {
            throw new InvalidDataException($"Scheme asset path '{relativePath}' must stay inside the scheme directory.");
        }
        var segments = candidate.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            throw new InvalidDataException($"Scheme asset path '{relativePath}' must stay inside the scheme directory.");
        }
        return string.Join('/', segments);
    }

    private static string SanitizeFileName(string? fileName)
    {
        var name = Path.GetFileName(fileName ?? string.Empty);
        StringBuilder builder = new(Math.Min(name.Length, MaximumFileNameLength));
        foreach (var character in name)
        {
            if (builder.Length >= MaximumFileNameLength)
            {
                break;
            }
            builder.Append(Path.GetInvalidFileNameChars().Contains(character) ? '_' : character);
        }
        var result = builder.ToString().Trim();
        return result.Length == 0 || result is "." or ".." ? "asset.bin" : result;
    }

    private static string SanitizeSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "scheme";
        }
        StringBuilder builder = new(Math.Min(value.Length, 64));
        foreach (var character in value.Trim())
        {
            if (builder.Length >= 64)
            {
                break;
            }
            builder.Append(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'
                ? char.ToLowerInvariant(character)
                : '_');
        }
        var result = builder.ToString().Trim('.', '_', '-');
        return result.Length == 0 ? "scheme" : result;
    }

    private static bool IsCanonicalSha256(string value)
    {
        if (value.Length != 64)
        {
            return false;
        }
        foreach (var character in value)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsContained(string root, string path)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.Length > normalizedRoot.Length
            && path.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
    }

    private static void EnsureAssetCount(SchemePackage package)
    {
        if (package.Assets.Count > MaximumAssetCount)
        {
            throw new InvalidDataException(
                $"A scheme may declare at most {MaximumAssetCount} assets.");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ProjectDiagnostics.Write(ProjectLogLevel.Warn, "Scheme", error.Message);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ProjectDiagnostics.Write(ProjectLogLevel.Warn, "Scheme", error.Message);
        }
    }

    /// <summary>
    /// Deletes the destination directory when the store owns it.
    /// <para>存储拥有目标目录时删除该目录。</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_deleteRootOnDispose)
        {
            TryDeleteDirectory(Root);
        }
    }
}