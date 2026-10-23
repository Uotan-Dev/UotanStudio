using System.Text.Json.Serialization;

namespace UotanStudio.Projects;

/// <summary>
/// Identity of one portable patch resource carried by two complementary path forms: an absolute local
/// authoring source that is never written to JSON, and a portable project anchor that survives moving the project.
/// <para>单个可移植补丁资源的身份信息，由两种互补的路径形式共同表达：从不写入 JSON 的本地绝对编辑源，以及在项目移动后依然有效的可移植项目锚点。</para>
/// </summary>
public sealed class VisualPluginAsset
{
    /// <summary>
    /// Stable portable resource identifier.
    /// <para>稳定的可移植资源标识符。</para>
    /// </summary>
    /// <value>
    /// Manifest string matched ordinally and case-sensitively when a resource is looked up.
    /// <para>清单中的字符串；查找资源时按逐字符、区分大小写的方式匹配。</para>
    /// </value>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Display filename, not an extraction destination.
    /// <para>显示用文件名，不是解包目标路径。</para>
    /// </summary>
    /// <value>
    /// File name captured from the source path; it never carries directory information and is not used to place bytes on disk.
    /// <para>从源路径捕获的文件名；不携带目录信息，也不用于决定字节落盘的位置。</para>
    /// </value>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Exact uncompressed length.
    /// <para>精确的未压缩长度。</para>
    /// </summary>
    /// <value>
    /// Byte count recorded when the identity was captured; every copy must produce exactly this many bytes and no more.
    /// <para>捕获身份时记录的字节数；每次复制必须恰好产生这么多字节且不能更多。</para>
    /// </value>
    public long Length { get; set; }

    /// <summary>
    /// SHA-256 identity in hexadecimal.
    /// <para>十六进制表示的 SHA-256 身份。</para>
    /// </summary>
    /// <value>
    /// Lowercase hex digest of the resource content; comparisons during copies ignore case, and bundle entry names use its lowercase form.
    /// <para>资源内容的小写十六进制摘要；复制时的比较不区分大小写，包内条目名使用其小写形式。</para>
    /// </value>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>
    /// Trusted local authoring source; ignored during serialization and deserialization.
    /// <para>受信任的本地编辑源；序列化和反序列化时被忽略。</para>
    /// </summary>
    /// <value>
    /// Full path resolved at identity-capture time, or <see langword="null"/> when the resource lives only inside a bundle and reads go through <c>BundlePath</c> instead.
    /// <para>捕获身份时解析出的完整路径；当资源仅存在于包内、读取需经由 <c>BundlePath</c> 时为 <see langword="null"/>。</para>
    /// </value>
    [JsonIgnore]
    public string? SourcePath { get; private set; }

    /// <summary>
    /// Portable project anchor: the resource path relative to the project root directory.
    /// <para>可移植项目锚点：资源相对项目根目录的路径。</para>
    /// </summary>
    /// <value>
    /// Root-relative path with <c>/</c> separators that is serialized with the resource and re-resolved against
    /// whichever project root is open, so a moved project keeps its own resources attached; <see langword="null"/>
    /// when the resource has no location inside a project, as for resources read out of a bundle.
    /// <para>使用 <c>/</c> 分隔符的相对根目录路径，随资源一并序列化，并在每次打开项目时按当时的项目根重新解析，
    /// 因此项目被移动后仍能挂上属于自己的资源；资源没有项目内位置时（例如从包内读取的资源）为 <see langword="null"/>。</para>
    /// </value>
    public string? RelativePath { get; set; }

    /// <summary>
    /// Runs Snapshot.
    /// <para>执行 Snapshot。</para>
    /// </summary>
    public VisualPluginAsset Snapshot() => new()
    {
        Id = Id,
        Name = Name,
        Length = Length,
        Sha256 = Sha256,
        SourcePath = SourcePath,
        RelativePath = RelativePath
    };

    /// <summary>Anchors a project-owned resource to its project root while keeping its absolute authoring source.
    /// <para>在保留绝对编辑源的同时，把项目内的资源锚定到其项目根目录。</para></summary>
    /// <param name="asset">Resource to anchor; the instance passed in is never mutated.
    /// <para>待锚定的资源；传入的实例不会被修改。</para></param>
    /// <param name="projectRoot">Project root directory that the resulting anchor is expressed against; it is
    /// normalized and compared to the asset source path without requiring the front and back slashes to match.
    /// <para>结果锚点所相对的项目根目录；该路径会被规范化，并在比较资源源路径时不要求正反斜杠写法一致。</para></param>
    /// <returns>A new asset copying every field of <paramref name="asset"/>. Its <see cref="RelativePath"/> is the
    /// source path expressed relative to <paramref name="projectRoot"/> with <c>/</c> separators when the source
    /// path is non-empty and lies inside the root on the same volume; otherwise it is <see langword="null"/> and the
    /// copy carries no project anchor. <see cref="SourcePath"/> is preserved either way.
    /// <para>一个复制了 <paramref name="asset"/> 全部字段的新资源。当源路径非空且在同一卷内位于
    /// <paramref name="projectRoot"/> 之下时，<see cref="RelativePath"/> 是源路径相对该根目录且使用 <c>/</c> 分隔的写法；
    /// 否则为 <see langword="null"/>，副本不带项目锚点。无论哪种情况都会保留 <see cref="SourcePath"/>。</para></returns>
    public static VisualPluginAsset WithProjectAnchor(VisualPluginAsset asset, string projectRoot)
    {
        var copy = asset.Snapshot();
        copy.RelativePath = AnchorWithin(projectRoot, asset.SourcePath);
        return copy;
    }

    /// <summary>Resolves the location the resource is read from for a given project root.
    /// <para>针对给定的项目根目录解析该资源的读取位置。</para></summary>
    /// <param name="projectRoot">Root directory of the open project; the portable <see cref="RelativePath"/> is
    /// resolved against it, and it is ignored when the resource has no anchor.
    /// <para>当前打开项目的根目录；可移植的 <see cref="RelativePath"/> 以其为基准解析，资源没有锚点时该参数被忽略。</para></param>
    /// <returns>The normalized full path formed by combining <paramref name="projectRoot"/> with <see cref="RelativePath"/>
    /// when an anchor exists, so a moved project resolves to its own copy; otherwise the recorded <see cref="SourcePath"/>
    /// as-is, which is <see langword="null"/> for resources that only live inside a bundle. An anchor that escapes its
    /// project root, is rooted, or is malformed resolves to <see langword="null"/> instead of a path outside the root.
    /// <para>存在锚点时返回由 <paramref name="projectRoot"/> 与 <see cref="RelativePath"/> 组合成的完整路径，因此移动后的
    /// 项目会解析到自己的那份副本；否则返回记录的 <see cref="SourcePath"/>，仅存在于包内的资源该值为
    /// <see langword="null"/>。越出项目根、为绝对路径或格式非法的锚点会解析为 <see langword="null"/>，而不是根目录之外
    /// 的路径。</para></returns>
    public string? ResolveSourcePath(string projectRoot)
    {
        if (string.IsNullOrEmpty(RelativePath))
        {
            return SourcePath;
        }
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return null;
        }
        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(Path.GetFullPath(projectRoot), RelativePath));
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return full.StartsWith(rootWithSeparator, StringComparison.Ordinal) ? full : null;
    }

    /// <summary>Positions a copied resource identity at the project file that now stores it.
    /// <para>把已复制资源的身份定位到当前实际存储它的项目文件。</para></summary>
    /// <param name="asset">Captured identity whose content fields are copied as-is; the instance passed in is never mutated.
    /// <para>已捕获的身份；其内容字段原样复制，传入的实例不会被修改。</para></param>
    /// <param name="sourcePath">Full path of the copy inside <paramref name="projectRoot"/>; it must lie under that root on the same volume.
    /// <para>位于 <paramref name="projectRoot"/> 内的副本完整路径；必须在同一卷内处于该根目录之下。</para></param>
    /// <param name="projectRoot">Project root directory the portable anchor is expressed against.
    /// <para>可移植锚点所相对的项目根目录。</para></param>
    /// <returns>A new asset whose <see cref="SourcePath"/> is the normalized copy path and whose <see cref="RelativePath"/>
    /// is that path relative to the root with <c>/</c> separators.
    /// <para>一个新资源：<see cref="SourcePath"/> 为规范化的副本路径，<see cref="RelativePath"/> 为该路径相对根目录且使用
    /// <c>/</c> 分隔的写法。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="sourcePath"/> is empty, <paramref name="projectRoot"/> is empty,
    /// or the copy does not lie inside the root.
    /// <para><paramref name="sourcePath"/> 为空、<paramref name="projectRoot"/> 为空，或副本不在根目录之内。</para></exception>
    public static VisualPluginAsset WithProjectLocation(VisualPluginAsset asset, string sourcePath, string projectRoot)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var full = Path.GetFullPath(sourcePath);
        var relative = AnchorWithin(projectRoot, full)
            ?? throw new ArgumentException("The project resource must lie inside the project root.", nameof(sourcePath));
        var copy = asset.Snapshot();
        copy.SourcePath = full;
        copy.RelativePath = relative;
        return copy;
    }

    private static string? AnchorWithin(string projectRoot, string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(projectRoot))
        {
            return null;
        }

        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        var full = Path.GetFullPath(sourcePath);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return full[rootWithSeparator.Length..].Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }

    /// <summary>Captures a selected file's identity without loading its contents into memory.
    /// <para>捕获所选文件的身份信息，而不把其内容整体读入内存。</para></summary>
    /// <param name="id">Portable resource identifier stored on the returned asset as-is.
    /// <para>存入返回资源的可移植资源标识符，原样保存。</para></param>
    /// <param name="path">File to hash; it is opened read-shared, streamed through SHA-256 in bounded chunks, and closed before returning.
    /// <para>待计算哈希的文件；以共享读方式打开，用有界分块经 SHA-256 流式处理，返回前即关闭。</para></param>
    /// <param name="cancellationToken">Cancels the incremental hash; cancellation propagates and no asset is returned.
    /// <para>取消增量哈希；取消会向上传播且不会返回资源。</para></param>
    /// <returns>Asset carrying the file's display name, exact length, lowercase-hex SHA-256 and full source path.
    /// <para>包含文件显示名、精确长度、小写十六进制 SHA-256 和完整源路径的资源。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null, empty, or contains invalid characters.
    /// <para><paramref name="path"/> 为 null、空或包含无效字符。</para></exception>
    /// <exception cref="InvalidDataException">The file exceeds <see cref="VisualPluginBundle.MaximumAssetSize"/>, shrank mid-read, or grew beyond its recorded length.
    /// <para>文件超过 <see cref="VisualPluginBundle.MaximumAssetSize"/>、读取中途变短或超出记录的长度。</para></exception>
    /// <exception cref="IOException">The file cannot be opened.<para>文件无法打开。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    public static async Task<VisualPluginAsset> FromFileAsync(string id, string path,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > VisualPluginBundle.MaximumAssetSize)
        {
            throw new InvalidDataException("Patch resource exceeds the per-resource size limit.");
        }
        var length = stream.Length;
        using var hash = Hash.Digests.Sha256.CreateAccumulator();
        var remaining = length;
        var buffer = new byte[131072];
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new InvalidDataException("Patch resource changed while capturing its identity.");
            }
            hash.Append(buffer.AsSpan(0, read));
            remaining -= read;
        }
        if (await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0)
        {
            throw new InvalidDataException("Patch resource changed while capturing its identity.");
        }
        return new VisualPluginAsset
        {
            Id = id,
            Name = Path.GetFileName(path),
            Length = length,
            Sha256 = Convert.ToHexStringLower(hash.GetHashAndReset()),
            SourcePath = Path.GetFullPath(path)
        };
    }
}