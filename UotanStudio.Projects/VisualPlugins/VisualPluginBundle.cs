using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using UotanStudio.Compress.Zip;

namespace UotanStudio.Projects;

/// <summary>
/// Bounded ZIP transport for portable patch graphs and lazily read resources.
/// <para>用于可移植补丁图与按需读取资源的有界 ZIP 传输容器。</para>
/// </summary>
public static class VisualPluginBundle
{
    /// <summary>
    /// Maximum number of resources in one bundle.
    /// <para>单个包内资源数量的上限。</para>
    /// </summary>
    public const int MaximumAssetCount = 256;

    /// <summary>
    /// Maximum length of one resource (256 MiB).
    /// <para>单个资源的最大长度（256 MiB）。</para>
    /// </summary>
    public const long MaximumAssetSize = 256L * 1024 * 1024;

    /// <summary>
    /// Maximum combined resource length (2 GiB).
    /// <para>所有资源合计的最大长度（2 GiB）。</para>
    /// </summary>
    public const long MaximumTotalAssetSize = 2L * 1024 * 1024 * 1024;

    private const int MaximumDirectorySize = 1024 * 1024;
    private const string ManifestName = "manifest.json";

    /// <summary>Reads bounded metadata. Resource hashes are checked when resources are consumed.
    /// <para>读取有界的元数据。资源哈希在消费资源时才校验。</para></summary>
    /// <param name="path">Bundle file to open. Its ZIP central directory is preflighted first under strict bounds (no ZIP64, no multidisk layout, capped entry count and directory size) before the archive is parsed.
    /// <para>待打开的包文件。先在其 ZIP 中央目录上执行严格边界的预检（不支持 ZIP64 和多盘布局，条目数与目录大小均有上限），然后再解析归档。</para></param>
    /// <returns>Validated package. Every scope, including embedded composite packages, retains the full path of this bundle as its source identity and a shared root snapshot that later reads re-validate against.
    /// <para>校验通过的包。每个作用域（含内嵌复合包）都会保留本包的完整路径作为来源标识，并持有一份共享的根快照，供后续读取时重新校验。</para></returns>
    /// <exception cref="InvalidDataException">Preflight rejected the archive, the manifest is missing, oversized, truncated or followed by extra bytes, its JSON structure is unsupported, the package fails validation, or the entries do not exactly match the declared resources.
    /// <para>预检拒绝该归档；清单缺失、超限、被截断或尾部有多余字节；JSON 结构不受支持；包校验失败；或条目与声明的资源不完全一致。</para></exception>
    /// <exception cref="IOException">The file cannot be opened or read.
    /// <para>文件无法打开或读取。</para></exception>
    public static VisualPluginPackage Load(string path)
    {
        using var file = OpenBundle(path);
        using var archive = ZipContainer.OpenRead(file);
        var manifest = archive.GetEntry(ManifestName)
            ?? throw new InvalidDataException("Patch bundle has no manifest.");
        if (manifest.Length > VisualPluginValidation.MaximumFileSize)
        {
            throw new InvalidDataException("Patch manifest exceeds its size limit.");
        }
        using var source = manifest.Open();
        var bytes = new byte[checked((int)manifest.Length)];
        source.ReadExactly(bytes);
        if (source.ReadByte() != -1)
        {
            throw new InvalidDataException("Patch manifest exceeds its declared length.");
        }
        VisualPluginPackage package;
        try
        {
            package = JsonSerializer.Deserialize(bytes, VisualPluginJsonContext.Default.VisualPluginPackage)
                ?? throw new InvalidDataException("Patch manifest is empty.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Patch manifest has an invalid or unsupported document structure.", error);
        }
        EnsureValid(package);
        ValidateEntries(archive, package);
        foreach (var scope in EnumerateScopes(package))
        {
            scope.BundlePath = Path.GetFullPath(path);
            scope.BundleRoot = package;
        }
        return package;
    }

    /// <summary>Streams the package resources into a sibling temporary ZIP and publishes it atomically.
    /// <para>将包内资源以流式写入同级临时 ZIP，并在校验通过后原子发布。</para></summary>
    /// <param name="path">Destination bundle path; it is resolved to a full path and may be overwritten on success.
    /// <para>目标包路径；会被解析为完整路径，成功后可能被覆盖。</para></param>
    /// <param name="package">Package to write; it must pass structural validation, its manifest must stay within the size limit, and every resource must be readable from its local source or an attached bundle.
    /// <para>待写入的包；必须通过结构化校验，清单大小不得超限，且每个资源都必须可从其本地源或所附包中读取。</para></param>
    /// <param name="cancellationToken">Checked before staging, per resource, and before the final move; cancellation propagates and the destination keeps its previous content.
    /// <para>在暂存前、每个资源写入时以及最终移动前均会检查；取消会向上传播，目标文件保持原有内容。</para></param>
    /// <returns>A task that completes once the bundle is published.
    /// <para>包发布完成后结束的异步任务。</para></returns>
    /// <exception cref="InvalidDataException">The package fails validation, the manifest exceeds its size limit, or a resource has no source or does not match its declared length or SHA-256.
    /// <para>包校验失败、清单超出大小限制，或资源没有来源、与其声明的长度或 SHA-256 不符。</para></exception>
    /// <exception cref="IOException">The temporary or destination file cannot be written or moved.
    /// <para>临时文件或目标文件无法写入或移动。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    public static Task SaveAsync(string path, VisualPluginPackage package,
        CancellationToken cancellationToken = default)
        => SaveAsync(path, package, null, cancellationToken);

    /// <summary>Streams the package resources into a sibling temporary ZIP and publishes it atomically, resolving
    /// project-anchored resources against the supplied project root.
    /// <para>将包内资源以流式写入同级临时 ZIP 并在校验通过后原子发布，同时按给定工程根解析项目锚点资源。</para>
    /// </summary>
    /// <param name="path">Destination bundle path; it is resolved to a full path and may be overwritten on success.
    /// <para>目标包路径；会被解析为完整路径，成功后可能被覆盖。</para></param>
    /// <param name="package">Package to write; it must pass structural validation and every resource must be readable
    /// from its project anchor, local source or attached bundle.
    /// <para>待写入的包；必须通过结构化校验，且每个资源都必须可从其项目锚点、本地源或所附包中读取。</para></param>
    /// <param name="projectRoot">Project root used to resolve <see cref="VisualPluginAsset.RelativePath"/> anchors, or
    /// <see langword="null"/> to use absolute sources and attached bundles only.
    /// <para>用于解析 <see cref="VisualPluginAsset.RelativePath"/> 锚点的工程根；为 <see langword="null"/> 时仅使用绝对源和所附包。</para></param>
    /// <param name="cancellationToken">Checked before staging, per resource, and before the final move; cancellation
    /// propagates and the destination keeps its previous content.
    /// <para>在暂存前、每个资源写入时以及最终移动前均会检查；取消会向上传播，目标文件保持原有内容。</para></param>
    /// <returns>A task that completes once the bundle is published.
    /// <para>包发布完成后结束的异步任务。</para></returns>
    /// <exception cref="InvalidDataException">The package fails validation, the manifest exceeds its size limit, or a
    /// resource has no source or does not match its declared length or SHA-256.
    /// <para>包校验失败、清单超出大小限制，或资源没有来源、与其声明的长度或 SHA-256 不符。</para></exception>
    /// <exception cref="IOException">The temporary or destination file cannot be written or moved.
    /// <para>临时文件或目标文件无法写入或移动。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    public static async Task SaveAsync(string path, VisualPluginPackage package, string? projectRoot,
        CancellationToken cancellationToken)
    {
        EnsureValid(package);
        cancellationToken.ThrowIfCancellationRequested();
        var manifest = JsonSerializer.SerializeToUtf8Bytes(package, VisualPluginJsonContext.Default.VisualPluginPackage);
        if (manifest.LongLength > VisualPluginValidation.MaximumFileSize)
        {
            throw new InvalidDataException("Patch manifest exceeds its size limit.");
        }
        var destination = Path.GetFullPath(path);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite,
                             FileShare.None, 131072, FileOptions.Asynchronous))
            {
                using (var archive = ZipContainer.Create(file, leaveOpen: true))
                {
                    await using (var output = archive.CreateEntry(ManifestName, CompressionLevel.NoCompression).Open())
                    {
                        await output.WriteAsync(manifest, cancellationToken).ConfigureAwait(false);
                    }
                    var written = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var (scope, asset) in EnumerateScopes(package).SelectMany(scope =>
                                 scope.Assets.Select(asset => (scope, asset))))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var entryName = GetEntryName(asset);
                        if (!written.Add(entryName))
                        {
                            continue;
                        }
                        await using var output = archive.CreateEntry(entryName, CompressionLevel.NoCompression).Open();
                        await CopyAssetAsync(scope, asset.Id, output, projectRoot, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>Copies one resource with bounded memory and exact length and hash checks.
    /// <para>以有界内存和精确的长度与哈希校验复制单个资源。</para></summary>
    /// <param name="package">Scope whose own asset list is searched and whose structure is validated before copying.
    /// <para>复制前先校验其结构、并在其自身资源列表中查找目标的作用域。</para></param>
    /// <param name="id">Resource identifier, matched case-sensitively against the scope's asset ids.
    /// <para>资源标识符，与作用域的资产 id 进行区分大小写的匹配。</para></param>
    /// <param name="destination">Writable stream owned and kept open by the caller; it is neither closed nor rewound, and a failed check can leave partially written content in it.
    /// <para>由调用方持有并保持打开的可写流；本方法既不关闭也不回退它，校验失败时其中可能残留已部分写入的内容。</para></param>
    /// <param name="cancellationToken">Cancels reads and writes; cancellation propagates.
    /// <para>取消读取与写入；取消会向上传播。</para></param>
    /// <returns>A task that completes once the verified bytes are written.
    /// <para>校验通过的字节全部写入后结束的异步任务。</para></returns>
    /// <exception cref="InvalidDataException">The package fails validation, the id is unknown, the resource has neither a local source nor a bundle path, the source is truncated or longer than declared, or its SHA-256 does not match (compared case-insensitively).
    /// <para>包校验失败、id 未知、资源既无本地源也无包路径、来源被截断或长于声明值，或其 SHA-256 不匹配（比较不区分大小写）。</para></exception>
    /// <exception cref="IOException">The source file or bundle cannot be opened or read.
    /// <para>源文件或包无法打开或读取。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    public static Task CopyAssetAsync(VisualPluginPackage package, string id, Stream destination,
        CancellationToken cancellationToken = default)
        => CopyAssetAsync(package, id, destination, null, cancellationToken);

    /// <summary>Copies one resource with bounded memory and exact length and hash checks, resolving a project-anchored
    /// resource against the supplied project root.
    /// <para>以有界内存和精确的长度与哈希校验复制单个资源，并按给定工程根解析项目锚点资源。</para>
    /// </summary>
    /// <param name="package">Scope whose own asset list is searched and whose structure is validated before copying.
    /// <para>复制前先校验其结构、并在其自身资源列表中查找目标的作用域。</para></param>
    /// <param name="id">Resource identifier, matched case-sensitively against the scope's asset ids.
    /// <para>资源标识符，与作用域的资产 id 进行区分大小写的匹配。</para></param>
    /// <param name="destination">Writable stream owned and kept open by the caller; it is neither closed nor rewound,
    /// and a failed check can leave partially written content in it.
    /// <para>由调用方持有并保持打开的可写流；本方法既不关闭也不回退它，校验失败时其中可能残留已部分写入的内容。</para></param>
    /// <param name="projectRoot">Project root used to resolve <see cref="VisualPluginAsset.RelativePath"/> for resources
    /// that have no absolute authoring source and no attached bundle; <see langword="null"/> disables anchor resolution.
    /// <para>用于为没有绝对编辑源、也没有所附包的资源解析 <see cref="VisualPluginAsset.RelativePath"/> 的工程根；
    /// <see langword="null"/> 表示不解析锚点。</para></param>
    /// <param name="cancellationToken">Cancels reads and writes; cancellation propagates.
    /// <para>取消读取与写入；取消会向上传播。</para></param>
    /// <returns>A task that completes once the verified bytes are written.
    /// <para>校验通过的字节全部写入后结束的异步任务。</para></returns>
    /// <exception cref="InvalidDataException">The package fails validation, the id is unknown, the resource has no
    /// resolvable source, the source is truncated or longer than declared, or its SHA-256 does not match (compared
    /// case-insensitively).
    /// <para>包校验失败、id 未知、资源没有可解析来源、来源被截断或长于声明值，或其 SHA-256 不匹配（比较不区分大小写）。</para></exception>
    /// <exception cref="IOException">The source file, anchored copy or bundle cannot be opened or read.
    /// <para>源文件、锚定副本或包无法打开或读取。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    public static async Task CopyAssetAsync(VisualPluginPackage package, string id, Stream destination,
        string? projectRoot, CancellationToken cancellationToken)
    {
        EnsureValid(package);
        var asset = package.Assets.SingleOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal))
            ?? throw new InvalidDataException($"Patch resource '{id}' does not exist.");
        if (ResolveLocalSource(package, asset, projectRoot) is { } sourcePath)
        {
            await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await CopyVerifiedAsync(source, destination, asset, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (package.BundlePath is null)
        {
            throw new InvalidDataException($"Patch resource '{id}' has no source.");
        }
        await using var file = OpenBundle(package.BundlePath);
        using var archive = ZipContainer.OpenRead(file);
        EnsureValid(package.BundleRoot ?? package);
        ValidateEntries(archive, package.BundleRoot ?? package);
        await using var entry = archive.GetEntry(GetEntryName(asset))!.Open();
        await CopyVerifiedAsync(entry, destination, asset, cancellationToken).ConfigureAwait(false);
    }

    private static string? ResolveLocalSource(VisualPluginPackage package, VisualPluginAsset asset, string? projectRoot)
    {
        if (asset.SourcePath is not null)
        {
            return asset.ResolveSourcePath(projectRoot ?? string.Empty) ?? asset.SourcePath;
        }
        if (package.BundlePath is null && !string.IsNullOrWhiteSpace(projectRoot))
        {
            return asset.ResolveSourcePath(projectRoot);
        }
        return null;
    }

    private static async Task CopyVerifiedAsync(Stream source, Stream destination, VisualPluginAsset asset,
        CancellationToken cancellationToken)
    {
        using var hash = Hash.Digests.Sha256.CreateAccumulator();
        var buffer = new byte[131072];
        var remaining = asset.Length;
        while (remaining > 0)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new InvalidDataException("Patch resource is truncated.");
            }
            hash.Append(buffer.AsSpan(0, read));
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
        if (await source.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0
            || !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), asset.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Patch resource length or SHA-256 does not match its declared identity.");
        }
    }

    private static void EnsureValid(VisualPluginPackage package)
    {
        var errors = VisualPluginValidation.Validate(package);
        if (errors.Count != 0)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        }
    }

    private static void ValidateEntries(ZipArchive archive, VisualPluginPackage package)
    {
        var expected = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var asset in EnumerateScopes(package).SelectMany(scope => scope.Assets))
        {
            var name = GetEntryName(asset);
            if (expected.TryGetValue(name, out var length) && length != asset.Length)
            {
                throw new InvalidDataException("Resources with the same content hash declare different lengths.");
            }
            expected[name] = asset.Length;
        }
        expected.Add(ManifestName, -1);
        foreach (var entry in archive.Entries)
        {
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (!expected.Remove(entry.FullName, out var length) || length >= 0 && length != entry.Length
                || unixType is not (0 or 0x8000))
            {
                throw new InvalidDataException("Patch bundle contains duplicate, unexpected, linked or incorrectly sized entries.");
            }
        }
        if (expected.Count != 0)
        {
            throw new InvalidDataException("Patch bundle is missing declared resources.");
        }
    }

    private static FileStream OpenBundle(string path)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            131072, FileOptions.Asynchronous | FileOptions.RandomAccess);
        try
        {
            Preflight(file);
            file.Position = 0;
            return file;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    private static string GetEntryName(VisualPluginAsset asset) => "assets/" + asset.Sha256.ToLowerInvariant();

    /// <summary>Enumerates a structurally validated embedded graph without using registry or global state.</summary>
    public static IEnumerable<VisualPluginPackage> EnumerateScopes(VisualPluginPackage package)
    {
        yield return package;
        foreach (var child in package.Graph.Nodes.Select(node => node.CompositePackage).OfType<VisualPluginPackage>())
        {
            foreach (var scope in EnumerateScopes(child))
            {
                yield return scope;
            }
        }
    }

    private static void Preflight(Stream file)
    {
        if (file.Length < 22 || file.Length > MaximumTotalAssetSize + VisualPluginValidation.MaximumFileSize + MaximumDirectorySize * 2)
        {
            throw new InvalidDataException("Patch bundle size is outside supported bounds.");
        }
        var tail = new byte[(int)Math.Min(file.Length, 65557)];
        file.Position = file.Length - tail.Length;
        file.ReadExactly(tail);
        var end = -1;
        for (var i = tail.Length - 22; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == 0x06054B50
                && i + 22 + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20)) == tail.Length)
            {
                end = i;
                break;
            }
        }
        if (end < 0)
        {
            throw new InvalidDataException("Patch bundle end record is missing.");
        }
        var record = tail.AsSpan(end);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(record[10..]);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(record[16..]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(record[4..]) != 0
            || BinaryPrimitives.ReadUInt16LittleEndian(record[8..]) != count
            || count is 0 or > MaximumAssetCount + 1 || size > MaximumDirectorySize
            || (long)offset + size != file.Length - tail.Length + end)
        {
            throw new InvalidDataException("Patch bundle directory is oversized, ZIP64, multidisk or malformed.");
        }
        file.Position = offset;
        var directory = new byte[(int)size];
        file.ReadExactly(directory);
        var cursor = 0;
        for (var i = 0; i < count; i++)
        {
            if (directory.Length - cursor < 46)
            {
                throw new InvalidDataException("Truncated patch bundle directory.");
            }
            var header = directory.AsSpan(cursor);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x02014B50
                || (BinaryPrimitives.ReadUInt16LittleEndian(header[8..]) & 1) != 0
                || BinaryPrimitives.ReadUInt16LittleEndian(header[34..]) != 0
                || BinaryPrimitives.ReadUInt32LittleEndian(header[20..]) == uint.MaxValue
                || BinaryPrimitives.ReadUInt32LittleEndian(header[24..]) == uint.MaxValue
                || BinaryPrimitives.ReadUInt32LittleEndian(header[42..]) >= offset)
            {
                throw new InvalidDataException("Unsupported patch bundle directory entry.");
            }
            cursor += 46 + BinaryPrimitives.ReadUInt16LittleEndian(header[28..])
                + BinaryPrimitives.ReadUInt16LittleEndian(header[30..])
                + BinaryPrimitives.ReadUInt16LittleEndian(header[32..]);
            if (cursor > directory.Length)
            {
                throw new InvalidDataException("Patch bundle directory entry exceeds its range.");
            }
        }
        if (cursor != directory.Length)
        {
            throw new InvalidDataException("Patch bundle directory count is inconsistent.");
        }
    }
}