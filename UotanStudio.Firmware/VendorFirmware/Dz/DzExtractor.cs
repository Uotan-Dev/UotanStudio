using System.Security.Cryptography;
using UotanStudio.Firmware.Compression;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Streams;
using UotanStudio.Firmware.VendorFirmware.Dz;

namespace UotanStudio.Firmware.VendorFirmware.Dz;

/// <summary>A parsed DZ chunk: its 512-byte header plus the stream position of its compressed payload.
/// <para>已解析的 DZ 块：512 字节头部及其压缩负载在流中的位置。</para></summary>
public sealed class DzChunk
{
    /// <summary>The chunk header record.
    /// <para>块头记录。</para></summary>
    /// <value>The parsed header carrying magic, names, sizes, MD5 and addressing fields; required.
    /// <para>已解析的头部，包含魔数、名称、大小、MD5 与寻址字段；必填。</para></value>
    public required DzChunkHeader Header { get; init; }

    /// <summary>Stream position where this chunk's compressed payload begins.
    /// <para>此块压缩负载在流中的起始位置。</para></summary>
    /// <value>Zero-based byte offset recorded while parsing; payload reads stay bounded to the header's declared data size.
    /// <para>解析期间记录的从零开始字节偏移；对负载的读取始终限定在头部声明的数据大小内。</para></value>
    public long DataOffset { get; init; }

    /// <summary>The chunk's file name decoded from the header.
    /// <para>从头部解码出的块文件名。</para></summary>
    /// <value>UTF-8 chunk name with trailing NUL characters trimmed; used as the extraction output name.
    /// <para>按 UTF-8 解码并去除结尾 NUL 的块名；用作解包输出文件名。</para></value>
    public string Name => Header.GetChunkName();
}

/// <summary>Parses, extracts and rebuilds chunks from LG DZ firmware containers, treating inputs as untrusted.
/// <para>解析、提取并重建 LG DZ 固件容器中的块，输入一律视为不可信。</para></summary>
internal static class DzExtractor
{
    /// <summary>
    /// Reconstructs one partition using an explicit physical layout and verifies every decoded chunk.
    /// <para>按显式物理布局重建一个分区，并校验每个解压块；空洞填零。</para>
    /// </summary>
    /// <param name="dzPath">The DZ source path. <para>DZ 源路径。</para></param>
    /// <param name="outputPath">The partition image path. <para>分区镜像路径。</para></param>
    /// <param name="layout">The layout obtained from the device partition table. <para>从设备分区表取得的布局。</para></param>
    /// <param name="cancellationToken">The cancellation token. <para>取消令牌。</para></param>
    /// <exception cref="ArgumentException">
    /// The layout's logical block size is not a power of two within 512..65536, or its start sector is
    /// negative, or its sector count is zero or negative.
    /// <para>布局的逻辑块大小不是 512..65536 范围内的 2 的幂，或起始扇区为负，或扇区数为零或负数。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// No chunk matches the layout's device and slice name, a chunk exceeds the partition length, or chunks
    /// overlap.
    /// <para>没有块匹配布局的设备与切片名、块超出分区长度，或块之间重叠。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> is signaled before or during chunk decoding.
    /// <para><paramref name="cancellationToken"/> 在块解码之前或期间被触发。</para>
    /// </exception>
    public static void ExtractPartition(string dzPath, string outputPath, DzPartitionLayout layout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.LogicalBlockSize is < 512 or > 65536 ||
            (layout.LogicalBlockSize & (layout.LogicalBlockSize - 1)) != 0 ||
            layout.StartSector < 0 || layout.SectorCount <= 0)
            throw new ArgumentException("Invalid DZ partition layout.", nameof(layout));
        using var source = File.OpenRead(dzPath);
        var (_, allChunks) = Parse(source, cancellationToken);
        var chunks = allChunks.Where(c => c.Header.Dev == layout.Device &&
            c.Header.GetSliceName() == layout.Name).OrderBy(c => c.Header.TargetAddr).ToList();
        if (chunks.Count == 0) throw new InvalidDataException("DZ partition has no matching chunks.");
        var length = checked(layout.SectorCount * layout.LogicalBlockSize);
        long previousEnd = 0;
        foreach (var chunk in chunks)
        {
            var offset = checked(((long)chunk.Header.TargetAddr - layout.StartSector) * layout.LogicalBlockSize);
            var trimLength = checked((long)chunk.Header.TrimCount * layout.LogicalBlockSize);
            FileExtraction.ValidateRange(offset, Math.Max(trimLength, chunk.Header.TargetSize), length, "DZ partition chunk");
            if (offset < previousEnd) throw new InvalidDataException("DZ partition chunks overlap.");
            previousEnd = checked(offset + Math.Max(trimLength, chunk.Header.TargetSize));
        }
        FileExtraction.WriteAtomic(outputPath, output =>
        {
            output.SetLength(length);
            foreach (var chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.Position = checked(((long)chunk.Header.TargetAddr - layout.StartSector) * layout.LogicalBlockSize);
                DecompressChunk(source, chunk, output, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
        });
    }
    /// <summary>
    /// Parses a DZ file, returning the file header and chunk list.
    /// <para>解析 DZ 文件,返回文件头与块列表。</para>
    /// </summary>
    /// <param name="dzPath">Path of an existing DZ file; must not be null or empty. <para>已存在的 DZ 文件路径；不能为 <see langword="null"/> 或空字符串。</para></param>
    /// <returns>The validated file header and the chunks in table order, each carrying the offset of its payload.
    /// <para>已校验的文件头以及按表序排列、附带各自负载偏移的块列表。</para></returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="dzPath"/> is null or empty.
    /// <para><paramref name="dzPath"/> 为 <see langword="null"/> 或空字符串。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The file header magic does not match, the chunk table is truncated for the declared chunk count,
    /// a chunk header magic is invalid, or a chunk's declared data exceeds the file bounds.
    /// <para>文件头魔数不匹配、按声明块数计算时块表被截断、块头魔数无效，或块声明的数据超出文件边界。</para>
    /// </exception>
    public static (DzFileHeader Header, List<DzChunk> Chunks) Parse(string dzPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(dzPath);

        using var fs = new FileStream(dzPath, FileMode.Open, FileAccess.Read);
        return Parse(fs, CancellationToken.None);
    }

    internal static (DzFileHeader Header, List<DzChunk> Chunks) Parse(Stream fs, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        fs.Position = 0;
        if (fs.Length < DzFileHeader.Size)
        {
            throw new InvalidDataException("The DZ file header is truncated.");
        }
        var headerBuffer = new byte[DzFileHeader.Size];
        fs.ReadExactly(headerBuffer);
        var header = DzFileHeader.FromBytes(headerBuffer);
        if (!header.Header.AsSpan().SequenceEqual(DzConstants.DzFileHeader))
            throw new InvalidDataException("Unsupported DZ file format.");
        if (header.ChunkCount > DzBuilder.MaximumChunks || header.ChunkCount > (fs.Length - DzFileHeader.Size) / DzChunkHeader.Size)
        {
            throw new InvalidDataException("The DZ chunk table is truncated or exceeds the entry limit.");
        }

        var chunks = new List<DzChunk>();
        var chunkBuffer = new byte[DzChunkHeader.Size];
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        for (uint index = 0; index < header.ChunkCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileExtraction.ValidateRange(fs.Position, DzChunkHeader.Size, fs.Length, "DZ chunk header");
            fs.ReadExactly(chunkBuffer);
            digest.AppendData(chunkBuffer);
            var chunk = DzChunkHeader.FromBytes(chunkBuffer);
            if (!chunk.Header.AsSpan().SequenceEqual(DzConstants.DzChunkHeader))
                throw new InvalidDataException($"The DZ chunk {index} has invalid magic.");
            FileExtraction.ValidateRange(fs.Position, chunk.DataSize, fs.Length, "DZ chunk data");
            chunks.Add(new DzChunk { Header = chunk, DataOffset = fs.Position });
            fs.Position = checked(fs.Position + chunk.DataSize);
        }

        if (header.Md5.Any(value => value != 0) && !digest.GetHashAndReset().AsSpan().SequenceEqual(header.Md5))
        {
            throw new InvalidDataException("DZ chunk header MD5 does not match.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        return (header, chunks);
    }

    /// <summary>
    /// Decompresses and verifies each selected DZ chunk before committing its output.
    /// <para>解压并验证选中的 DZ 块，然后提交各块的输出文件。</para>
    /// </summary>
    /// <param name="dzPath">The DZ source path. <para>DZ 源路径。</para></param>
    /// <param name="outDir">The output directory. <para>输出目录。</para></param>
    /// <param name="nameFilter">The optional chunk filter. <para>可选的块名筛选器。</para></param>
    /// <param name="overwrite">Whether to replace existing files. <para>是否替换已有文件。</para></param>
    /// <param name="cancellationToken">The cancellation token. <para>取消令牌。</para></param>
    /// <returns>The committed chunk count. <para>已提交的块数。</para></returns>
    public static int Extract(string dzPath, string outDir, Func<string, bool>? nameFilter = null,
        bool overwrite = true, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(dzPath);
        ArgumentException.ThrowIfNullOrEmpty(outDir);
        cancellationToken.ThrowIfCancellationRequested();

        using var fs = new FileStream(dzPath, FileMode.Open, FileAccess.Read);
        var (_, chunks) = Parse(fs, cancellationToken);
        var count = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = chunk.Name;
            if (nameFilter is null || nameFilter(name))
            {
                var outPath = FileExtraction.ResolvePath(outDir, name);
                if (!names.Add(outPath))
                    throw new InvalidDataException($"Duplicate DZ chunk output name '{name}'.");
                FileExtraction.WriteAtomic(outPath, output =>
                {
                    DecompressChunk(fs, chunk, output, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                }, overwrite);
                count++;
            }
        }

        return count;
    }

    internal static void DecompressChunk(Stream source, DzChunk chunk, FileStream output,
        CancellationToken cancellationToken = default)
    {
        using var data = new BoundedReadStream(source, chunk.DataOffset, chunk.Header.DataSize, leaveOpen: true);
        Span<byte> magic = stackalloc byte[4];
        data.ReadExactly(magic);
        data.Position = 0;
        var format = magic.SequenceEqual(new byte[] { 0x28, 0xB5, 0x2F, 0xFD }) ? CompressionFormat.Zstd
            : (magic[0] & 15) == 8 && ((magic[0] << 8) | magic[1]) % 31 == 0 ? CompressionFormat.Zlib
            : throw new InvalidDataException($"DZ chunk '{chunk.Name}' has an unsupported compression header.");
        var start = output.Position;
        CompressionService.Decompress(data, output, format, chunk.Header.TargetSize, cancellationToken);
        if (output.Position - start != chunk.Header.TargetSize)
            throw new InvalidDataException($"DZ chunk '{chunk.Name}' decompressed to an unexpected size.");
        if (chunk.Header.Md5.Any(value => value != 0) || chunk.Header.Crc32 != 0)
        {
            output.Flush();
            using var decoded = new BoundedReadStream(output, start, chunk.Header.TargetSize, leaveOpen: true);
            var hashes = DzBuilder.HashContent(decoded, cancellationToken);
            if (chunk.Header.Md5.Any(value => value != 0) && !hashes.Md5.AsSpan().SequenceEqual(chunk.Header.Md5))
            {
                throw new InvalidDataException($"DZ chunk '{chunk.Name}' MD5 does not match.");
            }
            if (chunk.Header.Crc32 != 0 && hashes.Crc32 != chunk.Header.Crc32)
            {
                throw new InvalidDataException($"DZ chunk '{chunk.Name}' CRC32 does not match.");
            }
            output.Position = start + chunk.Header.TargetSize;
        }
    }
}

/// <summary>Defines the physical layout needed to rebuild a DZ partition.<para>定义重建 DZ 分区所需的物理布局。</para></summary>
/// <param name="Name">The DZ slice name. <para>DZ 分区名。</para></param>
/// <param name="Device">The storage device identifier. <para>存储设备标识。</para></param>
/// <param name="StartSector">The partition start LBA. <para>分区起始 LBA。</para></param>
/// <param name="SectorCount">The partition sector count. <para>分区扇区数。</para></param>
/// <param name="LogicalBlockSize">The logical sector size. <para>逻辑扇区大小。</para></param>
internal sealed record DzPartitionLayout(string Name, uint Device, long StartSector, long SectorCount,
    int LogicalBlockSize = 512);