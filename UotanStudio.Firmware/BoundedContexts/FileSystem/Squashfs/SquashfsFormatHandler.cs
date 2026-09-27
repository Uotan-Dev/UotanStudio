using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Squashfs;

/// <summary>
/// Read-only format handler for SquashFS version 4 images: detection by the <c>hsqs</c>/<c>sqsh</c> magic,
/// metadata reading and tree extraction. Packing is intentionally absent; SquashFS writing stays with the
/// external tooling pipeline.
/// <para>SquashFS 版本 4 镜像的只读格式处理器：按 <c>hsqs</c>/<c>sqsh</c> 魔数识别、读取元数据并解包目录树。
/// 本处理器刻意不提供打包能力；SquashFS 写入仍由外部工具流水线负责。</para>
/// </summary>
public sealed class SquashfsFormatHandler : IReadonlyFormatHandler
{
    /// <summary>Name of the format this handler serves.
    /// <para>本处理器所服务格式的名称。</para></summary>
    /// <value>The constant <c>"SquashFS"</c>. <para>常量 <c>"SquashFS"</c>。</para></value>
    public string FormatName => "SquashFS";

    /// <summary>File extensions this handler registers.
    /// <para>本处理器注册的文件扩展名。</para></summary>
    /// <value>The list <c>img</c>, <c>squashfs</c>, <c>sqsh</c> and <c>sfs</c>; extension alone never
    /// overrides magic detection. <para>列表 <c>img</c>、<c>squashfs</c>、<c>sqsh</c> 与 <c>sfs</c>；
    /// 仅凭扩展名不会取代魔数检测。</para></value>
    public IReadOnlyList<string> FileExtensions { get; } = ["img", "squashfs", "sqsh", "sfs"];

    /// <summary>
    /// Determines whether the file carries the SquashFS v4 magic in either byte order.
    /// <para>判断文件是否携带任一键序的 SquashFS v4 魔数。</para>
    /// </summary>
    /// <param name="filePath">Candidate file path; <see langword="null"/>, blank or nonexistent paths
    /// short-circuit to <see langword="false"/>.
    /// <para>候选文件路径；为 <see langword="null"/>、空白或不存在的路径直接返回 <see langword="false"/>。</para></param>
    /// <returns><see langword="true"/> when the magic and version 4.0/4.1 header validate; <see langword="false"/>
    /// on any I/O failure, which is swallowed.
    /// <para>魔数与 4.0/4.1 版本头校验通过时返回 <see langword="true"/>；任何 I/O 失败都返回
    /// <see langword="false"/> 且被吞掉。</para></returns>
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            using var fs = File.OpenRead(filePath);
            if (fs.Length < SquashfsSuperblock.Size)
            {
                return false;
            }

            Span<byte> super = stackalloc byte[SquashfsSuperblock.Size];
            fs.ReadExactly(super);
            var sb = SquashfsSuperblock.Parse(super, fs.Length);
            return sb.Compression is >= 0 and <= 6;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the superblock, table geometry and full entry listing without extracting file data.
    /// <para>读取超级块、表几何与完整条目列表，但不解出文件数据。</para>
    /// </summary>
    /// <param name="filePath">SquashFS image file, opened read-only on a worker thread.
    /// <para>SquashFS 镜像文件；在工作线程上以只读方式打开。</para></param>
    /// <param name="cancellationToken">Token checked throughout the walk.
    /// <para>在遍历全程检查的令牌。</para></param>
    /// <returns>An <c>Ok</c> result whose metadata carries superblock geometry, compressor name, feature
    /// flag names and xattr counts and whose entries list every image path; a <c>Fail</c> result with the
    /// error message when the image cannot be parsed.
    /// <para><c>Ok</c> 结果的元数据包含超级块几何、压缩器名、特性标志名与 xattr 计数，明细行列出全部镜像
    /// 路径；镜像无法解析时返回带错误消息的 <c>Fail</c> 结果。</para></returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已被触发。</para></exception>
    public Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var input = File.OpenRead(filePath);
                var parser = new SquashfsParser(input, cancellationToken);
                var sb = parser.Superblock;
                var entries = parser.EnumerateEntries();
                var metadata = new Dictionary<string, string>
                {
                    ["Format"] = "SquashFS",
                    ["Version"] = $"{sb.MajorVersion}.{sb.MinorVersion}",
                    ["ByteOrder"] = sb.BigEndian ? "big-endian" : "little-endian",
                    ["BlockSize"] = sb.BlockSize.ToString(),
                    ["BlockLog"] = sb.BlockLog.ToString(),
                    ["Inodes"] = sb.InodeCount.ToString(),
                    ["Fragments"] = sb.FragmentCount.ToString(),
                    ["Compression"] = SquashfsDecompression.Describe(sb.Compression),
                    ["Flags"] = MetadataDisplay.Flags(sb.Flags,
                        (0x1, "uncompressed inodes"),
                        (0x2, "uncompressed data"),
                        (0x8, "uncompressed fragments"),
                        (0x10, "no fragments"),
                        (0x20, "always fragments"),
                        (0x40, "duplicates kept"),
                        (0x80, "exports"),
                        (0x100, "no xattrs"),
                        (0x400, "compression options")),
                    ["IdCount"] = sb.IdCount.ToString(),
                    ["MkfsTime"] = MetadataDisplay.Number(sb.MkfsTime),
                    ["RootInode"] = $"0x{sb.RootInodeAddress:X16}",
                    ["BytesUsed"] = MetadataDisplay.Number(sb.BytesUsed),
                    ["InodeTableStart"] = $"0x{sb.InodeTableStart:X16}",
                    ["DirectoryTableStart"] = $"0x{sb.DirectoryTableStart:X16}",
                    ["FragmentTableStart"] = $"0x{sb.FragmentTableStart:X16}",
                    ["LookupTableStart"] = $"0x{sb.LookupTableStart:X16}",
                    ["XattrIds"] = parser.XattrIdCount < 0 ? "none" : parser.XattrIdCount.ToString(),
                    ["EntryCount"] = entries.Count.ToString(),
                };
                return FirmwareOpResult.Ok(
                    $"SquashFS {metadata["Version"]} image, {metadata["Compression"]} compressed, {entries.Count} entries.",
                    [.. entries.Select(entry => entry.Path)],
                    metadata);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail($"Failed to read SquashFS metadata from '{filePath}': {ex.Message}");
            }
        }, cancellationToken);

    /// <summary>
    /// Extracts the SquashFS tree into the output directory.
    /// <para>将 SquashFS 目录树解包到输出目录。</para>
    /// </summary>
    /// <param name="filePath">Source image; the input is never modified.
    /// <para>源镜像；输入文件不会被修改。</para></param>
    /// <param name="outputDirectory">Destination tree; entries are written with their image paths.
    /// <para>目标目录树；条目按镜像内路径写出。</para></param>
    /// <param name="options">Unpack options; <see langword="null"/> overwrites existing files, reports no
    /// byte progress and applies the default output budget. Symbolic links become files holding their
    /// target text. <para>解包选项；为 <see langword="null"/> 时覆盖已存在文件、不报告字节进度并使用默认
    /// 输出预算。符号链接会写成承载目标文本的文件。</para></param>
    /// <param name="cancellationToken">Token checked throughout parsing and extraction.
    /// <para>在解析与解包全程检查的令牌。</para></param>
    /// <returns>An <c>Ok</c> result listing the extracted file paths, or a <c>Fail</c> result when the image,
    /// a compressor block or extraction fails. <para>返回列出已解出文件路径的 <c>Ok</c> 结果；镜像、压缩块
    /// 或解包失败时返回 <c>Fail</c> 结果。</para></returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已被触发。</para></exception>
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var input = File.OpenRead(filePath);
                var parser = new SquashfsParser(input, cancellationToken);
                var byteProgress = FileSystemByteProgress.ForStage(options?.FileSystemProgress, FileSystemWorkStage.Extracting);
                var files = parser.ExtractToDirectory(
                    outputDirectory,
                    overwrite: options?.Overwrite ?? true,
                    maximumOutputBytes: options?.MaximumOutputBytes ?? new UnpackOptions().MaximumOutputBytes,
                    byteProgress);
                return FirmwareOpResult.Ok(
                    $"Extracted {files.Count} files from SquashFS image '{filePath}' to '{outputDirectory}'.",
                    [.. files]);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail($"Failed to unpack SquashFS image '{filePath}': {ex.Message}");
            }
        }, cancellationToken);
}