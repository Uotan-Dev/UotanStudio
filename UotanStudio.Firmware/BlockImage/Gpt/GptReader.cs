using System.Buffers.Binary;
using System.Text;
using UotanStudio.Hash.Crc32Ieee;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Firmware.BlockImage.Gpt;

/// <summary>
/// Bounded, read-only GPT 1.0 pair validation. Does not validate the protective MBR or repair media.
/// <para>有界、只读的 GPT 1.0 主备校验。不校验保护性 MBR，也不修复介质。</para>
/// </summary>
public static partial class GptReader
{
    private const uint MaximumEntries = 65536;
    private const uint MaximumEntrySize = 4096;
    private const ulong MaximumArrayBytes = 16 * 1024 * 1024;
    private static readonly UnicodeEncoding NameEncoding = new(false, false, true);

    /// <summary>
    /// Validates both headers and byte-identical entry arrays from a whole-device address space.
    /// <para>在整个设备地址空间上校验两个头部以及逐字节一致的条目数组。</para>
    /// </summary>
    /// <param name="source">
    /// Caller-owned readable, seekable stream; its position is restored on every exit.
    /// <para>由调用方持有的可读、可定位流；无论成功与否其位置都会恢复。</para>
    /// </param>
    /// <param name="blockSize">
    /// Logical block size, a power of two from 512 through 65536.
    /// <para>逻辑块大小，须为 512 至 65536 之间 2 的幂。</para>
    /// </param>
    /// <param name="blockCount">
    /// Device capacity in logical blocks; must fit signed stream addressing.
    /// <para>以逻辑块计的设备容量；须能用有符号流偏移寻址。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation checked between bounded reads.
    /// <para>在各次有界读取之间检查的取消令牌。</para>
    /// </param>
    /// <returns>
    /// Validated partition metadata and checked byte ranges, without reading partition contents.
    /// <para>已校验的分区元数据与经过检查的字节范围，不读取分区内容。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="source"/> cannot be read or seeked, or the geometry is out of range.
    /// <para><paramref name="source"/> 不可读或不可定位，或几何参数超出范围。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The signature, CRC, geometry, identities or entry arrays fail validation.
    /// <para>签名、CRC、几何、标识或条目数组未通过校验。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// The token was cancelled.
    /// <para>令牌已被取消。</para>
    /// </exception>
    public static GptTable Read(Stream source, int blockSize, ulong blockCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek)
        {
            throw new ArgumentException("GPT input must be readable and seekable.", nameof(source));
        }
        ValidateGeometry(blockSize, blockCount);
        var original = source.Position;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source.Length < (long)(blockCount * (uint)blockSize))
            {
                throw new InvalidDataException("The GPT address space is shorter than the declared device.");
            }
            var primary = ReadHeader(source, blockSize, blockCount, 1, cancellationToken);
            var backup = ReadHeader(source, blockSize, blockCount, blockCount - 1, cancellationToken);
            ValidatePair(primary, backup);
            using var builder = new TableBuilder(primary, backup, blockSize, blockCount);
            var left = new byte[(int)primary.Size];
            var right = new byte[left.Length];
            for (var index = 0; index < primary.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadAt(source, checked((long)(primary.ArrayLba * (uint)blockSize + (ulong)index * primary.Size)), left);
                ReadAt(source, checked((long)(backup.ArrayLba * (uint)blockSize + (ulong)index * primary.Size)), right);
                builder.Append(index, left, right);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return builder.Finish();
        }
        finally
        {
            source.Position = original;
        }
    }

    private sealed class TableBuilder(Header primary, Header backup, int blockSize, ulong blockCount) : IDisposable
    {
        private readonly List<GptPartition> _entries = [];
        private readonly HashSet<Guid> _ids = [];
        private readonly Crc32IeeeAccumulator _crc = new();
        private readonly DigestAccumulator _hash = CreateHash(primary, backup);

        private static DigestAccumulator CreateHash(Header primary, Header backup)
        {
            var hash = Sha256.CreateAccumulator();
            hash.Append(primary.Sha256);
            hash.Append(backup.Sha256);
            return hash;
        }

        public void Dispose() => _hash.Dispose();

        internal void Append(int index, ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
        {
            if (!left.SequenceEqual(right))
            {
                throw new InvalidDataException("The primary and backup GPT entry arrays differ.");
            }
            _crc.Append(left);
            _hash.Append(left);
            var type = new Guid(left[..16]);
            if (type == Guid.Empty)
            {
                return;
            }
            var id = new Guid(left[16..32]);
            var first = BinaryPrimitives.ReadUInt64LittleEndian(left[32..]);
            var last = BinaryPrimitives.ReadUInt64LittleEndian(left[40..]);
            var attributes = BinaryPrimitives.ReadUInt64LittleEndian(left[48..]);
            if (id == Guid.Empty || !_ids.Add(id) || first < primary.First || last > primary.Last || first > last)
            {
                throw new InvalidDataException("A GPT partition has an invalid identity or range.");
            }
            if (left[128..].ContainsAnyExcept((byte)0) || (attributes & 0x0000FFFFFFFFFFF8UL) != 0)
            {
                throw new InvalidDataException("A GPT partition contains nonzero reserved fields.");
            }
            var nameBytes = left.Slice(56, 72);
            var nameLength = 0;
            while (nameLength < nameBytes.Length && BinaryPrimitives.ReadUInt16LittleEndian(nameBytes[nameLength..]) != 0)
            {
                nameLength += 2;
            }
            string name;
            try
            {
                name = NameEncoding.GetString(nameBytes[..nameLength]);
            }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidDataException("A GPT partition name contains invalid UTF-16.", ex);
            }
            _entries.Add(new GptPartition(index, type, id, first, last, attributes, name,
                checked((long)(first * (uint)blockSize)), checked((long)((last - first + 1) * (uint)blockSize))));
        }

        internal GptTable Finish()
        {
            if (_crc.GetCurrentHash() != primary.ArrayCrc)
            {
                throw new InvalidDataException("The GPT partition array CRC is invalid.");
            }
            var ordered = _entries.OrderBy(entry => entry.FirstLba).ToArray();
            for (var index = 1; index < ordered.Length; index++)
            {
                if (ordered[index].FirstLba <= ordered[index - 1].LastLba)
                {
                    throw new InvalidDataException("GPT partitions overlap.");
                }
            }
            return new GptTable(primary.DiskId, blockSize, blockCount, _entries, Convert.ToHexString(_hash.GetHashAndReset()),
                primary.HeaderSize, primary.First, primary.Last, primary.Count, primary.Size, primary.ArrayLba, backup.ArrayLba);
        }
    }

    private static Header ReadHeader(Stream source, int blockSize, ulong blockCount, ulong lba,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = new byte[blockSize];
        ReadAt(source, checked((long)(lba * (uint)blockSize)), bytes);
        return ParseHeader(bytes, blockSize, blockCount, lba, null, null, null);
    }

    private static Header ParseHeader(Span<byte> span, int blockSize, ulong blockCount, ulong lba,
        ulong? partitionEntryLba, uint? partitionEntryCount, uint? partitionEntrySize)
    {
        if (!span[..8].SequenceEqual("EFI PART"u8) || BinaryPrimitives.ReadUInt32LittleEndian(span[8..]) != 0x10000)
        {
            throw new InvalidDataException("The GPT signature or revision is unsupported.");
        }
        var size = BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);
        if (size < 92 || size > blockSize || span[20..24].ContainsAnyExcept((byte)0)
            || span[92..].ContainsAnyExcept((byte)0))
        {
            throw new InvalidDataException("The GPT header size or reserved fields are invalid.");
        }
        var crc = BinaryPrimitives.ReadUInt32LittleEndian(span[16..]);
        span[16..20].Clear();
        if (Crc32Ieee.Compute(span[..(int)size]) != crc)
        {
            throw new InvalidDataException("The GPT header CRC is invalid.");
        }
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], crc);
        var current = BinaryPrimitives.ReadUInt64LittleEndian(span[24..]);
        var alternate = BinaryPrimitives.ReadUInt64LittleEndian(span[32..]);
        var first = BinaryPrimitives.ReadUInt64LittleEndian(span[40..]);
        var last = BinaryPrimitives.ReadUInt64LittleEndian(span[48..]);
        var diskId = new Guid(span[56..72]);
        var arrayLba = BinaryPrimitives.ReadUInt64LittleEndian(span[72..]);
        var headerCount = BinaryPrimitives.ReadUInt32LittleEndian(span[80..]);
        var headerEntrySize = BinaryPrimitives.ReadUInt32LittleEndian(span[84..]);
        var arrayCrc = BinaryPrimitives.ReadUInt32LittleEndian(span[88..]);
        ValidateEntryDimensionOverride(partitionEntryCount, partitionEntrySize);
        if (current != lba || alternate != (lba == 1 ? blockCount - 1 : 1) || diskId == Guid.Empty
            || first > last || last >= blockCount - 1
            || (partitionEntryCount is null && headerCount is 0 or > MaximumEntries)
            || (partitionEntrySize is null
                && (headerEntrySize is < 128 or > MaximumEntrySize
                    || (headerEntrySize & (headerEntrySize - 1)) != 0)))
        {
            throw new InvalidDataException("The GPT header identity, geometry or entry dimensions are invalid.");
        }
        var count = partitionEntryCount ?? headerCount;
        var entrySize = partitionEntrySize ?? headerEntrySize;
        var arrayBytes = (ulong)count * entrySize;
        if (arrayBytes > MaximumArrayBytes)
        {
            if (partitionEntryCount is not null || partitionEntrySize is not null)
            {
                throw new ArgumentOutOfRangeException(
                    partitionEntryCount is not null ? nameof(partitionEntryCount) : nameof(partitionEntrySize),
                    "The requested partition-entry dimensions do not fit the supported entry array limit.");
            }
            throw new InvalidDataException("The GPT entry array exceeds the supported metadata limit.");
        }
        var arrayBlocks = (arrayBytes + (uint)blockSize - 1) / (uint)blockSize;
        var reservedBlocks = (Math.Max(arrayBytes, 16384UL) + (uint)blockSize - 1) / (uint)blockSize;
        if (first < 2 + reservedBlocks || reservedBlocks >= blockCount - 1
            || last >= blockCount - 1 - reservedBlocks)
        {
            throw new InvalidDataException("The GPT entry array overlaps usable storage or a header.");
        }
        if (partitionEntryLba is null)
        {
            if (arrayLba >= blockCount || arrayBlocks > blockCount - arrayLba
                || (lba == 1 ? arrayLba < 2 || arrayLba + arrayBlocks > first
                    : arrayLba <= last || arrayLba + arrayBlocks > lba))
            {
                throw new InvalidDataException("The GPT entry array overlaps usable storage or a header.");
            }
            return new Header(first, last, diskId, arrayLba, count, entrySize, arrayCrc, size, Sha256.ComputeHash(span));
        }
        var requested = partitionEntryLba.Value;
        if (requested < 2 || requested > blockCount - 1 || arrayBlocks > blockCount - requested)
        {
            throw new ArgumentOutOfRangeException(nameof(partitionEntryLba), requested,
                "The requested partition-entry array LBA does not leave the entry array inside the reported capacity.");
        }
        return new Header(first, last, diskId, requested, count, entrySize, arrayCrc, size, Sha256.ComputeHash(span));
    }

    private static void ValidateEntryDimensionOverride(uint? partitionEntryCount, uint? partitionEntrySize)
    {
        if (partitionEntryCount is > MaximumEntries or 0)
        {
            throw new ArgumentOutOfRangeException(nameof(partitionEntryCount), partitionEntryCount,
                "The requested partition-entry count must be between 1 and 65536.");
        }
        if (partitionEntrySize is { } requestedEntrySize
            && (requestedEntrySize is < 128 or > MaximumEntrySize
                || (requestedEntrySize & (requestedEntrySize - 1)) != 0))
        {
            throw new ArgumentOutOfRangeException(nameof(partitionEntrySize), requestedEntrySize,
                "The requested partition-entry size must be a power of two from 128 through 4096.");
        }
    }

    private static void ReadAt(Stream source, long offset, Span<byte> destination)
    {
        source.Position = offset;
        source.ReadExactly(destination);
    }

    private sealed record Header(ulong First, ulong Last, Guid DiskId, ulong ArrayLba, uint Count, uint Size, uint ArrayCrc, uint HeaderSize, byte[] Sha256);
}