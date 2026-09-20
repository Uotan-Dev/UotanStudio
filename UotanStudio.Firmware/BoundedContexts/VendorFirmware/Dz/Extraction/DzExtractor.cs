using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Dz.Models;
using System.Security.Cryptography;
using UotanStudio.Firmware.BoundedContexts.Compression;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Streams;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Dz.Extraction;

public sealed class DzChunk
{
    public required DzChunkHeader Header { get; init; }

    public long DataOffset { get; init; }

    public string Name => Header.GetChunkName();
}

public static class DzExtractor
{
    /// <summary>
    /// Reconstructs one partition using an explicit physical layout and verifies every decoded chunk.
    /// <para>按显式物理布局重建一个分区，并校验每个解压块；空洞填零。</para>
    /// </summary>
    /// <param name="dzPath">The DZ source path. DZ 源路径。</param>
    /// <param name="outputPath">The partition image path. 分区镜像路径。</param>
    /// <param name="layout">The layout obtained from the device partition table. 从设备分区表取得的布局。</param>
    /// <param name="cancellationToken">The cancellation token. 取消令牌。</param>
    public static void ExtractPartition(string dzPath, string outputPath, DzPartitionLayout layout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.LogicalBlockSize is < 512 or > 65536 ||
            (layout.LogicalBlockSize & (layout.LogicalBlockSize - 1)) != 0 ||
            layout.StartSector < 0 || layout.SectorCount <= 0)
            throw new ArgumentException("Invalid DZ partition layout.", nameof(layout));
        var (_, allChunks) = Parse(dzPath);
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
        using var source = File.OpenRead(dzPath);
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
    public static (DzFileHeader Header, List<DzChunk> Chunks) Parse(string dzPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(dzPath);

        using var fs = new FileStream(dzPath, FileMode.Open, FileAccess.Read);

        var headerBuffer = new byte[DzFileHeader.Size];
        fs.ReadExactly(headerBuffer);
        var header = DzFileHeader.FromBytes(headerBuffer);
        if (!header.Header.AsSpan().SequenceEqual(DzConstants.DzFileHeader))
            throw new InvalidDataException("Unsupported DZ file format.");
        if (header.ChunkCount > (fs.Length - DzFileHeader.Size) / DzChunkHeader.Size)
            throw new InvalidDataException("The DZ chunk table is truncated.");

        var chunks = new List<DzChunk>();
        var chunkBuffer = new byte[DzChunkHeader.Size];
        for (uint index = 0; index < header.ChunkCount; index++)
        {
            fs.ReadExactly(chunkBuffer);
            var chunk = DzChunkHeader.FromBytes(chunkBuffer);
            if (!chunk.Header.AsSpan().SequenceEqual(DzConstants.DzChunkHeader))
                throw new InvalidDataException($"The DZ chunk {index} has invalid magic.");
            FileExtraction.ValidateRange(fs.Position, chunk.DataSize, fs.Length, "DZ chunk data");
            chunks.Add(new DzChunk { Header = chunk, DataOffset = fs.Position });
            fs.Position = checked(fs.Position + chunk.DataSize);
        }

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

        var (_, chunks) = Parse(dzPath);
        using var fs = new FileStream(dzPath, FileMode.Open, FileAccess.Read);
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
        if (chunk.Header.Md5.Any(value => value != 0))
        {
            output.Flush();
            using var decoded = new BoundedReadStream(output, start, chunk.Header.TargetSize, leaveOpen: true);
            if (!MD5.HashData(decoded).AsSpan().SequenceEqual(chunk.Header.Md5))
                throw new InvalidDataException($"DZ chunk '{chunk.Name}' MD5 does not match.");
            output.Position = start + chunk.Header.TargetSize;
        }
    }
}

/// <summary>Defines the physical layout needed to rebuild a DZ partition.<para>定义重建 DZ 分区所需的物理布局。</para></summary>
/// <param name="Name">The DZ slice name. DZ 分区名。</param>
/// <param name="Device">The storage device identifier. 存储设备标识。</param>
/// <param name="StartSector">The partition start LBA. 分区起始 LBA。</param>
/// <param name="SectorCount">The partition sector count. 分区扇区数。</param>
/// <param name="LogicalBlockSize">The logical sector size. 逻辑扇区大小。</param>
public sealed record DzPartitionLayout(string Name, uint Device, long StartSector, long SectorCount,
    int LogicalBlockSize = 512);