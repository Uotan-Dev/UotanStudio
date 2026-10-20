using System.Buffers.Binary;
using UotanStudio.Firmware.FileSystem;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Tests.TestInfrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Guards the EROFS ZLIB4C read path: a semi-compressed pcluster stores its first logical cluster raw
/// and the remaining logical clusters as one raw-DEFLATE stream, indexed by a PLAIN head lcluster
/// followed by NONHEAD lclusters carrying the CBLKCNT block count. The synthetic corpus rewrites a
/// valid builder image into that layout so the decoder contract is exercised without a writer-side
/// implementation.
/// <para>守护 EROFS ZLIB4C 读取路径：半压缩 pcluster 的首个逻辑簇以明文存放，其余逻辑簇合成一条原始
/// DEFLATE 流，索引由 PLAIN head lcluster 加携带 CBLKCNT 块数的 NONHEAD lcluster 组成。合成语料把
/// 合法的打包器镜像改写为该布局，从而在没有写侧实现的情况下验证解码契约。</para>
/// </summary>
public sealed class ErofsZlib4cTests
{
    private const int BlockSize = 4096;
    private const int FileSize = 128 * 1024;
    private const int LclusterSize = 4096;
    private const byte LclusterTypePlain = 0;
    private const byte LclusterTypeNonHead = 2;

    [Fact]
    public void ReadFile_SemiClusteredDeflatePclusters_DecodesRawHeadAndDeflateTail()
    {
        var payload = Payload();
        var image = RewriteAsSemiClusters(BuildDeflateBaseImage(payload), bigPcluster2: true);
        using var parser = new ErofsParser(image);
        Assert.Equal(payload, parser.ReadFileBytes("data.bin"));
    }

    [Fact]
    public void EnumerateFiles_SemiClusteredDeflatePclusters_KeepsInodeAndSizeIdentity()
    {
        var payload = Payload();
        var image = RewriteAsSemiClusters(BuildDeflateBaseImage(payload), bigPcluster2: true);
        using var parser = new ErofsParser(image);
        var entry = Assert.Single(parser.EnumerateFiles());
        Assert.Equal("data.bin", entry.Path);
        Assert.Equal(payload.LongLength, entry.Size);
    }

    [Fact]
    public void DumpIndexClusters_SemiClusteredDeflatePclusters_AlternatesPlainHeadAndNonHead()
    {
        var payload = Payload();
        var image = RewriteAsSemiClusters(BuildDeflateBaseImage(payload), bigPcluster2: true);
        using var parser = new ErofsParser(image);
        var rows = parser.DumpIndexClusters("data.bin");
        Assert.Equal(FileSize / LclusterSize, rows.Count);
        for (var lcn = 0; lcn < rows.Count; lcn++)
        {
            Assert.Equal(lcn % 2 == 0 ? LclusterTypePlain : LclusterTypeNonHead, rows[lcn].Type);
        }
        Assert.All(rows.Where(static row => row.Type == LclusterTypeNonHead), row => Assert.Equal(1u, row.Delta0));
    }

    [Fact]
    public void ReadFile_SemiClusteredDeflatePclusterMissingBigPclusterAdvise_ReportsMissingEncodedTail()
    {
        var payload = Payload();
        var image = RewriteAsSemiClusters(BuildDeflateBaseImage(payload), bigPcluster2: false);
        using var parser = new ErofsParser(image);
        var ex = Assert.Throws<FileSystemCorruptedException>(() => parser.ReadFileBytes("data.bin"));
        Assert.Contains("no encoded tail", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadFile_SemiClusteredDeflatePclusterWithCorruptedTail_ThrowsInvalidData()
    {
        var payload = Payload();
        var image = RewriteAsSemiClusters(BuildDeflateBaseImage(payload), bigPcluster2: true, corruptStream: true);
        using var parser = new ErofsParser(image);
        Assert.Throws<FileSystemCorruptedException>(() => parser.ReadFileBytes("data.bin"));
    }

    [Fact]
    public void ReadFile_PlainHeadWithLzmaStream_KeepsKernelShiftedRawSemantics()
    {
        var payload = Payload();
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", payload);
        var baseImage = new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            BlockSize = BlockSize,
            Compress = true,
            Algorithm = ErofsCompressionAlgorithm.Lzma,
            UseCompactedIndexes = false,
            TailPacking = false,
            CompressedTailPacking = false,
            Fragments = false,
            Dedup = false,
            SuperblockChecksum = false,
        });
        var image = RewriteAsRawPlainPairs(baseImage);
        using var parser = new ErofsParser(image);
        Assert.Equal(payload, parser.ReadFileBytes("data.bin"));
    }

    private static byte[] Payload()
    {
        var bytes = new byte[FileSize];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)('a' + i % 251);
        }
        return bytes;
    }

    private static byte[] BuildDeflateBaseImage(byte[] payload)
    {
        using var source = new TempDirectory();
        source.WriteBytes("data.bin", payload);
        return new ErofsBuilder().Build(source.Path, new ErofsOptions
        {
            BlockSize = BlockSize,
            Compress = true,
            Algorithm = ErofsCompressionAlgorithm.Deflate,
            UseCompactedIndexes = false,
            TailPacking = false,
            CompressedTailPacking = false,
            Fragments = false,
            Dedup = false,
            SuperblockChecksum = false,
        });
    }

    private static byte[] RewriteAsRawPlainPairs(byte[] baseImage)
    {
        ulong nid;
        long size;
        byte[] payload;
        using (var probe = new ErofsParser(baseImage))
        {
            var entry = Assert.Single(probe.EnumerateFiles());
            nid = entry.Nid;
            size = entry.Size;
            payload = probe.ReadFileBytes(entry.Path);
        }

        Assert.True(size % (2L * LclusterSize) == 0);
        var blockBits = baseImage[1024 + 12];
        var blockSize = 1 << (int)blockBits;
        var metaBlkaddr = BinaryPrimitives.ReadUInt32LittleEndian(baseImage.AsSpan(1024 + 40, 4));
        var inodeLoc = (long)metaBlkaddr * blockSize + (long)nid * ErofsSuperblock.CompactInodeSize;
        var inode = ErofsInode.FromBytes(baseImage.AsSpan((int)inodeLoc, ErofsInode.ExtendedSize), nid, (int)blockBits);
        Assert.True(inode.IsCompressed);
        Assert.Equal(ErofsDataLayout.Compressed, inode.DataLayout);
        var headerOffset = (inodeLoc + inode.InlineDataOffset + 7) & ~7;
        Assert.Equal((byte)ErofsAlgorithm.Lzma, baseImage[(int)headerOffset + 6]);
        var clusterBits = baseImage[(int)headerOffset + 7] & ZErofsMapHeader.LclusterBitsMask;
        var lclusterSize = 1 << (int)(blockBits + clusterBits);
        Assert.Equal(LclusterSize, lclusterSize);
        var totalIdx = (int)(size / lclusterSize);
        var indexBase = headerOffset + ZErofsMapHeader.Size + 8;

        var dataBase = (long)(baseImage.Length + blockSize - 1) / blockSize * blockSize;
        var image = baseImage.ToArray();
        Array.Resize(ref image, (int)dataBase + totalIdx * blockSize);
        for (var pair = 0; pair < totalIdx / 2; pair++)
        {
            var fileOffset = (long)pair * 2 * lclusterSize;
            var diskOffset = dataBase + (long)pair * 2 * blockSize;
            payload.AsSpan((int)fileOffset, (int)(2 * lclusterSize)).CopyTo(image.AsSpan((int)diskOffset, (int)(2 * lclusterSize)));
            var headEntry = indexBase + (long)pair * 2 * ZErofsLclusterIndex.Size;
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan((int)headEntry), LclusterTypePlain);
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan((int)headEntry + 2), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan((int)headEntry + 4), (uint)(diskOffset / blockSize));
            var tailEntry = headEntry + ZErofsLclusterIndex.Size;
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan((int)tailEntry), LclusterTypeNonHead);
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan((int)tailEntry + 2), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan((int)tailEntry + 4),
                (ushort)(ZErofsLclusterIndex.LiD0CblkCnt | 2));
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan((int)tailEntry + 6), 1);
        }

        var advise = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan((int)headerOffset + 4, 2));
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan((int)headerOffset + 4, 2),
            (ushort)(advise | ZErofsMapHeader.AdviseBigPcluster2));
        var incompat = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(1024 + 80, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(1024 + 80, 4), incompat | ErofsSuperblock.IncompatBigPcluster);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(1024 + 36, 4), (uint)(image.Length / blockSize));
        return image;
    }

    private static byte[] RewriteAsSemiClusters(byte[] baseImage, bool bigPcluster2, bool corruptStream = false)
    {
        const ErofsAlgorithm algorithm = ErofsAlgorithm.Deflate;
        ulong nid;
        long size;
        byte[] payload;
        using (var probe = new ErofsParser(baseImage))
        {
            var entry = Assert.Single(probe.EnumerateFiles());
            nid = entry.Nid;
            size = entry.Size;
            payload = probe.ReadFileBytes(entry.Path);
        }

        Assert.True(size % (2L * LclusterSize) == 0);
        var blockBits = baseImage[1024 + 12];
        var blockSize = 1 << (int)blockBits;
        Assert.Equal(BlockSize, blockSize);
        var metaBlkaddr = BinaryPrimitives.ReadUInt32LittleEndian(baseImage.AsSpan(1024 + 40, 4));
        var inodeLoc = (long)metaBlkaddr * blockSize + (long)nid * ErofsSuperblock.CompactInodeSize;
        var inode = ErofsInode.FromBytes(baseImage.AsSpan((int)inodeLoc, ErofsInode.ExtendedSize), nid, (int)blockBits);
        Assert.True(inode.IsCompressed);
        var headerOffset = (inodeLoc + inode.InlineDataOffset + 7) & ~7;
        Assert.Equal((byte)algorithm, baseImage[(int)headerOffset + 6]);
        var clusterBits = baseImage[(int)headerOffset + 7] & ZErofsMapHeader.LclusterBitsMask;
        var lclusterSize = 1 << (int)(blockBits + clusterBits);
        Assert.Equal(LclusterSize, lclusterSize);
        var totalIdx = (int)(size / lclusterSize);
        var indexBase = headerOffset + ZErofsMapHeader.Size + 8;

        var dataBase = (long)(baseImage.Length + blockSize - 1) / blockSize * blockSize;
        var image = baseImage.ToArray();
        var cursor = dataBase;
        for (var pair = 0; pair < totalIdx / 2; pair++)
        {
            var headFileOffset = (long)pair * 2 * lclusterSize;
            var tailFileOffset = headFileOffset + lclusterSize;
            var stream = new byte[ErofsCompression.MaximumOutputSize(algorithm, lclusterSize)];
            var streamLength = ErofsCompression.AlgorithmCompress(algorithm, payload, (int)tailFileOffset, lclusterSize,
                stream, 0, stream.Length, 1);
            Assert.InRange(streamLength, 1, lclusterSize - 1);
            var blocks = 1 + (streamLength + blockSize - 1) / blockSize;
            var required = cursor + (long)blocks * blockSize;
            if (image.Length < required)
            {
                Array.Resize(ref image, (int)required);
            }
            payload.AsSpan((int)headFileOffset, lclusterSize).CopyTo(image.AsSpan((int)cursor, lclusterSize));
            var streamStart = (int)(cursor + lclusterSize);
            var streamRoom = (int)((long)blocks * blockSize - lclusterSize);
            if (corruptStream)
            {
                image.AsSpan(streamStart, streamRoom).Fill(0xFF);
            }
            else
            {
                image.AsSpan(streamStart, streamRoom).Clear();
                stream.AsSpan(0, streamLength).CopyTo(image.AsSpan(streamStart + streamRoom - streamLength, streamLength));
            }

            var headEntry = indexBase + (long)pair * 2 * ZErofsLclusterIndex.Size;
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan((int)headEntry), LclusterTypePlain);
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan((int)headEntry + 2), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan((int)headEntry + 4), (uint)(cursor / blockSize));
            var tailEntry = headEntry + ZErofsLclusterIndex.Size;
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan((int)tailEntry), LclusterTypeNonHead);
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan((int)tailEntry + 2), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan((int)tailEntry + 4),
                (ushort)(ZErofsLclusterIndex.LiD0CblkCnt | (uint)blocks));
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan((int)tailEntry + 6), 1);
            cursor = required;
        }

        if (image.Length < cursor)
        {
            Array.Resize(ref image, (int)cursor);
        }

        var advise = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan((int)headerOffset + 4, 2));
        advise = bigPcluster2
            ? (ushort)(advise | ZErofsMapHeader.AdviseBigPcluster2)
            : (ushort)(advise & ~ZErofsMapHeader.AdviseBigPcluster2);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan((int)headerOffset + 4, 2), advise);

        var incompat = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(1024 + 80, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(1024 + 80, 4), incompat | ErofsSuperblock.IncompatBigPcluster);
        var algs = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(1024 + 84, 2));
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(1024 + 84, 2), (ushort)(algs | (1 << (int)algorithm)));
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(1024 + 36, 4), (uint)(image.Length / blockSize));
        return image;
    }
}