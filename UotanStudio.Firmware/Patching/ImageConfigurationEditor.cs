using System.Security.Cryptography;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.Core.Streams;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.FileSystem.Ext4;
using UotanStudio.Firmware.FileSystem.Extraction;
using UotanStudio.Firmware.FileSystem.Rebuilding;

namespace UotanStudio.Firmware.Patching;

/// <summary>
/// Edits existing configuration content and inode attributes through a private, metadata-preserving rebuild.
/// <para>通过私有且保留元数据的重建流程，编辑已有的配置内容与 inode 属性。</para>
/// </summary>
public static class ImageConfigurationEditor
{
    /// <summary>
    /// Rebuilds raw EXT4/EROFS, checks the replaced bytes and publishes atomically. The replacement is
    /// limited to 16 MiB. Extraction and output capacities are explicit; caller streams remain open.
    /// <para>重建原始 EXT4/EROFS 镜像，校验被替换的字节后再原子发布。替换内容限制为 16 MiB。
    /// 解包字节预算与输出容量均需显式给出；调用方传入的流保持打开。</para>
    /// </summary>
    /// <example>
    /// <code>
    /// await ImageConfigurationEditor.RebuildAsync(source, "EXT4", "/system/etc/hosts", content,
    ///     target, capacity, 256L * 1024 * 1024, token);
    /// </code>
    /// </example>
    /// <param name="sourcePath">Existing raw image file read as the rebuild input. <para>作为重建输入读取的现有原始镜像文件路径。</para></param>
    /// <param name="format"><c>"EXT4"</c> or <c>"EROFS"</c>, matched case-sensitively. <para>区分大小写的 <c>"EXT4"</c> 或 <c>"EROFS"</c>。</para></param>
    /// <param name="imagePath">Image-relative path of the configuration file being replaced, validated by
    /// <see cref="ImageConfigurationReader.Extract"/>. <para>被替换配置文件在镜像内的路径，按
    /// <see cref="ImageConfigurationReader.Extract"/> 的规则校验。</para></param>
    /// <param name="replacement">Readable stream whose bytes from the current position become the new file content; it is not
    /// disposed and its excess beyond 16 MiB fails. <para>可读流，从当前位置起的字节成为新的文件内容；不会被释放，
    /// 超出 16 MiB 的部分会导致失败。</para></param>
    /// <param name="outputPath">Destination file replaced atomically only after every verification passes.
    /// <para>目标文件路径；只有在全部校验通过后才会被原子替换。</para></param>
    /// <param name="capacity">Maximum output image size in bytes; must be positive. <para>输出镜像的最大字节数；必须为正数。</para></param>
    /// <param name="maximumExtractionBytes">Total byte budget across all files the tree extraction may produce; must be positive.
    /// <para>整棵目录树解包可产生的总字节预算；必须为正数。</para></param>
    /// <param name="token">Checked throughout copying, unpacking, rebuilding and publication.
    /// <para>在复制、解包、重建与发布的全过程中都会检查。</para></param>
    /// <returns>A task that completes once the rebuilt image has been verified and published.
    /// <para>在重建镜像完成校验并发布后结束的任务。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="replacement"/> is <see langword="null"/>.
    /// <para><paramref name="replacement"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> or
    /// <paramref name="maximumExtractionBytes"/> is zero or negative.
    /// <para><paramref name="capacity"/> 或 <paramref name="maximumExtractionBytes"/> 为零或负数。</para></exception>
    /// <exception cref="InvalidDataException">Any failure listed for <see cref="RebuildManyAsync"/> applies here as well.
    /// <para><see cref="RebuildManyAsync"/> 中列出的任何失败情形同样适用于本方法。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="token"/> was signaled; the destination keeps its previous
    /// content. <para><paramref name="token"/> 已发出取消信号；目标文件保留原有内容。</para></exception>
    public static Task RebuildAsync(string sourcePath, string format, string imagePath, Stream replacement,
        string outputPath, long capacity, long maximumExtractionBytes, CancellationToken token)
        => RebuildManyAsync(sourcePath, format, [new ImageConfigurationReplacement(imagePath, replacement)],
            outputPath, capacity, maximumExtractionBytes, token);

    /// <summary>
    /// Applies up to 256 content and 256 attribute requests in one tree, verifying requested bytes and attributes before
    /// publication.
    /// <para>在同一棵目录树中最多应用 256 项内容替换与 256 项属性修改，并在发布前校验所请求的字节与属性。</para>
    /// </summary>
    /// <param name="sourcePath">Existing raw image file copied to a private temporary file before any editing.
    /// <para>在任何编辑之前会被复制到私有临时文件的现有原始镜像文件。</para></param>
    /// <param name="format">Selects the handler: <c>"EXT4"</c> or <c>"EROFS"</c>, matched case-sensitively.
    /// <para>决定使用哪个处理器：区分大小写的 <c>"EXT4"</c> 或 <c>"EROFS"</c>。</para></param>
    /// <param name="replacements">Content requests, at most 256 with distinct paths; an empty list is allowed only when an
    /// attribute or extended-attribute list is non-empty. <para>内容替换请求，最多 256 项且路径互不重复；
    /// 仅当属性或扩展属性列表非空时才允许为空。</para></param>
    /// <param name="outputPath">Destination file written through a temporary file and replaced atomically at the end.
    /// <para>目标文件经由临时文件写入，最后原子替换。</para></param>
    /// <param name="capacity">Maximum output image size in bytes; must be positive. <para>输出镜像的最大字节数；必须为正数。</para></param>
    /// <param name="maximumExtractionBytes">Total byte budget for the extracted tree; must be positive.
    /// <para>解包目录树的总字节预算；必须为正数。</para></param>
    /// <param name="token">Checked during copying, unpacking, rebuilding, verification and publication.
    /// <para>在复制、解包、重建、校验与发布的全过程中都会检查。</para></param>
    /// <param name="attributeEdits">Ownership and permission changes, at most 256; the file-type bits of the current mode are
    /// retained. <see langword="null"/> means no attribute edits. <para>属主与权限修改，最多 256 项；保留当前 mode 的文件类型位。
    /// <see langword="null"/> 表示不做属性编辑。</para></param>
    /// <param name="xattrEdits">Extended-attribute changes, at most 256, each with a value of at most 64 KiB;
    /// <see langword="null"/> means no extended-attribute edits. <para>扩展属性修改，最多 256 项，每项取值最多 64 KiB；
    /// <see langword="null"/> 表示不做扩展属性编辑。</para></param>
    /// <returns>A task that completes once every request has been re-read from the rebuilt image and the output is published.
    /// <para>在全部请求都能从重建后的镜像中重新读出、且输出已发布后结束的任务。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="replacements"/> is <see langword="null"/>, one of its items is
    /// <see langword="null"/> or carries a null path or content stream, or an element of <paramref name="attributeEdits"/> or
    /// <paramref name="xattrEdits"/> is <see langword="null"/>.
    /// <para><paramref name="replacements"/> 为 <see langword="null"/>、其中某一项为 <see langword="null"/> 或其路径与
    /// 内容流为空，或 <paramref name="attributeEdits"/>、<paramref name="xattrEdits"/> 中存在 <see langword="null"/> 元素。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> or
    /// <paramref name="maximumExtractionBytes"/> is zero or negative.
    /// <para><paramref name="capacity"/> 或 <paramref name="maximumExtractionBytes"/> 为零或负数。</para></exception>
    /// <exception cref="InvalidDataException">The batch is empty or over 256 requests of a kind, the format is unsupported, a
    /// replacement path repeats or its stream is unreadable, an extended attribute value exceeds 64 KiB, the extraction byte
    /// budget is exceeded, unpacking or preservation fails, the rebuilt image lacks the metadata, or content, inode attribute or
    /// extended attribute verification fails. <para>批次为空或某类请求超过 256 项、格式不受支持、替换路径重复或流不可读、
    /// 扩展属性值超过 64 KiB、超出解包字节预算、解包或元数据保留失败、重建镜像缺少元数据，或内容、inode 属性、
    /// 扩展属性校验失败。</para></exception>
    /// <exception cref="FileNotFoundException">A replacement, attribute or extended attribute target is absent from the image.
    /// <para>镜像中不存在内容、属性或扩展属性的目标路径。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="token"/> was signaled; the temporary directory is removed and
    /// the destination keeps its previous content. <para><paramref name="token"/> 已发出取消信号；临时目录会被删除，
    /// 目标文件保留原有内容。</para></exception>
    public static async Task RebuildManyAsync(string sourcePath, string format,
        IReadOnlyList<ImageConfigurationReplacement> replacements, string outputPath, long capacity,
        long maximumExtractionBytes, CancellationToken token, IReadOnlyList<FileSystemAttributeEdit>? attributeEdits = null,
        IReadOnlyList<FileSystemXattrEdit>? xattrEdits = null)
    {
        ArgumentNullException.ThrowIfNull(replacements);
        if (replacements.Count > 256 || attributeEdits?.Count > 256 || xattrEdits?.Count > 256
            || replacements.Count == 0 && attributeEdits is not { Count: > 0 } && xattrEdits is not { Count: > 0 })
        {
            throw new InvalidDataException("An image edit batch requires at least one request and at most 256 requests of each kind.");
        }
        var edits = replacements.ToArray();
        var attributes = attributeEdits?.ToArray() ?? [];
        var extended = xattrEdits?.Select(edit =>
        {
            ArgumentNullException.ThrowIfNull(edit);
            if (edit.Value?.Length > 65536 || edit.ExpectedValue?.Length > 65536)
            {
                throw new InvalidDataException("Extended attribute value exceeds 64 KiB.");
            }
            return edit with { Value = edit.Value?.ToArray(), ExpectedValue = edit.ExpectedValue?.ToArray() };
        }).ToArray() ?? [];
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edit in edits)
        {
            ArgumentNullException.ThrowIfNull(edit);
            ArgumentNullException.ThrowIfNull(edit.Path);
            ArgumentNullException.ThrowIfNull(edit.Content);
            if (!edit.Content.CanRead || !identities.Add(edit.Path.TrimStart('/')))
            {
                throw new InvalidDataException("Image replacement paths must be distinct and streams readable.");
            }
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumExtractionBytes);
        IFormatHandler handler = format switch
        {
            "EXT4" => new Ext4FormatHandler(),
            "EROFS" => new ErofsFormatHandler(),
            _ => throw new PatchingUnsupportedFeatureException("Only raw EXT4 and EROFS configuration edits are supported.")
        };
        token.ThrowIfCancellationRequested();
        var temporary = Path.Combine(Path.GetTempPath(), "uotan-image-edit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var frozen = Path.Combine(temporary, "source.img");
            await using (var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
            await using (var output = new FileStream(frozen, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                using var limited = new CountingWriteStream(output, 64L * 1024 * 1024 * 1024, leaveOpen: true, cancellationToken: token);
                await input.CopyToAsync(limited, 65536, token).ConfigureAwait(false);
            }
            foreach (var edit in edits)
            {
                using var image = File.OpenRead(frozen);
                ImageConfigurationReader.Extract(image, format, edit.Path, Stream.Null, token);
            }
            var tree = Path.Combine(temporary, "source");
            var originalAttributes = new List<ImageFileAttributes>();
            foreach (var edit in attributes)
            {
                ArgumentNullException.ThrowIfNull(edit);
                using var image = File.OpenRead(frozen);
                originalAttributes.Add(ImageAttributeReader.Read(image, format, edit.Path, token));
            }
            var paths = ReadPaths(frozen, format, maximumExtractionBytes, token);
            var mapping = new PortableExtractionPaths(tree, paths);
            using var scope = RepackMetadata.UseContext(RepackMetadataContext.FromProjectRoot(Path.Combine(temporary, "metadata")));
            var unpacked = await handler.UnpackAsync(frozen, tree, cancellationToken: token).ConfigureAwait(false);
            if (!unpacked.Success) { throw new InvalidDataException(unpacked.Summary); }
            foreach (var edit in edits)
            {
                var mapped = mapping.Resolve(edit.Path.TrimStart('/'));
                await using var output = new FileStream(mapped, FileMode.Truncate, FileAccess.Write, FileShare.None, 65536, true);
                using var limited = new CountingWriteStream(output, 16 * 1024 * 1024, leaveOpen: true, cancellationToken: token);
                await edit.Content.CopyToAsync(limited, 65536, token).ConfigureAwait(false);
            }
            var rebuilt = Path.Combine(temporary, "rebuilt.img");
            if (!FileSystemPreservation.TryRestore(tree, rebuilt, format, token, maximumOutputBytes: capacity,
                attributeEdits: attributes, xattrEdits: extended))
            {
                throw new InvalidDataException("Image preservation metadata is missing.");
            }
            var extracted = Path.Combine(temporary, "verified-content");
            foreach (var edit in edits)
            {
                using (var image = File.OpenRead(rebuilt))
                using (var output = new FileStream(extracted, FileMode.Create, FileAccess.Write))
                {
                    ImageConfigurationReader.Extract(image, format, edit.Path, output, token);
                }
                await using var expected = File.OpenRead(mapping.Resolve(edit.Path.TrimStart('/')));
                await using var actual = File.OpenRead(extracted);
                var expectedHash = await SHA256.HashDataAsync(expected, token).ConfigureAwait(false);
                var actualHash = await SHA256.HashDataAsync(actual, token).ConfigureAwait(false);
                if (expected.Length != actual.Length || !expectedHash.AsSpan().SequenceEqual(actualHash))
                {
                    throw new InvalidDataException("Rebuilt image configuration verification failed.");
                }
            }
            for (var index = 0; index < attributes.Length; index++)
            {
                var edit = attributes[index];
                using var image = File.OpenRead(rebuilt);
                var actual = ImageAttributeReader.Read(image, format, edit.Path, token);
                if (actual.Uid != edit.Uid || actual.Gid != edit.Gid || (actual.Mode & 0xFFF) != edit.Permissions
                    || (actual.Mode & 0xF000) != (originalAttributes[index].Mode & 0xF000))
                {
                    throw new InvalidDataException("Rebuilt inode attribute verification failed.");
                }
            }
            foreach (var edit in extended)
            {
                using var image = File.OpenRead(rebuilt);
                var actual = ImageAttributeReader.ReadXattr(image, format, edit.Path, edit.Name, token);
                if (actual is null ? edit.Value is not null : edit.Value is null || !actual.AsSpan().SequenceEqual(edit.Value))
                {
                    throw new InvalidDataException("Rebuilt extended attribute verification failed.");
                }
            }
            FileExtraction.WriteAtomic(outputPath, output =>
            {
                using var input = File.OpenRead(rebuilt);
                using var limited = new CountingWriteStream(output, capacity, leaveOpen: true, cancellationToken: token);
                input.CopyTo(limited, 65536);
                token.ThrowIfCancellationRequested();
            }, overwrite: true);
        }
        finally
        {
            Directory.Delete(temporary, true);
        }
    }

    private static string[] ReadPaths(string source, string format, long maximumBytes, CancellationToken token)
    {
        using var stream = File.OpenRead(source);
        long total = 0;
        if (format == "EXT4")
        {
            using var parser = new Ext4Parser(stream, 100000, 16 * 1024 * 1024, token);
            return parser.ListFiles().Select(entry => Check(entry.Path, entry.Size)).ToArray();
        }
        using var erofs = new ErofsParser(stream, 100000, 16 * 1024 * 1024, token);
        return erofs.EnumerateFiles().Select(entry => Check(entry.Path, entry.Size)).ToArray();

        string Check(string path, long size)
        {
            token.ThrowIfCancellationRequested();
            if (size < 0 || size > maximumBytes - total)
            {
                throw new PatchingCorruptedException("Image exceeds the extraction byte budget.");
            }
            total += size;
            return path;
        }
    }
}

/// <summary>
/// One literal image path and replacement bytes, consumed from the stream's current position without disposing it.
/// <para>一个字面的镜像内路径与替换字节；从流的当前位置开始读取，且不会释放该流。</para>
/// </summary>
/// <param name="Path">Image-relative target path, validated by <see cref="ImageConfigurationReader.Extract"/> before the rebuild.
/// <para>镜像内的目标路径，在重建之前由 <see cref="ImageConfigurationReader.Extract"/> 校验。</para></param>
/// <param name="Content">Readable stream supplying the new file bytes from its current position, capped at 16 MiB; the caller keeps
/// ownership and closes it. <para>从当前位置提供新文件字节的可读流，上限为 16 MiB；所有权仍属调用方，由其关闭。</para></param>
public sealed record ImageConfigurationReplacement(string Path, Stream Content);