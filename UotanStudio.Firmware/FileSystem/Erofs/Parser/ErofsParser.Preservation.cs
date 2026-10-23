using System.Text.Json.Nodes;
using UotanStudio.Firmware.Core.Streams;
using UotanStudio.Firmware.FileSystem.Extraction;
using UotanStudio.Firmware.FileSystem.Metadata;
using UotanStudio.Firmware.FileSystem.Rebuilding;

namespace UotanStudio.Firmware.FileSystem.Erofs.Parser;

/// <summary>
/// EROFS parser part that freezes everything repack-metadata capture reads — the entry list the flat export and
/// the preservation payload consume, and the parameters an edited rebuild inherits — so extraction and capture
/// share one walk of the image instead of capturing through additional parsers.
/// <para>EROFS 解析器的组成部分，冻结重打包元数据采集所读取的全部内容——扁平导出与保留负载消费的条目清单，以及编辑
/// 重建所继承的参数——使解包与采集共享一次镜像遍历，而不是经额外解析器采集。</para>
/// </summary>
public sealed partial class ErofsParser
{
    /// <summary>
    /// Captures the entry list, its preservation projection and the rebuild parameters from this parser in one
    /// pass; the tree must already be enumerated, which extraction does.
    /// <para>在一次遍历中从本解析器采集条目清单、其保留投影与重建参数；目录树必须已经枚举，解包阶段即是如此。</para>
    /// </summary>
    /// <param name="sourceLength">Length in bytes of the source image this parser reads.
    /// <para>本解析器所读源镜像的字节长度。</para></param>
    /// <param name="digest">Background source digest whose result capture inserts, or <see langword="null"/> when the
    /// caller computed none; ownership transfers to the returned snapshot.
    /// <para>采集将插入其结果的来源摘要后台任务；调用方未计算时为 <see langword="null"/>。所有权转移给返回的快照。</para></param>
    /// <param name="sourceStream">Primary source stream disposed with the snapshot, or <see langword="null"/> when the
    /// caller keeps ownership. <para>随快照释放的主来源流；调用方保留所有权时为 <see langword="null"/>。</para></param>
    /// <param name="cancellationToken">Cancels the rebuild-profile capture.
    /// <para>取消重建档案采集。</para></param>
    /// <returns>The frozen snapshot; the caller disposes it after the flat export and the preservation payload are
    /// written. <para>冻结的快照；调用方在扁平导出与保留负载写出后释放它。</para></returns>
    /// <exception cref="FileSystemCorruptedException">The image metadata cannot be represented by a rebuild profile.
    /// <para>镜像元数据无法用重建档案表示。</para></exception>
    internal ErofsPreservationSnapshot CreatePreservationSnapshot(long sourceLength,
        SourceDigestComputation? digest, Stream? sourceStream, CancellationToken cancellationToken)
    {
        var entries = EnumerateEntries();
        var options = ReadRebuildOptions();
        var extent = checked((long)Superblock.BlockCount64 * options.BlockSize);
        var rebuild = new RebuildParameters
        {
            Format = "EROFS",
            BlockSize = options.BlockSize,
            Uuid = options.Uuid,
            VolumeName = options.VolumeName,
            SourceLength = sourceLength,
            SourceFilesystemBytes = extent,
            Erofs = ErofsRebuildMetadata.Capture(options, this, extent, cancellationToken),
        };
        var preserved = new List<PreservedInodeEntry>(entries.Count);
        foreach (var entry in entries)
        {
            preserved.Add(new PreservedInodeEntry
            {
                Path = entry.Path,
                Size = entry.Size,
                Inode = entry.Nid,
                DeviceId = entry.DeviceId,
                Mode = entry.Mode,
                Uid = entry.Uid,
                Gid = entry.Gid,
                Metadata = new FileSystemInodeMetadata
                {
                    ModificationTime = new(checked((long)entry.MTime), entry.MTimeNanoseconds),
                    LinkCount = checked((uint)entry.LinkCount),
                },
                SymlinkTarget = (entry.Mode & 0xF000) == 0xA000 ? ReadPreservedSymlinkTarget(entry.Path) : null,
                Xattrs = entry.Xattrs?.GroupBy(x => x.Name, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.First().Value, StringComparer.Ordinal),
            });
        }

        return new ErofsPreservationSnapshot(this, sourceStream, entries, preserved, rebuild, digest);
    }

    /// <summary>
    /// Reads one symlink target the way preservation capture records it: through the parser's own file reader and
    /// the portable name encoding, not through the walk's display text.
    /// <para>按保留采集记录的方式读取一个符号链接目标：经解析器自身的文件读取与可移植名称编码，而不是遍历的展示文本。</para>
    /// </summary>
    /// <param name="path">Exact image path of the symlink entry. <para>符号链接条目的精确镜像路径。</para></param>
    /// <returns>The decoded target text; empty when the target carries no bytes.
    /// <para>解码后的目标文本；目标不含字节时为空字符串。</para></returns>
    private string ReadPreservedSymlinkTarget(string path)
    {
        using var bytes = new MemoryStream();
        using var bounded = new CountingWriteStream(bytes, 1024 * 1024, leaveOpen: true);
        ReadFile(path, bounded);
        return PortableExtractionPaths.NameEncoding.GetString(bytes.GetBuffer().AsSpan(0, (int)bytes.Length));
    }
}

/// <summary>
/// Frozen extraction-time view of one EROFS image: the listing the flat export walks, the preservation entries
/// already projected from it, and the rebuild parameters captured from the same parse. It also owns the live
/// parser, the primary source stream and the background digest, so capture must dispose it once the metadata is
/// written; disposing closes the source.
/// <para>一幅 EROFS 镜像在解包时的冻结视图：扁平导出所遍历的清单、已由其投影的保留条目，以及同一次解析采集的重建
/// 参数。它还持有活动解析器、主来源流与后台摘要，因此采集必须在元数据写出后释放它；释放即关闭来源。</para>
/// </summary>
internal sealed class ErofsPreservationSnapshot : IDisposable
{
    private readonly ErofsParser _parser;
    private readonly Stream? _sourceStream;
    private readonly SourceDigestComputation? _digest;
    private bool _disposed;

    internal ErofsPreservationSnapshot(ErofsParser parser, Stream? sourceStream,
        IReadOnlyList<ErofsFileEntry> entries, IReadOnlyList<PreservedInodeEntry> preservedEntries,
        RebuildParameters rebuild, SourceDigestComputation? digest)
    {
        _parser = parser;
        _sourceStream = sourceStream;
        _digest = digest;
        Entries = entries;
        PreservedEntries = preservedEntries;
        Rebuild = rebuild;
    }

    /// <summary>Gets the listing frozen from the parser's own enumeration.
    /// <para>获取由解析器自身枚举冻结的清单。</para>
    /// </summary>
    /// <value>The same entries the parser caches, in traversal order followed by the file listing.
    /// <para>与解析器缓存的条目相同，按遍历顺序的目录条目在前，文件清单在后。</para></value>
    internal IReadOnlyList<ErofsFileEntry> Entries { get; }

    /// <summary>Gets the preservation entries recorded into the payload.
    /// <para>获取记录进负载的保留条目。</para>
    /// </summary>
    /// <value>One entry per frozen listing entry, in the same order.
    /// <para>每条冻结清单条目对应一项，顺序一致。</para></value>
    internal IReadOnlyList<PreservedInodeEntry> PreservedEntries { get; }

    /// <summary>Gets the rebuild parameters captured from the same parse.
    /// <para>获取同一次解析采集的重建参数。</para></summary>
    internal RebuildParameters Rebuild { get; }

    /// <summary>
    /// Writes the flat <c>fs_config</c>, <c>file_contexts</c> and <c>partition_info</c> exports from the frozen
    /// listing, which stays in the parser's cached enumeration.
    /// <para>从冻结清单写出扁平的 <c>fs_config</c>、<c>file_contexts</c> 与 <c>partition_info</c> 导出；该清单始终
    /// 保留在解析器的缓存枚举中。</para>
    /// </summary>
    /// <param name="metadataDirectory">Directory the flat files are written to.
    /// <para>写出扁平文件的目录。</para></param>
    /// <param name="partitionName">File-name prefix for the three exports.
    /// <para>三份导出文件使用的文件名前缀。</para></param>
    /// <param name="superblockConfig">Superblock object whose members become <c>partition.key=value</c> lines.
    /// <para>其成员成为 <c>分区.key=value</c> 行的超级块对象。</para></param>
    /// <returns>One message per skipped row plus a message describing a failed export; empty when complete.
    /// <para>每条被跳过的行对应一条说明，另有失败导出的一句话说明；完整时为空。</para></returns>
    internal IReadOnlyList<string> GenerateFlatMetadata(string metadataDirectory, string partitionName,
        JsonObject? superblockConfig)
        => FileSystemMetadataGenerator.GenerateForErofs(metadataDirectory, partitionName, _parser, superblockConfig);

    /// <summary>
    /// Resolves the source SHA-256 the background read computed, or <see langword="null"/> when capture has to hash
    /// the source itself. <para>解析后台读取算出的源 SHA-256；采集必须自行哈希来源时返回 <see langword="null"/>。</para>
    /// </summary>
    /// <returns>The uppercase hexadecimal digest, or <see langword="null"/> when none was started.
    /// <para>大写十六进制摘要；未启动摘要计算时为 <see langword="null"/>。</para></returns>
    internal string? ResolveSourceDigest() => _digest?.Resolve();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _parser.Dispose();
        _sourceStream?.Dispose();
        _digest?.Dispose();
    }
}