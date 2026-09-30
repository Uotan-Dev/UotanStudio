using System.Buffers.Binary;

namespace UotanStudio.Firmware.FileSystem.Squashfs;

/// <summary>
/// Version 4 SquashFS on-disk superblock: the 96-byte fixed layout plus magic/endianness validation.
/// <para>SquashFS 版本 4 的落盘超级块：96 字节固定布局，外加魔数与字节序校验。</para>
/// </summary>
internal sealed class SquashfsSuperblock
{
    internal const int Size = 96;
    internal const uint MagicLittleEndian = 0x73717368;
    internal const uint MagicBigEndian = 0x68737173;
    internal const int MetadataWindowSize = 8192;
    internal const int MaximumBlockLog = 20;

    /// <summary>Gets a value indicating whether every multi-byte field is stored big-endian.
    /// <para>获取一个值，指示是否所有多字节字段均以大端序存储。</para></summary>
    /// <value><see langword="true"/> for a <c>sqsh</c> image, <see langword="false"/> for <c>hsqs</c>.
    /// <para><c>sqsh</c> 镜像为 <see langword="true"/>，<c>hsqs</c> 为 <see langword="false"/>。</para></value>
    internal bool BigEndian { get; private init; }

    internal uint InodeCount { get; private init; }
    internal uint MkfsTime { get; private init; }
    internal int BlockSize { get; private init; }
    internal uint FragmentCount { get; private init; }
    internal ushort CompressionId { get; private init; }
    internal ushort BlockLog { get; private init; }
    internal ushort Flags { get; private init; }
    internal ushort IdCount { get; private init; }
    internal ushort MajorVersion { get; private init; }
    internal ushort MinorVersion { get; private init; }
    internal ulong RootInodeAddress { get; private init; }
    internal ulong BytesUsed { get; private init; }
    internal ulong IdTableStart { get; private init; }
    internal ulong XattrIdTableStart { get; private init; }
    internal ulong InodeTableStart { get; private init; }
    internal ulong DirectoryTableStart { get; private init; }
    internal ulong FragmentTableStart { get; private init; }
    internal ulong LookupTableStart { get; private init; }

    /// <summary>Decodes the compressor identifier stored at superblock offset 20.
    /// <para>解码超级块偏移 20 处保存的压缩器标识。</para></summary>
    /// <value>The raw identifier (1 gzip, 2 lzma, 3 lzo, 4 xz, 5 lz4, 6 zstd).
    /// <para>原始标识（1 gzip、2 lzma、3 lzo、4 xz、5 lz4、6 zstd）。</para></value>
    internal int Compression => CompressionId;

    /// <summary>
    /// Parses and validates a 96-byte version 4 superblock against the containing image length.
    /// <para>解析并校验 96 字节的版本 4 超级块，并对照镜像总长做边界检查。</para>
    /// </summary>
    /// <param name="data">Exactly 96 superblock bytes. <para>恰好 96 个超级块字节。</para></param>
    /// <param name="imageLength">Total bytes of the source image used for range validation.
    /// <para>用于范围校验的源镜像总字节数。</para></param>
    /// <returns>The validated superblock model. <para>校验通过的超级块模型。</para></returns>
    /// <exception cref="FileSystemCorruptedException">The magic, geometry, counts or table starts are invalid.
    /// <para>魔数、几何参数、计数或表起始位置无效时抛出。</para></exception>
    /// <exception cref="FileSystemUnsupportedFeatureException">The image declares a version this parser does not read.
    /// <para>镜像声明了本解析器不读取的版本时抛出。</para></exception>
    internal static SquashfsSuperblock Parse(ReadOnlySpan<byte> data, long imageLength)
    {
        if (data.Length < Size)
        {
            throw new FileSystemCorruptedException("The SquashFS superblock is truncated.");
        }

        var magicLe = BinaryPrimitives.ReadUInt32LittleEndian(data);
        var be = magicLe != MagicLittleEndian;
        if (be && BinaryPrimitives.ReadUInt32BigEndian(data) != MagicLittleEndian)
        {
            throw new FileSystemCorruptedException("Neither the 'hsqs' nor the 'sqsh' SquashFS magic was found.");
        }

        var block = data[..Size].ToArray();
        uint ReadU32(int offset) => be
            ? BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(offset))
            : BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(offset));
        ushort ReadU16(int offset) => be
            ? BinaryPrimitives.ReadUInt16BigEndian(block.AsSpan(offset))
            : BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(offset));
        ulong ReadU64(int offset) => be
            ? BinaryPrimitives.ReadUInt64BigEndian(block.AsSpan(offset))
            : BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(offset));

        var blockLog = ReadU16(22);
        var blockSize = ReadU32(12);
        if (blockLog > MaximumBlockLog || blockSize != 1u << (int)blockLog || blockSize < 4096)
        {
            throw new FileSystemCorruptedException($"The SquashFS block geometry (size {blockSize}, log {blockLog}) is invalid.");
        }

        var major = ReadU16(28);
        var minor = ReadU16(30);
        if (major != 4 || minor > 1)
        {
            throw new FileSystemUnsupportedFeatureException($"Unsupported SquashFS version {major}.{minor}; only 4.0 and 4.1 are parsed.");
        }

        var inodeCount = ReadU32(4);
        var fragmentCount = ReadU32(16);
        if (ReadU16(26) == 0)
        {
            throw new FileSystemCorruptedException("The SquashFS id table is empty.");
        }

        if (inodeCount is 0 or > 1 << 20)
        {
            throw new FileSystemCorruptedException($"The SquashFS inode count {inodeCount} exceeds the supported limit.");
        }

        if (fragmentCount > 1 << 20)
        {
            throw new FileSystemCorruptedException($"The SquashFS fragment count {fragmentCount} exceeds the supported limit.");
        }

        var bytesUsed = ReadU64(40);
        if (bytesUsed < Size || bytesUsed > (ulong)imageLength)
        {
            throw new FileSystemCorruptedException($"The SquashFS bytes_used {bytesUsed} does not fit the image of {imageLength} bytes.");
        }

        var result = new SquashfsSuperblock
        {
            BigEndian = be,
            InodeCount = inodeCount,
            MkfsTime = ReadU32(8),
            BlockSize = (int)blockSize,
            FragmentCount = fragmentCount,
            CompressionId = ReadU16(20),
            BlockLog = blockLog,
            Flags = ReadU16(24),
            IdCount = ReadU16(26),
            MajorVersion = major,
            MinorVersion = minor,
            RootInodeAddress = ReadU64(32),
            BytesUsed = bytesUsed,
            IdTableStart = ReadU64(48),
            XattrIdTableStart = ReadU64(56),
            InodeTableStart = ReadU64(64),
            DirectoryTableStart = ReadU64(72),
            FragmentTableStart = ReadU64(80),
            LookupTableStart = ReadU64(88),
        };

        result.ValidateTableStart(result.InodeTableStart, "inode table");
        result.ValidateTableStart(result.DirectoryTableStart, "directory table");
        result.ValidateTableStart(result.IdTableStart, "id table");
        if (result.FragmentCount > 0)
        {
            result.ValidateTableStart(result.FragmentTableStart, "fragment table");
        }

        return result;
    }

    private void ValidateTableStart(ulong start, string name)
    {
        if (start == ulong.MaxValue)
        {
            throw new FileSystemCorruptedException($"The SquashFS {name} start is the invalid-block sentinel.");
        }

        if (start + 2 > BytesUsed)
        {
            throw new FileSystemCorruptedException($"The SquashFS {name} start {start} lies beyond the used image bytes.");
        }
    }
}