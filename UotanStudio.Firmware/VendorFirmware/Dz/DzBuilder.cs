using System.Buffers.Binary;
using System.Text;
using UotanStudio.Compress;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Streams;
using UotanStudio.Firmware.Core.Text;
using UotanStudio.Hash.Crc32Ieee;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Firmware.VendorFirmware.Dz;

/// <summary>
/// Supplies decoded chunk content and its preserved 512-byte DZ header.
/// <para>提供解压后的块内容及其保留的 512 字节 DZ 头。</para>
/// </summary>
/// <param name="Header">The complete chunk header template. 完整块头模板。</param>
/// <param name="FilePath">The decoded content path. 解压后内容的文件路径。</param>
/// <param name="Compression">Zlib or Zstandard encoding. Zlib 或 Zstandard 编码。</param>
public sealed record DzChunkSource(byte[] Header, string FilePath, CompressionFormat Compression = CompressionFormat.Zlib);

/// <summary>
/// Rebuilds DZ 2.1 containers with bounded compression and verified atomic publication.
/// <para>通过有界压缩和经验证的原子发布重建 DZ 2.1 容器。</para>
/// </summary>
/// <remarks>
/// Chunk order, addresses, trim ranges and opaque header bytes are retained; content digests and lengths are recalculated.
/// Compressed byte identity and OEM acceptance are not guaranteed.
/// <para>保留块顺序、地址、擦除范围和不透明头字节，重新计算内容摘要和长度。不保证压缩字节身份或 OEM 接受性。</para>
/// </remarks>
public static class DzBuilder
{
    internal const int MaximumChunks = 16384;
    private static readonly UTF8Encoding Names = new(false, true);

    /// <summary>
    /// Rebuilds a DZ container from preserved headers and decoded files.
    /// <para>根据保留的头部和解压后的文件重建 DZ 容器。</para>
    /// </summary>
    /// <param name="header">The complete 512-byte DZ 2.1 file header. 完整的 512 字节 DZ 2.1 文件头。</param>
    /// <param name="chunks">The ordered chunk sources. 按顺序排列的块来源。</param>
    /// <param name="outputPath">The verified output path. 经验证的输出路径。</param>
    /// <param name="logicalBlockSize">The explicitly verified sector size, or null to limit each chunk to its original decoded size.
    /// <para>经明确确认的扇区大小，或为 null 以将每个块限制在其原始解压大小内。</para></param>
    /// <param name="cancellationToken">The cancellation token. 取消令牌。</param>
    /// <returns>A task completing after verified publication. 验证并发布完成后的任务。</returns>
    public static Task BuildAsync(byte[] header, IReadOnlyList<DzChunkSource> chunks, string outputPath,
        int? logicalBlockSize = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(chunks);
        cancellationToken.ThrowIfCancellationRequested();
        if (chunks.Count is < 1 or > MaximumChunks)
        {
            throw new InvalidDataException("DZ chunk count exceeds the rebuild limit.");
        }
        var preservedHeader = header.ToArray();
        var sources = chunks.Select(source => source with { Header = [.. source.Header] }).ToArray();
        return Task.Run(() => Write(preservedHeader, sources, outputPath, logicalBlockSize, cancellationToken),
            cancellationToken);
    }

    internal static void ValidateHeader(byte[] header)
    {
        if (header.Length != DzFileHeader.Size || !header.AsSpan(0, 4).SequenceEqual(DzConstants.DzFileHeader)
            || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) != 2
            || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)) != 1)
        {
            throw new InvalidDataException("Only complete DZ 2.1 header templates are supported.");
        }
    }

    internal static string ValidateChunkHeader(byte[] header)
    {
        if (header.Length != DzChunkHeader.Size || !header.AsSpan(0, 4).SequenceEqual(DzConstants.DzChunkHeader))
        {
            throw new InvalidDataException("Invalid DZ chunk header template.");
        }
        var name = Names.GetStringTrimNul(header.AsSpan(36, 64));
        var slice = Names.GetStringTrimNul(header.AsSpan(4, 32));
        if (string.IsNullOrWhiteSpace(slice) || slice.Contains('\0'))
        {
            throw new InvalidDataException("Invalid DZ slice name.");
        }
        _ = FileExtraction.ResolvePath(Path.GetTempPath(), name);
        return name;
    }

    internal static void Write(byte[] header, DzChunkSource[] chunks, string outputPath, int? blockSize,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ValidateHeader(header);
        if (blockSize is { } size && (size is < 512 or > 65536 || (size & (size - 1)) != 0))
        {
            throw new ArgumentOutOfRangeException(nameof(blockSize));
        }
        var outputFullPath = Path.GetFullPath(outputPath);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ranges = new List<(uint Device, uint Start, ulong End)>(chunks.Length);
        foreach (var source in chunks)
        {
            token.ThrowIfCancellationRequested();
            var name = ValidateChunkHeader(source.Header);
            if (!names.Add(name.Replace('\\', '/')) || source.Compression is not (CompressionFormat.Zlib or CompressionFormat.Zstd))
            {
                throw new InvalidDataException("Duplicate DZ chunk name or unsupported compression.");
            }
            if (string.Equals(Path.GetFullPath(source.FilePath), outputFullPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("DZ output cannot replace an input chunk.");
            }
            var parsed = DzChunkHeader.FromBytes(source.Header);
            var capacity = blockSize is { } sectorSize ? (long)parsed.TrimCount * sectorSize : parsed.TargetSize;
            var length = new FileInfo(source.FilePath).Length;
            if (length > uint.MaxValue || length > capacity || parsed.TrimCount == 0)
            {
                throw new InvalidDataException("DZ chunk content exceeds its preserved capacity.");
            }
            ranges.Add((parsed.Dev, parsed.TargetAddr, (ulong)parsed.TargetAddr + parsed.TrimCount));
        }
        var sorted = ranges.OrderBy(range => range.Device).ThenBy(range => range.Start).ToArray();
        for (var index = 1; index < sorted.Length; index++)
        {
            if (sorted[index].Device == sorted[index - 1].Device && sorted[index].Start < sorted[index - 1].End)
            {
                throw new InvalidDataException("DZ chunk trim ranges overlap.");
            }
        }

        FileExtraction.WriteAtomic(outputPath, output =>
        {
            output.Write(header);
            using var headerDigest = Md5.CreateAccumulator();
            var expected = new List<byte[]>(chunks.Length);
            foreach (var source in chunks)
            {
                token.ThrowIfCancellationRequested();
                using var input = File.OpenRead(source.FilePath);
                var original = DzChunkHeader.FromBytes(source.Header);
                var capacity = blockSize is { } sectorSize ? (long)original.TrimCount * sectorSize : original.TargetSize;
                if (input.Length > capacity || input.Length > uint.MaxValue)
                {
                    throw new InvalidDataException("DZ input changed beyond its capacity.");
                }
                byte[] sha256;
                byte[] md5;
                uint crc32;
                var chunkHeader = source.Header.ToArray();
                var recordOffset = output.Position;
                output.Write(chunkHeader);
                using (var digesting = new DzContentDigestStream(input, leaveOpen: true))
                using (var counted = new CountingReadStream(digesting, leaveOpen: true, cancellationToken: token))
                using (var encoded = new CountingWriteStream(output, uint.MaxValue, leaveOpen: true, cancellationToken: token))
                {
                    CompressionService.Compress(counted, encoded, source.Compression);
                    if (counted.BytesRead != input.Length)
                    {
                        throw new InvalidDataException("DZ input changed during compression.");
                    }

                    (sha256, md5, crc32) = digesting.GetHashesAndReset();
                    BinaryPrimitives.WriteUInt32LittleEndian(chunkHeader.AsSpan(104), checked((uint)encoded.BytesWritten));
                }
                BinaryPrimitives.WriteUInt32LittleEndian(chunkHeader.AsSpan(100), checked((uint)input.Length));
                md5.CopyTo(chunkHeader, 108);
                BinaryPrimitives.WriteUInt32LittleEndian(chunkHeader.AsSpan(136), crc32);
                var end = output.Position;
                output.Position = recordOffset;
                output.Write(chunkHeader);
                output.Position = end;
                headerDigest.Append(chunkHeader);
                expected.Add(sha256);
            }
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(192), (uint)chunks.Length);
            headerDigest.GetHashAndReset().CopyTo(header, 196);
            output.Position = 0;
            output.Write(header);
            output.Flush();
            output.Position = 0;
            var parsed = DzExtractor.Parse(output, token);
            if (parsed.Chunks.Count != chunks.Length)
            {
                throw new InvalidDataException("DZ output chunk count mismatch.");
            }
            for (var index = 0; index < parsed.Chunks.Count; index++)
            {
                var chunk = parsed.Chunks[index];
                using var compressed = new BoundedReadStream(output, chunk.DataOffset, chunk.Header.DataSize, leaveOpen: true);
                using var decoded = new HashingWriteStream(Stream.Null, leaveOpen: true);
                CompressionService.Decompress(compressed, decoded, chunks[index].Compression, chunk.Header.TargetSize, token);
                if (decoded.BytesWritten != chunk.Header.TargetSize || !decoded.GetHashAndReset().AsSpan().SequenceEqual(expected[index]))
                {
                    throw new InvalidDataException("DZ output content verification failed.");
                }
            }
            token.ThrowIfCancellationRequested();
        }, cancellationToken: token);
    }

    internal static (byte[] Sha256, byte[] Md5, uint Crc32) HashContent(Stream input, CancellationToken token)
    {
        using var sha = Sha256.CreateAccumulator();
        using var md5 = Md5.CreateAccumulator();
        var crc = new Crc32IeeeAccumulator();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var count = input.Read(buffer);
            if (count == 0)
            {
                break;
            }
            sha.Append(buffer.AsSpan(0, count));
            md5.Append(buffer.AsSpan(0, count));
            crc.Append(buffer.AsSpan(0, count));
        }
        token.ThrowIfCancellationRequested();
        return (sha.GetHashAndReset(), md5.GetHashAndReset(), crc.GetCurrentHash());
    }
}