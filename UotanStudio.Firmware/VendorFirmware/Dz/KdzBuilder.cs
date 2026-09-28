using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.VendorFirmware.Dz;

namespace UotanStudio.Firmware.VendorFirmware.Dz;

/// <summary>Identifies one opaque member of a KDZ container.
/// <para>标识 KDZ 容器中的一个不透明成员。</para></summary>
/// <param name="Name">Safe relative name encoded as UTF-8 in the table.
/// <para>目录表中以 UTF-8 编码的安全相对名称。</para></param>
/// <param name="FilePath">Path of the member's complete bytes.
/// <para>包含成员完整字节的路径。</para></param>
internal sealed record KdzSource(string Name, string FilePath);

/// <summary>Builds LG KDZ outer containers without modifying or signing embedded members.
/// <para>构建 LG KDZ 外层容器，不修改或签名内嵌成员。</para></summary>
internal static class KdzBuilder
{
    internal const int MaximumEntries = 65536;
    internal const long MaximumLayoutBytes = 64 * 1024 * 1024;
    internal static readonly Encoding NameEncoding = new UTF8Encoding(false, true);

    /// <summary>Writes and verifies a KDZ v2 container before atomically replacing the destination.
    /// <para>写入并验证 KDZ v2 容器后原子替换目标文件。</para></summary>
    /// <param name="entries">Members in directory order; names must be unique.
    /// <para>按目录顺序排列的成员；名称必须唯一。</para></param>
    /// <param name="outputPath">Destination file, preserved on failure or cancellation.
    /// <para>目标文件，失败或取消时保留既有内容。</para></param>
    /// <param name="dataStart">First payload offset, at least the directory size and at most 64 MiB.
    /// <para>首个负载的偏移，至少容纳目录且不超过 64 MiB。</para></param>
    /// <param name="payloadOrder">Permutation of directory indexes specifying physical member order, or null.
    /// <para>指定物理成员顺序的目录索引排列；可为空。</para></param>
    /// <param name="cancellationToken">Cancels copying, hashing and publication.
    /// <para>取消复制、哈希计算和发布。</para></param>
    /// <returns>A task completing after verified publication.
    /// <para>经验证的输出发布完成后结束的任务。</para></returns>
    public static Task BuildAsync(IReadOnlyList<KdzSource> entries, string outputPath, long dataStart = 4096,
        IReadOnlyList<int>? payloadOrder = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var sources = entries.ToArray();
        var order = payloadOrder?.ToArray() ?? Enumerable.Range(0, sources.Length).ToArray();
        return Task.Run(() =>
        {
            ValidateSources(sources, outputPath, cancellationToken);
            ValidateOrder(order, sources.Length);
            var tableSize = checked(9L + KdzHeader.Size * sources.Length);
            if (dataStart < tableSize || dataStart > MaximumLayoutBytes)
            {
                throw new InvalidDataException("KDZ data start cannot contain the table or exceeds the layout limit.");
            }

            var offsets = Enumerable.Range(0, sources.Length)
                .Select(index => 8L + index * KdzHeader.Size + (index == sources.Length - 1 ? 1 : 0)).ToArray();
            var gaps = new long[sources.Length + 1];
            gaps[0] = dataStart;
            Write(sources, outputPath, order, offsets, gaps, null, null, 2, cancellationToken);
        }, cancellationToken);
    }

    internal static void ValidateSources(KdzSource[] sources, string outputPath, CancellationToken token)
    {
        if (sources.Length is < 1 or > MaximumEntries)
        {
            throw new InvalidDataException("KDZ requires between 1 and 65536 members.");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var output = Path.GetFullPath(outputPath);
        foreach (var source in sources)
        {
            token.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(source);
            ArgumentException.ThrowIfNullOrWhiteSpace(source.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(source.FilePath);
            var normalized = source.Name.Replace('\\', '/');
            if (!names.Add(normalized) || NameEncoding.GetByteCount(source.Name) > 256
                || source.Name[0] is '\0' or '\x03')
            {
                throw new InvalidDataException("KDZ member names must be unique and fit in 256 UTF-8 bytes.");
            }
            FileExtraction.ResolvePath(Path.GetTempPath(), source.Name);

            if (string.Equals(Path.GetFullPath(source.FilePath), output, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The KDZ output must not overwrite an input member.");
            }
        }
    }

    internal static void ValidateOrder(int[] order, int count)
    {
        if (order.Length != count || order.Any(index => index < 0 || index >= count)
            || order.Distinct().Count() != count)
        {
            throw new InvalidDataException("KDZ physical order must be a permutation of its directory indexes.");
        }
    }

    internal static void Write(KdzSource[] sources, string outputPath, int[] order, long[] recordOffsets,
        long[] gaps, string? layoutPath, string? layoutHash, int headerType, CancellationToken token)
    {
        var headerMagic = headerType switch
        {
            0 => DzConstants.KdzHeaderV0,
            1 => DzConstants.KdzHeaderV1,
            2 => DzConstants.KdzHeaderV2,
            _ => throw new InvalidDataException("Unsupported KDZ header type.")
        };
        ValidateSources(sources, outputPath, token);
        ValidateOrder(order, sources.Length);
        if (gaps.Length != sources.Length + 1 || gaps.Any(length => length < 0 || length > MaximumLayoutBytes)
            || gaps.Sum() > MaximumLayoutBytes || recordOffsets.Length != sources.Length)
        {
            throw new InvalidDataException("Invalid KDZ preservation layout.");
        }

        using var layout = layoutPath is null ? null : File.OpenRead(layoutPath);
        if (layout is not null)
        {
            if (layout.Length != gaps.Sum()
                || !string.Equals(Convert.ToHexString(HashRange(layout, layout.Length, token)), layoutHash,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("KDZ preservation bytes do not match their recorded digest.");
            }
            layout.Position = 0;
            ValidatePreservedHeader(layout, sources, recordOffsets, gaps[0], headerMagic, token);
            layout.Position = 0;
        }

        FileExtraction.WriteAtomic(outputPath, output =>
        {
            var buffer = new byte[64 * 1024];
            var offsets = new long[sources.Length];
            var lengths = new long[sources.Length];
            var hashes = new byte[sources.Length][];
            for (var physical = 0; physical <= order.Length; physical++)
            {
                CopyGap(layout, output, gaps[physical], buffer, token);
                if (physical == order.Length)
                {
                    break;
                }

                var index = order[physical];
                using var member = new FileStream(sources[index].FilePath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, buffer.Length, FileOptions.SequentialScan);
                offsets[index] = output.Position;
                lengths[index] = member.Length;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                Copy(member, output, member.Length, buffer, token, hash);
                if (member.ReadByte() != -1 || member.Length != lengths[index])
                {
                    throw new InvalidDataException("A KDZ source changed length during packing.");
                }
                hashes[index] = hash.GetHashAndReset();
            }

            if (layout is null)
            {
                output.Position = 0;
                output.Write(headerMagic);
                for (var index = 0; index < sources.Length; index++)
                {
                    token.ThrowIfCancellationRequested();
                    if (index == sources.Length - 1)
                    {
                        output.WriteByte(3);
                    }
                    var record = new byte[KdzHeader.Size];
                    NameEncoding.GetBytes(sources[index].Name, record.AsSpan(0, 256));
                    output.Write(record);
                }
            }

            Span<byte> numbers = stackalloc byte[16];
            for (var index = 0; index < sources.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                if (recordOffsets[index] < 8 || recordOffsets[index] > gaps[0] - KdzHeader.Size)
                {
                    throw new InvalidDataException("A KDZ record is outside the preserved header.");
                }
                output.Position = recordOffsets[index] + 256;
                BinaryPrimitives.WriteUInt64LittleEndian(numbers, (ulong)lengths[index]);
                BinaryPrimitives.WriteUInt64LittleEndian(numbers[8..], (ulong)offsets[index]);
                output.Write(numbers);
            }
            output.Flush();
            var parsed = KdzExtractor.Parse(output, token);
            if (parsed.HeaderType != headerType || parsed.Partitions.Count != sources.Length)
            {
                throw new InvalidDataException("KDZ output directory verification failed.");
            }
            for (var index = 0; index < sources.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var part = parsed.Partitions[index];
                if (part.Name != sources[index].Name || part.Offset != (ulong)offsets[index]
                    || part.Length != (ulong)lengths[index])
                {
                    throw new InvalidDataException("KDZ output member layout verification failed.");
                }
                output.Position = offsets[index];
                if (!CryptographicOperations.FixedTimeEquals(HashRange(output, lengths[index], token), hashes[index]))
                {
                    throw new InvalidDataException("KDZ output member digest verification failed.");
                }
            }
            output.Flush(true);
        }, cancellationToken: token);
    }

    internal static byte[] HashRange(Stream input, long length, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Copy(input, Stream.Null, length, new byte[64 * 1024], token, hash);
        return hash.GetHashAndReset();
    }

    private static void ValidatePreservedHeader(Stream layout, KdzSource[] sources, long[] recordOffsets,
        long prefixLength, byte[] headerMagic, CancellationToken token)
    {
        Span<byte> magic = stackalloc byte[8];
        layout.ReadExactly(magic);
        if (!magic.SequenceEqual(headerMagic))
        {
            throw new InvalidDataException("Preserved KDZ header is not version 2.");
        }
        var record = new byte[KdzHeader.Size];
        var finalMarker = false;
        for (var index = 0; index < sources.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var marker = layout.ReadByte();
            finalMarker = marker == 3;
            if (marker <= 0 || (finalMarker && index != sources.Length - 1))
            {
                throw new InvalidDataException("Preserved KDZ directory terminator does not match its member count.");
            }
            if (!finalMarker)
            {
                layout.Position--;
            }
            if (recordOffsets[index] != layout.Position || prefixLength - layout.Position < record.Length)
            {
                throw new InvalidDataException("Preserved KDZ record offsets are invalid.");
            }
            layout.ReadExactly(record);
            if (NameEncoding.GetString(record.AsSpan(0, 256)).TrimEnd('\0') != sources[index].Name)
            {
                throw new InvalidDataException("Preserved KDZ member name does not match its metadata.");
            }
        }
        if (!finalMarker && (layout.Position >= prefixLength || layout.ReadByte() != 0))
        {
            throw new InvalidDataException("Preserved KDZ directory has no valid terminator.");
        }
    }

    internal static void Copy(Stream input, Stream output, long length, byte[] buffer, CancellationToken token,
        IncrementalHash? hash = null)
    {
        while (length > 0)
        {
            token.ThrowIfCancellationRequested();
            var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, length));
            if (read == 0)
            {
                throw new EndOfStreamException("A KDZ input ended before its declared length.");
            }
            hash?.AppendData(buffer, 0, read);
            output.Write(buffer, 0, read);
            length -= read;
        }
        token.ThrowIfCancellationRequested();
    }

    private static void CopyGap(Stream? input, Stream output, long length, byte[] buffer, CancellationToken token)
    {
        if (input is not null)
        {
            Copy(input, output, length, buffer, token);
            return;
        }
        Array.Clear(buffer);
        while (length > 0)
        {
            token.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length, length);
            output.Write(buffer, 0, count);
            length -= count;
        }
    }
}