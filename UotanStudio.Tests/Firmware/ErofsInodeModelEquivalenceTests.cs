using System.Buffers.Binary;
using UotanStudio.Firmware.FileSystem;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes;
using UotanStudio.Firmware.FileSystem.Erofs.Parser;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class ErofsInodeModelEquivalenceTests
{
    private const int BlockSize = 4096;
    private const int BlockSizeBits = 12;
    private const string CorpusVariable = "UOTAN_EROFS_CORPUS";

    [Theory]
    [InlineData("flat")]
    [InlineData("compressed")]
    [InlineData("full-index")]
    [InlineData("tailpacked")]
    [InlineData("fragmented")]
    [InlineData("wide-ownership")]
    public void Decode_BuiltImageInode_MatchesIndependentDecodeForEveryField(string variant)
    {
        using var temp = new TempDirectory();
        WriteTree(temp);
        var image = new ErofsBuilder().Build(temp.Path, CreateOptions(variant));
        using var parser = new ErofsParser(image);
        var superblock = parser.Superblock;

        var entries = parser.EnumerateEntries();
        Assert.NotEmpty(entries);
        var layouts = new HashSet<ErofsDataLayout>();
        foreach (var entry in entries)
        {
            var offset = superblock.GetInodeOffset(entry.Nid);
            var raw = ReadInodeRecord(image, offset);
            var flat = InodeRecordOracle.Decode(raw);
            var unified = ErofsInode.FromBytes(raw, entry.Nid, superblock.BlockSizeBits);
            layouts.Add(unified.DataLayout);

            AssertOnDiskFieldsEqual(entry.Nid, flat, unified);
            AssertRecordSerializationMatches(flat, unified, raw, $"variant '{variant}' nid {entry.Nid}");

            var projected = ProjectOnDiskFields(unified);
            AssertOracleFieldsEqual(flat, projected, variant, entry.Nid);

            var reserialized = new byte[ErofsInode.ExtendedSize];
            unified.WriteTo(reserialized);
            var reloaded = ErofsInode.FromBytes(
                reserialized.AsSpan(0, unified.OnDiskSize), entry.Nid, superblock.BlockSizeBits);
            AssertOnDiskFieldsEqual(entry.Nid, flat, reloaded);
            AssertOracleFieldsEqual(
                flat, InodeRecordOracle.Decode(reserialized.AsSpan(0, unified.OnDiskSize)), variant, entry.Nid);
        }

        Assert.Contains(layouts, layout => layout == ErofsDataLayout.Flat || layout == ErofsDataLayout.Inline);
    }

    [Theory]
    [InlineData("compressed")]
    [InlineData("full-index")]
    [InlineData("tailpacked")]
    public void Decode_CompressedBuiltImage_MatchesMapHeaderAndRebuildRecord(string variant)
    {
        using var temp = new TempDirectory();
        WriteTree(temp);
        var image = new ErofsBuilder().Build(temp.Path, CreateOptions(variant));
        using var parser = new ErofsParser(image);
        var superblock = parser.Superblock;
        var records = parser.ReadRebuildOptions().SourceInodes!;

        var verified = 0;
        foreach (var entry in parser.EnumerateEntries())
        {
            var record = records[entry.Nid];
            if (!record.SourceCompressed)
            {
                var plainRaw = ReadInodeRecord(image, superblock.GetInodeOffset(entry.Nid));
                var plain = InodeRecordOracle.Decode(plainRaw);
                var plainUnified = ErofsInode.FromBytes(
                    plainRaw, entry.Nid, superblock.BlockSizeBits);
                Assert.Equal(plain.IsCompressed, plainUnified.IsCompressed);
                continue;
            }

            var offset = superblock.GetInodeOffset(entry.Nid);
            var raw = ReadInodeRecord(image, offset);
            var flat = InodeRecordOracle.Decode(raw);
            var unified = ErofsInode.FromBytes(raw, entry.Nid, superblock.BlockSizeBits);
            var headerOffset = AlignUp(offset + flat.InlineDataOffset, 8);
            var headerBytes = image.AsSpan((int)headerOffset, ZErofsMapHeader.Size).ToArray();
            var header = ZErofsMapHeader.FromBytes(headerBytes);
            unified.AttachMapHeader(headerBytes, superblock.BlockSizeBits);

            Assert.Equal(header.Advise, unified.ZAdvise);
            Assert.Equal(record.SourceCompressed, unified.IsCompressed);
            Assert.Equal(record.IsExtended, unified.IsExtended);
            Assert.Equal(record.IsExtended, flat.IsExtended);
            Assert.Equal(record.SourceAlgorithm, (ErofsCompressionAlgorithm)unified.ZAlgorithmTypeHead1);

            var compressed = Assert.IsType<ErofsCompressedIdata>(unified.Idata);
            Assert.Equal(record.DataLayout == ErofsDataLayout.CompactIndexes, compressed.CompactedIndexes);
            Assert.Equal(header.HasBigPcluster1 || header.HasBigPcluster2, compressed.BigPcluster);
            Assert.Equal(header.HasInlinePcluster, compressed.InlineCompressedTail);
            Assert.Equal(
                unified.DataLayout == ErofsDataLayout.CompactIndexes && header.HasCompacted2B,
                compressed.Compiled2B);

            if (header.IsFragmentInode)
            {
                Assert.Equal(ZErofsMapHeader.AdviseFragmentPcluster, unified.ZAdvise);
                Assert.Equal(
                    (long)(BinaryPrimitives.ReadUInt64LittleEndian(headerBytes) & ~(1UL << 63)),
                    unified.FragmentOff);
                Assert.NotEqual(ErofsFragmentShape.None, record.SourceFragmentShape);
            }
            else
            {
                Assert.Equal(record.LogicalClusterSize, 1 << unified.ZLclusterBits);
                Assert.Equal(header.IsFragmentInode, record.SourceFragmentShape != ErofsFragmentShape.None);
                if (header.HasFragmentPcluster)
                {
                    Assert.Equal(header.FragmentOff, unified.FragmentOff);
                }
            }

            verified++;
        }

        Assert.True(verified > 0, $"The '{variant}' image carried no compressed inode to verify.");
    }

    [Fact]
    public void Decode_SyntheticRecords_CarryEveryIdataFieldOfTheirLayout()
    {
        var flatRecord = BuildCompactRecord(ErofsInode.DataLayoutFlat, address: 0x1234, size: 9000, mode: ErofsInode.ModeRegularFile);
        var flat = ErofsInode.FromBytes(flatRecord, 7, BlockSizeBits);
        var flatIdata = Assert.IsType<ErofsFlatIdata>(flat.Idata);
        Assert.Equal(0x1234u, flatIdata.BlockAddress);
        Assert.Equal(3u, flatIdata.Blocks);
        Assert.Equal(32, flat.InodeISize);
        Assert.Equal(0, flat.XattrISize);

        var inlineRecord = BuildCompactRecord(ErofsInode.DataLayoutInline, address: 0x2000, size: 9000, mode: ErofsInode.ModeRegularFile);
        var inline = ErofsInode.FromBytes(inlineRecord, 8, BlockSizeBits);
        var inlineIdata = Assert.IsType<ErofsFlatIdata>(inline.Idata);
        Assert.Equal(0x2000u, inlineIdata.BlockAddress);
        Assert.Equal(2u, inlineIdata.Blocks);

        var extendedRecord = BuildExtendedRecord(ErofsInode.DataLayoutCompressed, address: 0x33, size: 1UL << 40, uid: 70_000, gid: 70_001);
        var extended = ErofsInode.FromBytes(extendedRecord, 9, BlockSizeBits);
        var extendedIdata = Assert.IsType<ErofsCompressedIdata>(extended.Idata);
        Assert.Equal(0x33u, extendedIdata.Blocks);
        Assert.Equal(64, extended.InodeISize);
        Assert.Equal(1UL << 40, extended.Size);
        Assert.Equal(70_000u, extended.Uid);
        Assert.Equal(70_001u, extended.Gid);

        var extentRecord = BuildCompactRecord(ErofsInode.DataLayoutCompressed, address: 0x44, size: 4096, mode: ErofsInode.ModeRegularFile);
        var extents = ErofsInode.FromBytes(extentRecord, 10, BlockSizeBits);
        extents.AttachMapHeader(BuildMapHeader(extentCount: 3, recordSize: 16), BlockSizeBits);
        var extentsIdata = Assert.IsType<ErofsCompressedIdata>(extents.Idata);
        Assert.Equal(0x44u, extentsIdata.Blocks);
        Assert.Equal(ZErofsMapHeader.AdviseExtents | (2 << ZErofsMapHeader.ExtRecSizeBit), extents.ZAdvise);
        Assert.Equal(3UL, extents.ZExtents);
        Assert.Equal(16, extentsIdata.ExtentRecordSize);
        Assert.Equal(3UL, extentsIdata.ExtentCount);
        Assert.False(extentsIdata.CompactedIndexes);
        Assert.False(extentsIdata.Compiled2B);

        var fullExtentRecord = BuildCompactRecord(ErofsInode.DataLayoutCompressed, address: 0x45, size: 4096, mode: ErofsInode.ModeRegularFile);
        var fullExtents = ErofsInode.FromBytes(fullExtentRecord, 11, BlockSizeBits);
        fullExtents.AttachMapHeader(BuildMapHeader(extentCount: 5, recordSize: 32), BlockSizeBits);
        var fullExtentIdata = Assert.IsType<ErofsCompressedIdata>(fullExtents.Idata);
        Assert.Equal(32, fullExtentIdata.ExtentRecordSize);
        Assert.Equal(5UL, fullExtentIdata.ExtentCount);
        Assert.False(fullExtentIdata.Compiled2B);

        var compact2BRecord = BuildCompactRecord(ErofsInode.DataLayoutCompactIndexes, address: 0x46, size: 4096, mode: ErofsInode.ModeRegularFile);
        var compact2B = ErofsInode.FromBytes(compact2BRecord, 12, BlockSizeBits);
        // A compact-index inode must agree on both big-pcluster head bits, which is what gives the compact
        // slot width; a header raising only one of them is corrupt and is covered by its own case.
        compact2B.AttachMapHeader(
            BuildPlainMapHeader(ZErofsMapHeader.AdviseCompacted2B
                | ZErofsMapHeader.AdviseBigPcluster1
                | ZErofsMapHeader.AdviseBigPcluster2),
            BlockSizeBits);
        var compact2BIdata = Assert.IsType<ErofsCompressedIdata>(compact2B.Idata);
        Assert.True(compact2BIdata.CompactedIndexes);
        Assert.True(compact2BIdata.Compiled2B);
        Assert.True(compact2BIdata.BigPcluster);
        Assert.False(compact2BIdata.InlineCompressedTail);
        Assert.Equal((byte)BlockSizeBits, compact2B.ZLclusterBits);

        var fullBitZeroRecord = BuildCompactRecord(ErofsInode.DataLayoutCompressed, address: 0x47, size: 4096, mode: ErofsInode.ModeRegularFile);
        var fullBitZero = ErofsInode.FromBytes(fullBitZeroRecord, 13, BlockSizeBits);
        fullBitZero.AttachMapHeader(BuildPlainMapHeader(ZErofsMapHeader.AdviseExtents), BlockSizeBits);
        var fullBitZeroIdata = Assert.IsType<ErofsCompressedIdata>(fullBitZero.Idata);
        Assert.False(fullBitZeroIdata.Compiled2B);
        Assert.False(fullBitZeroIdata.CompactedIndexes);

        var fragmentOff = 0x1_2345_6789UL;
        var fragmentRecord = BuildCompactRecord(ErofsInode.DataLayoutCompressed, address: 0x48, size: 100, mode: ErofsInode.ModeRegularFile);
        var fragment = ErofsInode.FromBytes(fragmentRecord, 14, BlockSizeBits);
        fragment.AttachMapHeader(BuildFragmentMapHeader(fragmentOff), BlockSizeBits);
        Assert.Equal(ZErofsMapHeader.AdviseFragmentPcluster, fragment.ZAdvise);
        Assert.Equal((long)fragmentOff, fragment.FragmentOff);

        var chunkFormat = (ushort)(0x60 | 2);
        var wideChunkRecord = BuildCompactRecord(ErofsInode.DataLayoutChunkBased, address: chunkFormat, size: 5 * (BlockSize << 2), mode: ErofsInode.ModeRegularFile);
        var wideChunk = ErofsInode.FromBytes(wideChunkRecord, 15, BlockSizeBits);
        wideChunk.AttachChunkIndex(BuildChunkIndexTable([((ulong)1 << 32) | 0x100, 0x200], wide: true));
        var wideIdata = Assert.IsType<ErofsChunkIdata>(wideChunk.Idata);
        Assert.Equal(chunkFormat, wideIdata.ChunkFormat);
        Assert.Equal([((ulong)1 << 32) | 0x100, 0x200UL], wideIdata.ChunkIndex!);
        Assert.Equal(0u, wideIdata.DeviceId);
        Assert.Equal(2, wideChunk.ChunkFormatBlkBits);
        Assert.True(wideChunk.UsesChunkIndexes);
        Assert.True(wideChunk.Chunk48Bit);

        var narrowChunkRecord = BuildCompactRecord(ErofsInode.DataLayoutChunkBased, address: (ushort)(0x40 | 1), size: 3 * (BlockSize << 1), mode: ErofsInode.ModeRegularFile);
        var narrowChunk = ErofsInode.FromBytes(narrowChunkRecord, 16, BlockSizeBits);
        narrowChunk.AttachChunkIndex(BuildChunkIndexTable([0x10, 0x20, 0x30], wide: false));
        var narrowIdata = Assert.IsType<ErofsChunkIdata>(narrowChunk.Idata);
        Assert.Equal([0x10UL, 0x20UL, 0x30UL], narrowIdata.ChunkIndex!);
        Assert.False(narrowChunk.UsesChunkIndexes);
        Assert.Equal(1, narrowChunk.ChunkFormatBlkBits);
    }

    [Fact]
    public void Decode_CorpusImageInode_MatchesIndependentDecodeForEveryField()
    {
        var directory = Environment.GetEnvironmentVariable(CorpusVariable);
        Assert.SkipUnless(
            !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory),
            $"Set {CorpusVariable} to a directory of real EROFS images to run this comparison.");

        var images = Directory.GetFiles(directory!, "*.img");
        Assert.NotEmpty(images);

        var verifiedImages = 0;
        var verifiedInodes = 0;
        var skipped = new List<string>();
        foreach (var path in images)
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var stream = File.OpenRead(path);
            ErofsParser parser;
            try
            {
                parser = new ErofsParser(stream);
            }
            catch (Exception ex) when (ex is FormatException or FileSystemException or NotSupportedException)
            {
                skipped.Add($"{Path.GetFileName(path)}: {ex.GetType().Name} {ex.Message}");
                continue;
            }

            using (parser)
            {
                var superblock = parser.Superblock;
                IReadOnlyList<ErofsFileEntry> entries;
                try
                {
                    entries = parser.EnumerateEntries();
                }
                catch (Exception ex) when (ex is FormatException or FileSystemException or NotSupportedException or InvalidOperationException)
                {
                    skipped.Add($"{Path.GetFileName(path)}: {ex.GetType().Name} {ex.Message}");
                    continue;
                }

                var sawInode = false;
                foreach (var entry in entries)
                {
                    if ((entry.Nid >> 63) != 0)
                    {
                        continue;
                    }

                    var offset = superblock.GetInodeOffset(entry.Nid);
                    if (offset < 0 || offset + 2 > RandomAccess.GetLength(handle))
                    {
                        continue;
                    }

                    var raw = ReadInodeRecord(handle, offset);
                    InodeRecordOracle flat;
                    try
                    {
                        flat = InodeRecordOracle.Decode(raw);
                    }
                    catch (FormatException)
                    {
                        Assert.Throws<FormatException>(
                            () => ErofsInode.FromBytes(raw, entry.Nid, superblock.BlockSizeBits));
                        continue;
                    }

                    var unified = ErofsInode.FromBytes(raw, entry.Nid, superblock.BlockSizeBits);
                    AssertOnDiskFieldsEqual(entry.Nid, flat, unified);
                    AssertRecordSerializationMatches(flat, unified, raw, $"corpus '{path}' nid {entry.Nid}");
                    if (flat.IsCompressed)
                    {
                        var headerOffset = AlignUp(offset + flat.InlineDataOffset, 8);
                        if (headerOffset + ZErofsMapHeader.Size <= RandomAccess.GetLength(handle))
                        {
                            var headerBytes = new byte[ZErofsMapHeader.Size];
                            RandomAccess.Read(handle, headerBytes, headerOffset);
                            unified.AttachMapHeader(headerBytes, superblock.BlockSizeBits);
                            var header = ZErofsMapHeader.FromBytes(headerBytes);
                            Assert.Equal(header.Advise, unified.ZAdvise);
                            if (header.IsFragmentInode)
                            {
                                Assert.Equal(
                                    (long)(BinaryPrimitives.ReadUInt64LittleEndian(headerBytes) & ~(1UL << 63)),
                                    unified.FragmentOff);
                            }
                        }
                    }

                    sawInode = true;
                    verifiedInodes++;
                }

                if (sawInode)
                {
                    verifiedImages++;
                }
                else
                {
                    skipped.Add($"{Path.GetFileName(path)}: {entries.Count} entries, none comparable");
                }
            }
        }

        Assert.Equal(images.Length, verifiedImages + skipped.Count);
        Assert.True(
            verifiedImages > 0,
            $"No corpus image yielded an inode comparison. Skipped: {string.Join(" | ", skipped)}");
        Assert.True(verifiedInodes > 0, "No corpus inode was compared.");
    }

    private static ErofsOptions CreateOptions(string variant) => variant switch
    {
        "flat" => new ErofsOptions { Compress = false },
        "compressed" => new ErofsOptions { Compress = true, UseCompactedIndexes = true },
        "full-index" => new ErofsOptions { Compress = true, UseCompactedIndexes = false },
        "tailpacked" => new ErofsOptions { Compress = true, CompressedTailPacking = true },
        "fragmented" => new ErofsOptions { Compress = true, Fragments = true, AllFilesAsFragments = true },
        "wide-ownership" => new ErofsOptions { Compress = false, ForceUid = 70_000, ForceGid = 70_001 },
        _ => throw new ArgumentOutOfRangeException(nameof(variant), variant, "Unknown EROFS inode model equivalence variant."),
    };

    private static void WriteTree(TempDirectory temp)
    {
        temp.WriteBytes("empty.bin", []);
        temp.Write("small.txt", "small payload\n");
        var block = new byte[BlockSize];
        new Random(7).NextBytes(block);
        temp.WriteBytes("block.bin", block);
        var text = string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 400));
        temp.Write("big.txt", text);
        var random = new Random(11);
        var binary = new byte[20 * 1024];
        random.NextBytes(binary);
        temp.WriteBytes("dir/nested.bin", binary);
        temp.Write("dir/sub/inner.txt", "inner content\n");
        temp.WriteBytes("dir/aligned.bin", new byte[2 * BlockSize]);
    }

    private static void AssertOnDiskFieldsEqual(ulong nid, InodeRecordOracle flat, ErofsInode unified)
    {
        Assert.Equal(nid, unified.Nid);
        Assert.Equal(flat.Advise, unified.IFormat);
        Assert.Equal((ErofsDataLayout)(flat.DataLayoutRaw >> 1), unified.DataLayout);
        Assert.Equal(flat.Size, unified.Size);
        Assert.Equal(flat.Uid, unified.Uid);
        Assert.Equal(flat.Gid, unified.Gid);
        Assert.Equal(flat.Ino, unified.Ino);
        Assert.Equal(flat.Generation, unified.Generation);
        Assert.Equal(flat.CTime, unified.CTime);
        Assert.Equal(flat.CTimeNsec, unified.CTimeNsec);
        Assert.Equal(flat.Nlink, unified.Nlink);
        Assert.Equal(flat.Mode, unified.Mode);
        Assert.Equal(flat.XattrICount, unified.XattrICount);
        Assert.Equal(flat.OnDiskSize, unified.InodeISize);
        Assert.Equal(flat.XattrTotalSize, unified.XattrISize);
        Assert.Equal(flat.DotOmitted, unified.DotOmitted);
        Assert.Equal(flat.NlinkIsOne, unified.NlinkIsOne);
        Assert.False(unified.InMetabox);

        switch (unified.DataLayout)
        {
            case ErofsDataLayout.Flat:
            case ErofsDataLayout.Inline:
                var flatIdata = Assert.IsType<ErofsFlatIdata>(unified.Idata);
                Assert.Equal(flat.RawBlockAddress, flatIdata.BlockAddress);
                var expectedBlocks = unified.DataLayout == ErofsDataLayout.Inline
                    ? flat.Size / BlockSize
                    : (flat.Size + BlockSize - 1) / BlockSize;
                Assert.Equal((uint)Math.Min(expectedBlocks, uint.MaxValue), flatIdata.Blocks);
                break;
            case ErofsDataLayout.Compressed:
            case ErofsDataLayout.CompactIndexes:
                var compressedIdata = Assert.IsType<ErofsCompressedIdata>(unified.Idata);
                Assert.Equal(flat.CompressedBlocks, compressedIdata.Blocks);
                break;
            case ErofsDataLayout.ChunkBased:
                var chunkIdata = Assert.IsType<ErofsChunkIdata>(unified.Idata);
                Assert.Equal(flat.ChunkFormat, chunkIdata.ChunkFormat);
                break;
        }
    }

    private static InodeRecordOracle ProjectOnDiskFields(ErofsInode unified) => new()
    {
        Advise = unified.IFormat,
        XattrICount = unified.XattrICount,
        Mode = unified.Mode,
        Nlink = unified.Nlink,
        Size = unified.Size,
        U = unified.AddressField(),
        Ino = unified.Ino,
        Uid = unified.Uid,
        Gid = unified.Gid,
        Generation = unified.Generation,
        CTime = unified.CTime,
        CTimeNsec = unified.CTimeNsec,
        AllowNlinkOneBit = unified.AllowNlinkOneBit,
    };

    private static void AssertOracleFieldsEqual(InodeRecordOracle expected, InodeRecordOracle actual, string variant, ulong nid)
    {
        Assert.True(expected.Advise == actual.Advise, $"i_format mismatch in '{variant}' for nid {nid}.");
        Assert.Equal(expected.XattrICount, actual.XattrICount);
        Assert.Equal(expected.Mode, actual.Mode);
        Assert.Equal(expected.Nlink, actual.Nlink);
        Assert.Equal(expected.Size, actual.Size);
        Assert.Equal(expected.U, actual.U);
        Assert.Equal(expected.Ino, actual.Ino);
        Assert.Equal(expected.Uid, actual.Uid);
        Assert.Equal(expected.Gid, actual.Gid);
        Assert.Equal(expected.Generation, actual.Generation);
        Assert.Equal(expected.CTime, actual.CTime);
        Assert.Equal(expected.CTimeNsec, actual.CTimeNsec);
        Assert.Equal(expected.OnDiskSize, actual.OnDiskSize);
        Assert.Equal(expected.XattrTotalSize, actual.XattrTotalSize);
        Assert.Equal(expected.DotOmitted, actual.DotOmitted);
        Assert.Equal(expected.AllowNlinkOneBit, actual.AllowNlinkOneBit);
    }

    private static void AssertRecordSerializationMatches(InodeRecordOracle flat, ErofsInode unified, byte[] raw, string context)
    {
        var flatFormat = flat.Advise;
        var unifiedFormat = unified.IFormat;
        var flatNlink = flat.Nlink;
        var flatNlinkIsOne = flat.NlinkIsOne;
        var flatExtended = flat.IsExtended;
        var flatDirectory = flat.IsDirectory;
        var flatAllowOne = flat.AllowNlinkOneBit;
        var flatBuffer = new byte[ErofsInode.ExtendedSize];
        var unifiedBuffer = new byte[ErofsInode.ExtendedSize];
        flat.WriteTo(flatBuffer);
        unified.WriteTo(unifiedBuffer);
        Assert.True(
            flatBuffer.AsSpan().SequenceEqual(unifiedBuffer),
            $"{context}: oracle(i_format=0x{flatFormat:X4} -> 0x{flat.Advise:X4}, nlink={flatNlink}) " +
            $"unified(i_format=0x{unifiedFormat:X4} -> 0x{unified.IFormat:X4}, nlink={unified.Nlink}) " +
            $"raw=[{string.Join(',', raw.AsSpan(0, 8).ToArray().Select(value => value.ToString("X2")))}]");

        var normalizesNlinkOne = flatAllowOne && !flatExtended && !flatDirectory && !flatNlinkIsOne && flatNlink == 1;
        if (normalizesNlinkOne)
        {
            var expected = raw.AsSpan(0, unified.OnDiskSize).ToArray();
            expected[0] |= 0x10;
            BinaryPrimitives.WriteUInt16LittleEndian(expected.AsSpan(6), 0);
            Assert.Equal(expected, unifiedBuffer.AsSpan(0, unified.OnDiskSize).ToArray());
            return;
        }

        Assert.True(
            raw.AsSpan(0, unified.OnDiskSize).SequenceEqual(unifiedBuffer.AsSpan(0, unified.OnDiskSize)),
            $"{context}: round-trip mismatch raw=[{string.Join(',', raw.AsSpan(0, 8).ToArray())}] " +
            $"wrote=[{string.Join(',', unifiedBuffer.AsSpan(0, 8).ToArray())}] flatFormat=0x{flatFormat:X4} " +
            $"unifiedFormat=0x{unifiedFormat:X4} flat.Nlink={flatNlink} unified.Nlink={unified.Nlink} " +
            $"flat.nlinkIsOne={flatNlinkIsOne} flat.extended={flatExtended} flat.dir={flatDirectory} " +
            $"flat.allowOne={flatAllowOne} branch={normalizesNlinkOne}");
    }

    private static byte[] ReadInodeRecord(byte[] image, long offset)
    {
        var length = (int)Math.Min(ErofsInode.ExtendedSize, image.LongLength - offset);
        return image.AsSpan((int)offset, length).ToArray();
    }

    private static byte[] ReadInodeRecord(Microsoft.Win32.SafeHandles.SafeFileHandle handle, long offset)
    {
        var length = (int)Math.Min(ErofsInode.ExtendedSize, RandomAccess.GetLength(handle) - offset);
        var record = new byte[length];
        RandomAccess.Read(handle, record, offset);
        return record;
    }

    private static long AlignUp(long value, int alignment) => (value + alignment - 1) & ~(long)(alignment - 1);

    private static byte[] BuildCompactRecord(ushort dataLayout, uint address, ulong size, ushort mode, ulong nlink = 1)
    {
        var record = new byte[ErofsInode.CompactSize];
        var isDirectory = (mode & ErofsInode.ModeTypeMask) == ErofsInode.ModeDirectory;
        var nlinkIsOne = nlink == 1 && !isDirectory;
        var format = (ushort)(dataLayout | (nlinkIsOne ? ErofsInode.AdviseBitNlinkOneOrDotOmitted : 0));
        Assert.Equal(0, format & ~ErofsInode.AdviseAll);

        BinaryPrimitives.WriteUInt16LittleEndian(record, format);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), mode);
        // The nlink-one bit aliases the address high half: raising it means the field carries the
        // 48-bit address extension and the link count reads as one, so nothing but zero may live here.
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), nlinkIsOne ? (ushort)0 : (ushort)Math.Min(nlink, ushort.MaxValue));
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(8), (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(12), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(16), address);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(24), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(26), 0);
        return record;
    }

    private static byte[] BuildExtendedRecord(ushort dataLayout, uint address, ulong size, uint uid, uint gid)
    {
        var record = new byte[ErofsInode.ExtendedSize];
        var format = (ushort)(dataLayout | ErofsInode.FlagExtended);
        BinaryPrimitives.WriteUInt16LittleEndian(record, format);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), ErofsInode.ModeRegularFile);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8), size);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(16), address);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(20), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(24), uid);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(28), gid);
        BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(32), 1_700_000_000);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(40), 123);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(44), 1);
        return record;
    }

    private static byte[] BuildMapHeader(ulong extentCount, int recordSize)
    {
        var record = new byte[ZErofsMapHeader.Size];
        BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)extentCount);
        var exponent = System.Numerics.BitOperations.Log2((uint)recordSize);
        var advise = (ushort)(ZErofsMapHeader.AdviseExtents | ((exponent - 2) << ZErofsMapHeader.ExtRecSizeBit));
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), advise);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), (ushort)(extentCount >> 32));
        return record;
    }

    private static byte[] BuildPlainMapHeader(ushort advise)
    {
        var record = new byte[ZErofsMapHeader.Size];
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), advise);
        return record;
    }

    private static byte[] BuildFragmentMapHeader(ulong fragmentOffset)
    {
        var record = new byte[ZErofsMapHeader.Size];
        BinaryPrimitives.WriteUInt64LittleEndian(record, fragmentOffset | (1UL << 63));
        return record;
    }

    private static byte[] BuildChunkIndexTable(ulong[] starts, bool wide)
    {
        var entrySize = wide ? ErofsInode.ChunkIndexEntrySize : ErofsInode.BlockMapEntrySize;
        var table = new byte[starts.Length * entrySize];
        for (var i = 0; i < starts.Length; i++)
        {
            var slot = table.AsSpan(i * entrySize, entrySize);
            if (wide)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(slot[4..], (uint)starts[i]);
                BinaryPrimitives.WriteUInt16LittleEndian(slot, (ushort)(starts[i] >> 32));
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(slot, (uint)starts[i]);
            }
        }

        return table;
    }

    private sealed class InodeRecordOracle
    {
        public ushort Advise;
        public ushort XattrICount;
        public ushort Mode;
        public ulong Nlink;
        public ulong Size;
        public uint U;
        public uint Ino;
        public uint Uid;
        public uint Gid;
        public uint Generation;
        public ulong CTime;
        public uint CTimeNsec;
        public bool AllowNlinkOneBit;

        public bool IsExtended => (Advise & 1) != 0;

        public int OnDiskSize => IsExtended ? 64 : 32;

        public ushort DataLayoutRaw => (ushort)(Advise & (0x7 << 1));

        public bool IsCompressed => DataLayoutRaw is (1 << 1) or (3 << 1);

        public bool IsDirectory => (Mode & 0xF000) == 0x4000;

        public bool NlinkIsOne => (Advise & (1 << 4)) != 0 && !IsDirectory;

        public bool DotOmitted => (Advise & (1 << 4)) != 0 && IsDirectory;

        public ushort ChunkFormat => (ushort)U;

        public uint RawBlockAddress => U;

        public uint CompressedBlocks => U;

        public int XattrTotalSize => XattrICount == 0 ? 0 : 12 + 4 * (XattrICount - 1);

        public int InlineDataOffset => OnDiskSize + XattrTotalSize;

        public static InodeRecordOracle Decode(ReadOnlySpan<byte> data)
        {
            if (data.Length < 2)
            {
                throw new FormatException("Inode region too short.");
            }

            var advise = BinaryPrimitives.ReadUInt16LittleEndian(data);
            if ((advise & ~(ushort)0x1F) != 0)
            {
                throw new FormatException($"EROFS inode uses undefined i_format bits 0x{advise & ~(ushort)0x1F:X4}.");
            }

            if ((advise & 1) != 0)
            {
                if (data.Length < 64)
                {
                    throw new FormatException($"Extended inode region too short: {data.Length} bytes.");
                }

                return new InodeRecordOracle
                {
                    Advise = advise,
                    XattrICount = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]),
                    Mode = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]),
                    Size = BinaryPrimitives.ReadUInt64LittleEndian(data[8..]),
                    U = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]),
                    Ino = BinaryPrimitives.ReadUInt32LittleEndian(data[20..]),
                    Uid = BinaryPrimitives.ReadUInt32LittleEndian(data[24..]),
                    Gid = BinaryPrimitives.ReadUInt32LittleEndian(data[28..]),
                    CTime = BinaryPrimitives.ReadUInt64LittleEndian(data[32..]),
                    CTimeNsec = BinaryPrimitives.ReadUInt32LittleEndian(data[40..]),
                    Nlink = BinaryPrimitives.ReadUInt32LittleEndian(data[44..]),
                };
            }

            if (data.Length < 32)
            {
                throw new FormatException($"Compact inode region too short: {data.Length} bytes.");
            }

            var mode = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
            var isDirectory = (mode & 0xF000) == 0x4000;
            var narrowLinkCount = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
            var nlinkIsOne = (advise & (1 << 4)) != 0 && !isDirectory;

            return new InodeRecordOracle
            {
                Advise = advise,
                XattrICount = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]),
                Mode = mode,
                Nlink = nlinkIsOne ? 1u : narrowLinkCount,
                Size = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]),
                Generation = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]),
                U = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]),
                Ino = BinaryPrimitives.ReadUInt32LittleEndian(data[20..]),
                Uid = BinaryPrimitives.ReadUInt16LittleEndian(data[24..]),
                Gid = BinaryPrimitives.ReadUInt16LittleEndian(data[26..]),
            };
        }

        public void WriteTo(Span<byte> destination)
        {
            if (destination.Length < OnDiskSize)
            {
                throw new ArgumentException("Inode destination buffer is too small.", nameof(destination));
            }

            destination.Clear();
            if (IsExtended)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(destination, Advise);
                BinaryPrimitives.WriteUInt16LittleEndian(destination[2..], XattrICount);
                BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], Mode);
                BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], Size);
                BinaryPrimitives.WriteUInt32LittleEndian(destination[16..], U);
                BinaryPrimitives.WriteUInt32LittleEndian(destination[20..], Ino);
                BinaryPrimitives.WriteUInt32LittleEndian(destination[24..], Uid);
                BinaryPrimitives.WriteUInt32LittleEndian(destination[28..], Gid);
                BinaryPrimitives.WriteUInt64LittleEndian(destination[32..], CTime);
                BinaryPrimitives.WriteUInt32LittleEndian(destination[40..], CTimeNsec);
                BinaryPrimitives.WriteUInt32LittleEndian(destination[44..], (uint)Math.Min(Nlink, uint.MaxValue));
                return;
            }

            var nlinkOneBit = AllowNlinkOneBit && !IsDirectory && Nlink == 1;
            if (nlinkOneBit)
            {
                Advise |= 1 << 4;
            }

            BinaryPrimitives.WriteUInt16LittleEndian(destination, Advise);
            BinaryPrimitives.WriteUInt16LittleEndian(destination[2..], XattrICount);
            BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], Mode);
            var narrowLinkCount = nlinkOneBit ? (ushort)0 : (ushort)Math.Min(Nlink, ushort.MaxValue);
            BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], narrowLinkCount);
            BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], (uint)Math.Min(Size, uint.MaxValue));
            BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], Generation);
            BinaryPrimitives.WriteUInt32LittleEndian(destination[16..], U);
            BinaryPrimitives.WriteUInt32LittleEndian(destination[20..], Ino);
            BinaryPrimitives.WriteUInt16LittleEndian(destination[24..], (ushort)Math.Min(Uid, ushort.MaxValue));
            BinaryPrimitives.WriteUInt16LittleEndian(destination[26..], (ushort)Math.Min(Gid, ushort.MaxValue));
        }
    }
}