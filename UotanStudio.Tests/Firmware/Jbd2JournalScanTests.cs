using System.Buffers.Binary;
using UotanStudio.Firmware.FileSystem;
using UotanStudio.Firmware.FileSystem.Ext4.Journal;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// JBD2 journal scanning and replay tests. <see cref="Jbd2Parser.ScanTransactions"/> takes a block reader delegate,
/// so the log is synthesized in memory instead of requiring a mounted or native-tool-produced image; these tests pin
/// the four descriptor tag widths, escape restoration, revoke parsing, the rule that only a descriptor-bearing
/// sequence commits, sequence breaks and the circular block walk, plus the revoke-over-write precedence in
/// <see cref="TransactionReplayer"/> and the <see cref="JournalSuperblock"/> wire round-trip.
/// <para>JBD2 日志扫描与重放测试。<see cref="Jbd2Parser.ScanTransactions"/> 接受块读取委托，
/// 因此日志在内存中合成，无需挂载镜像或原生工具产出的镜像；本测试钉住四种描述符 tag 宽度、escape 还原、
/// revoke 解析、"仅带描述符的序号才提交"这一规则、序号断裂与环形块推进，
/// 以及 <see cref="TransactionReplayer"/> 中 revoke 优先于 write 的语义和 <see cref="JournalSuperblock"/>
/// 的线格式往返。</para>
/// </summary>
public class Jbd2JournalScanTests
{
    private const uint BlockSize = 128;
    private const uint MaxLen = 16;
    private const uint EscapeFlag = 0x1;
    private const uint SameUuidFlag = 0x2;
    private const uint DeletedFlag = 0x4;
    private const uint LastTagFlag = 0x8;
    private const uint BlockTypeDescriptor = 1;
    private const uint BlockTypeCommit = 2;
    private const uint BlockTypeRevoke = 5;
    private const uint FeatureIncompat64Bit = 0x2;
    private const uint FeatureIncompatCsumV2 = 0x8;
    private const uint FeatureIncompatCsumV3 = 0x10;

    private static byte[] SuperblockBytes(uint start, uint sequence, uint featureIncompat, uint maxLen = MaxLen, uint blockSize = BlockSize)
    {
        var bytes = new byte[JournalSuperblock.SerializedSize];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0), JournalSuperblock.Magic);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), JournalSuperblock.BlockTypeSuperblockV2);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0x0C), blockSize);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0x10), maxLen);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0x14), 1);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0x18), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0x1C), start);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0x28), featureIncompat);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0x40), 1);
        return bytes;
    }

    private static JournalSuperblock Superblock(uint start, uint sequence, uint featureIncompat, uint maxLen = MaxLen, uint blockSize = BlockSize)
        => JournalSuperblock.FromBytes(SuperblockBytes(start, sequence, featureIncompat, maxLen, blockSize));

    private static byte[] Header(uint blockType, uint sequence, uint blockSize = BlockSize)
    {
        var block = new byte[blockSize];
        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(0), JournalSuperblock.Magic);
        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(4), blockType);
        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(8), sequence);
        return block;
    }

    private static byte[] Descriptor(uint sequence, (long BlockNumber, uint Flags)[] tags, int tagSize)
    {
        var block = Header(BlockTypeDescriptor, sequence);
        var offset = JournalSuperblock.HeaderSize;
        foreach (var (blockNumber, flags) in tags)
        {
            BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(offset), (uint)(blockNumber & 0xFFFF_FFFF));
            BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(offset + 4), flags);
            if (tagSize > 8)
            {
                BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(offset + 8), (uint)(blockNumber >> 32));
            }

            offset += tagSize;
        }

        return block;
    }

    private static byte[] Revoke(uint sequence, long[] blocks, bool has64Bit)
    {
        var block = Header(BlockTypeRevoke, sequence);
        var entrySize = has64Bit ? 8 : 4;
        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(JournalSuperblock.HeaderSize),
            (uint)(JournalSuperblock.HeaderSize + sizeof(uint) + blocks.Length * entrySize));
        var offset = JournalSuperblock.HeaderSize + sizeof(uint);
        foreach (var blockNumber in blocks)
        {
            if (has64Bit)
            {
                BinaryPrimitives.WriteUInt64BigEndian(block.AsSpan(offset), (ulong)blockNumber);
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(offset), (uint)blockNumber);
            }

            offset += entrySize;
        }

        return block;
    }

    private static byte[] Data(byte fill = 0xAB)
    {
        var block = new byte[BlockSize];
        block.AsSpan().Fill(fill);
        return block;
    }

    private static Func<uint, byte[]> Reader(IReadOnlyDictionary<uint, byte[]> blocks)
        => number => blocks.TryGetValue(number, out var block) ? block : new byte[BlockSize];

    private static Jbd2Parser.CommittedTransaction Transaction(uint sequence, Dictionary<long, byte[]> blocks, HashSet<long>? revoked = null)
        => new(sequence, blocks, revoked ?? []);

    [Fact]
    public void ScanTransactions_EmptySuperblock_ReturnsNoTransactions()
    {
        var journal = Superblock(start: 0, sequence: 1, featureIncompat: 0);

        Assert.True(journal.IsEmpty);
        Assert.Empty(Jbd2Parser.ScanTransactions(journal, Reader(new Dictionary<uint, byte[]>())));
    }

    [Fact]
    public void ScanTransactions_DescriptorDataCommit_ReturnsMappedBlock()
    {
        var journal = Superblock(start: 1, sequence: 7, featureIncompat: 0);
        var blocks = new Dictionary<uint, byte[]>
        {
            [1] = Descriptor(7, [(BlockNumber: 42, Flags: LastTagFlag)], tagSize: 8),
            [2] = Data(),
            [3] = Header(BlockTypeCommit, 7)
        };

        var transaction = Assert.Single(Jbd2Parser.ScanTransactions(journal, Reader(blocks)));

        Assert.Equal(7u, transaction.Sequence);
        Assert.Equal(0xAB, Assert.Single(transaction.BlockMap).Value[0]);
        Assert.Equal(42L, Assert.Single(transaction.BlockMap).Key);
        Assert.Empty(transaction.RevokedBlocks);
    }

    [Theory]
    [InlineData(0x0u, 8, 42L)]
    [InlineData(FeatureIncompat64Bit, 16, 0x1_0000_002AL)]
    [InlineData(FeatureIncompatCsumV2, 16, 42L)]
    [InlineData(FeatureIncompat64Bit | FeatureIncompatCsumV2, 24, 0x1_0000_002AL)]
    [InlineData(FeatureIncompatCsumV3, 32, 0x1_0000_002AL)]
    public void ScanTransactions_TagWidths_ReadBlockNumberFromMatchingLayout(uint featureIncompat, int tagSize, long expectedBlock)
    {
        var journal = Superblock(start: 1, sequence: 1, featureIncompat: featureIncompat);
        var blocks = new Dictionary<uint, byte[]>
        {
            [1] = Descriptor(1, [(BlockNumber: expectedBlock, Flags: LastTagFlag)], tagSize),
            [2] = Data(),
            [3] = Header(BlockTypeCommit, 1)
        };

        var transaction = Assert.Single(Jbd2Parser.ScanTransactions(journal, Reader(blocks)));

        Assert.Equal(expectedBlock, Assert.Single(transaction.BlockMap).Key);
    }

    [Fact]
    public void ScanTransactions_SameUuidTag_UsesEightByteStride()
    {
        var journal = Superblock(start: 1, sequence: 1, featureIncompat: FeatureIncompatCsumV2);
        var block = Header(BlockTypeDescriptor, 1);
        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(JournalSuperblock.HeaderSize), 11);
        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(JournalSuperblock.HeaderSize + 4), SameUuidFlag);
        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(JournalSuperblock.HeaderSize + 8), 22);
        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(JournalSuperblock.HeaderSize + 12), LastTagFlag);
        var blocks = new Dictionary<uint, byte[]>
        {
            [1] = block,
            [2] = Data(fill: 0xAB),
            [3] = Data(fill: 0xCD),
            [4] = Header(BlockTypeCommit, 1)
        };

        var transaction = Assert.Single(Jbd2Parser.ScanTransactions(journal, Reader(blocks)));

        Assert.Equal(2, transaction.BlockMap.Count);
        Assert.Equal(0xAB, transaction.BlockMap[11][0]);
        Assert.Equal(0xCD, transaction.BlockMap[22][0]);
    }

    [Fact]
    public void ScanTransactions_EscapedDataBlock_RestoresJournalMagic()
    {
        var journal = Superblock(start: 1, sequence: 1, featureIncompat: 0);
        var escaped = Data();
        BinaryPrimitives.WriteUInt32BigEndian(escaped.AsSpan(0), JournalSuperblock.Magic ^ 0xFFFF_FFFF);
        var blocks = new Dictionary<uint, byte[]>
        {
            [1] = Descriptor(1, [(BlockNumber: 5, Flags: EscapeFlag | LastTagFlag)], tagSize: 8),
            [2] = escaped,
            [3] = Header(BlockTypeCommit, 1)
        };

        var transaction = Assert.Single(Jbd2Parser.ScanTransactions(journal, Reader(blocks)));

        Assert.Equal(JournalSuperblock.Magic, BinaryPrimitives.ReadUInt32BigEndian(transaction.BlockMap[5].AsSpan(0, 4)));
    }

    [Fact]
    public void ScanTransactions_EscapedDataBlockWithoutEscapeFlag_KeepsEscapedBytes()
    {
        var journal = Superblock(start: 1, sequence: 1, featureIncompat: 0);
        var escaped = Data();
        BinaryPrimitives.WriteUInt32BigEndian(escaped.AsSpan(0), JournalSuperblock.Magic ^ 0xFFFF_FFFF);
        var blocks = new Dictionary<uint, byte[]>
        {
            [1] = Descriptor(1, [(BlockNumber: 5, Flags: LastTagFlag)], tagSize: 8),
            [2] = escaped,
            [3] = Header(BlockTypeCommit, 1)
        };

        var transaction = Assert.Single(Jbd2Parser.ScanTransactions(journal, Reader(blocks)));

        Assert.Equal(JournalSuperblock.Magic ^ 0xFFFF_FFFF, BinaryPrimitives.ReadUInt32BigEndian(transaction.BlockMap[5].AsSpan(0, 4)));
    }

    [Fact]
    public void ScanTransactions_DeletedTag_SkipsBlockButConsumesJournalSlot()
    {
        var journal = Superblock(start: 1, sequence: 1, featureIncompat: 0);
        var blocks = new Dictionary<uint, byte[]>
        {
            [1] = Descriptor(1, [(BlockNumber: 5, Flags: DeletedFlag), (BlockNumber: 6, Flags: LastTagFlag)], tagSize: 8),
            [3] = Data(fill: 0xCD),
            [4] = Header(BlockTypeCommit, 1)
        };

        var transaction = Assert.Single(Jbd2Parser.ScanTransactions(journal, Reader(blocks)));

        Assert.Equal(6, Assert.Single(transaction.BlockMap).Key);
        Assert.Equal(0xCD, transaction.BlockMap[6][0]);
    }

    [Fact]
    public void ScanTransactions_RevokeBlock_CollectsBlockNumbers()
    {
        var journal = Superblock(start: 1, sequence: 1, featureIncompat: 0);
        var blocks = new Dictionary<uint, byte[]>
        {
            [1] = Revoke(1, [9, 10], has64Bit: false),
            [2] = Descriptor(1, [(BlockNumber: 3, Flags: LastTagFlag)], tagSize: 8),
            [3] = Data(),
            [4] = Header(BlockTypeCommit, 1)
        };

        var transaction = Assert.Single(Jbd2Parser.ScanTransactions(journal, Reader(blocks)));

        Assert.Equal([9L, 10L], transaction.RevokedBlocks.Order().ToArray());
        Assert.Equal(3, Assert.Single(transaction.BlockMap).Key);
    }

    [Fact]
    public void ScanTransactions_SixtyFourBitRevokeEntries_ReadEightByteBlockNumbers()
    {
        var journal = Superblock(start: 1, sequence: 1, featureIncompat: FeatureIncompat64Bit);
        var blocks = new Dictionary<uint, byte[]>
        {
            [1] = Revoke(1, [0x2_0000_0001L], has64Bit: true),
            [2] = Descriptor(1, [(BlockNumber: 3, Flags: LastTagFlag)], tagSize: 16),
            [3] = Data(),
            [4] = Header(BlockTypeCommit, 1)
        };

        var transaction = Assert.Single(Jbd2Parser.ScanTransactions(journal, Reader(blocks)));

        Assert.Equal([0x2_0000_0001L], [.. transaction.RevokedBlocks]);
    }

    [Fact]
    public void ScanTransactions_CommitWithoutDescriptor_DoesNotEmitTransaction()
    {
        var journal = Superblock(start: 1, sequence: 1, featureIncompat: 0);
        var blocks = new Dictionary<uint, byte[]>
        {
            [1] = Header(BlockTypeCommit, 1),
            [2] = Descriptor(2, [(BlockNumber: 4, Flags: LastTagFlag)], tagSize: 8),
            [3] = Data(),
            [4] = Header(BlockTypeCommit, 2)
        };

        var transaction = Assert.Single(Jbd2Parser.ScanTransactions(journal, Reader(blocks)));

        Assert.Equal(2u, transaction.Sequence);
        Assert.Equal(4, Assert.Single(transaction.BlockMap).Key);
    }

    [Fact]
    public void ScanTransactions_SequenceBreak_StopsAtFirstMismatchingBlock()
    {
        var journal = Superblock(start: 1, sequence: 5, featureIncompat: 0);
        var blocks = new Dictionary<uint, byte[]>
        {
            [1] = Descriptor(5, [(BlockNumber: 4, Flags: LastTagFlag)], tagSize: 8),
            [2] = Data(),
            [3] = Header(BlockTypeDescriptor, 9),
            [4] = Header(BlockTypeCommit, 5)
        };

        Assert.Empty(Jbd2Parser.ScanTransactions(journal, Reader(blocks)));
    }

    [Fact]
    public void ScanTransactions_BlockWithoutJournalMagic_StopsScanning()
    {
        var journal = Superblock(start: 1, sequence: 1, featureIncompat: 0);
        var blocks = new Dictionary<uint, byte[]>
        {
            [1] = Data(),
            [2] = Header(BlockTypeCommit, 1)
        };

        Assert.Empty(Jbd2Parser.ScanTransactions(journal, Reader(blocks)));
    }

    [Fact]
    public void ScanTransactions_ShortBlock_StopsScanning()
    {
        var journal = Superblock(start: 1, sequence: 1, featureIncompat: 0);
        var blocks = new Dictionary<uint, byte[]>
        {
            [1] = new byte[JournalSuperblock.HeaderSize - 1],
            [2] = Header(BlockTypeCommit, 1)
        };

        Assert.Empty(Jbd2Parser.ScanTransactions(journal, Reader(blocks)));
    }

    [Fact]
    public void ScanTransactions_LogWrapsPastMaxLen_ContinuesFromBlockOne()
    {
        const uint maxLen = 4;
        var journal = Superblock(start: 3, sequence: 1, featureIncompat: 0, maxLen: maxLen);
        var blocks = new Dictionary<uint, byte[]>
        {
            [3] = Descriptor(1, [(BlockNumber: 7, Flags: LastTagFlag)], tagSize: 8),
            [4] = Data(),
            [1] = Header(BlockTypeCommit, 1)
        };

        var transaction = Assert.Single(Jbd2Parser.ScanTransactions(journal, Reader(blocks)));

        Assert.Equal(7, Assert.Single(transaction.BlockMap).Key);
    }

    [Fact]
    public void ScanTransactions_MultipleCommittedSequences_ReturnsOneTransactionEach()
    {
        var journal = Superblock(start: 1, sequence: 1, featureIncompat: 0);
        var blocks = new Dictionary<uint, byte[]>
        {
            [1] = Descriptor(1, [(BlockNumber: 3, Flags: LastTagFlag)], tagSize: 8),
            [2] = Data(fill: 0x11),
            [3] = Header(BlockTypeCommit, 1),
            [4] = Descriptor(2, [(BlockNumber: 4, Flags: LastTagFlag)], tagSize: 8),
            [5] = Data(fill: 0x22),
            [6] = Header(BlockTypeCommit, 2)
        };

        var transactions = Jbd2Parser.ScanTransactions(journal, Reader(blocks));

        Assert.Equal([1u, 2u], transactions.Select(transaction => transaction.Sequence).ToArray());
        Assert.Equal(0x11, transactions[0].BlockMap[3][0]);
        Assert.Equal(0x22, transactions[1].BlockMap[4][0]);
    }

    [Fact]
    public void ScanTransactions_NullArguments_ThrowsArgumentNullException()
    {
        var journal = Superblock(start: 1, sequence: 1, featureIncompat: 0);

        Assert.Throws<ArgumentNullException>(() => Jbd2Parser.ScanTransactions(null!, Reader(new Dictionary<uint, byte[]>())));
        Assert.Throws<ArgumentNullException>(() => Jbd2Parser.ScanTransactions(journal, null!));
    }

    [Fact]
    public void Replay_EmptyTransactionList_ReturnsSharedEmptyOverlay()
    {
        var overlay = TransactionReplayer.Replay([]);

        Assert.Same(JournalOverlay.Empty, overlay);
        Assert.False(overlay.HasOverlay);
        Assert.Equal(0, overlay.Count);
    }

    [Fact]
    public void JournalOverlay_NullMap_ThrowsArgumentNullException()
        => Assert.Throws<ArgumentNullException>(() => new JournalOverlay(null!));

    [Fact]
    public void Replay_LaterSequenceWins_OverwritesEarlierBlockData()
    {
        var overlay = TransactionReplayer.Replay(
        [
            Transaction(2, new Dictionary<long, byte[]> { [5] = Data(fill: 0x22) }),
            Transaction(1, new Dictionary<long, byte[]> { [5] = Data(fill: 0x11) })
        ]);

        Assert.True(overlay.TryGetBlock(5, out var block));
        Assert.Equal(0x22, block[0]);
        Assert.Equal(1, overlay.Count);
    }

    [Fact]
    public void Replay_RevokedBlockIsDropped_EvenWhenWrittenByALaterSequence()
    {
        var overlay = TransactionReplayer.Replay(
        [
            Transaction(1, new Dictionary<long, byte[]> { [5] = Data(), [6] = Data() }),
            Transaction(2, new Dictionary<long, byte[]> { [5] = Data(fill: 0x22) }, [5L])
        ]);

        Assert.False(overlay.TryGetBlock(5, out _));
        Assert.True(overlay.TryGetBlock(6, out _));
        Assert.Equal(1, overlay.Count);
    }

    [Fact]
    public void Replay_RevokedBlockNeverWritten_ReportsNoOverlay()
    {
        var overlay = TransactionReplayer.Replay([Transaction(1, [], [3L])]);

        Assert.False(overlay.HasOverlay);
        Assert.False(overlay.TryGetBlock(3, out _));
    }

    [Fact]
    public void Replay_NullTransactionList_ThrowsArgumentNullException()
        => Assert.Throws<ArgumentNullException>(() => TransactionReplayer.Replay(null!));

    [Fact]
    public void JournalSuperblock_CreateEmpty_RoundTripsThroughBytes()
    {
        var uuid = new byte[16];
        uuid.AsSpan().Fill(0x5A);
        var created = JournalSuperblock.CreateEmpty(4096, 1024, uuid, checksumV3: false);

        var parsed = JournalSuperblock.FromBytes(created.ToBytes());

        Assert.Equal(JournalSuperblock.Magic, parsed.HeaderMagic);
        Assert.Equal(JournalSuperblock.BlockTypeSuperblockV2, parsed.HeaderBlockType);
        Assert.Equal(4096u, parsed.BlockSize);
        Assert.Equal(1024u, parsed.MaxLen);
        Assert.Equal(1u, parsed.First);
        Assert.Equal(1u, parsed.Sequence);
        Assert.Equal(0u, parsed.Start);
        Assert.True(parsed.IsEmpty);
        Assert.Equal(uuid, parsed.Uuid);
        Assert.False(parsed.HasCsumV3);
        Assert.False(parsed.Has64Bit);
        Assert.False(parsed.HasCsumV2);
        Assert.Equal(JournalSuperblock.SerializedSize, created.ToBytes().Length);
    }

    [Fact]
    public void JournalSuperblock_ChecksumV3_WritesCrc32cAndReportsFeature()
    {
        var bytes = JournalSuperblock.CreateEmpty(4096, 64, checksumV3: true).ToBytes();

        var parsed = JournalSuperblock.FromBytes(bytes);

        Assert.True(parsed.HasCsumV3);
        Assert.Equal(JournalSuperblock.ChecksumTypeCrc32c, parsed.ChecksumType);
        Assert.NotEqual(0u, parsed.Checksum);
        Assert.Equal(parsed.Checksum, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(0x204, 4)));
    }

    [Theory]
    [InlineData(512u, 8u)]
    [InlineData(3000u, 8u)]
    [InlineData(4096u, 1u)]
    public void JournalSuperblock_CreateEmpty_RejectsInvalidGeometry(uint blockSize, uint maxLen)
        => Assert.Throws<ArgumentOutOfRangeException>(() => JournalSuperblock.CreateEmpty(blockSize, maxLen));

    [Fact]
    public void JournalSuperblock_CreateEmpty_RejectsWrongUuidLength()
        => Assert.Throws<ArgumentException>(() => JournalSuperblock.CreateEmpty(4096, 8, new byte[15]));

    [Fact]
    public void JournalSuperblock_CreateEmpty_GeneratesSixteenByteUuidWhenAbsent()
        => Assert.Equal(16, JournalSuperblock.CreateEmpty(4096, 8).Uuid.Length);

    [Fact]
    public void JournalSuperblock_FromBytes_RejectsBadMagicTruncationAndBlockType()
    {
        var bytes = JournalSuperblock.CreateEmpty(4096, 8).ToBytes();

        Assert.Throws<FileSystemCorruptedException>(() => JournalSuperblock.FromBytes(bytes.AsSpan(0, JournalSuperblock.MinimumSize - 1)));

        var badMagic = (byte[])bytes.Clone();
        badMagic[0] ^= 0xFF;
        Assert.Throws<FileSystemCorruptedException>(() => JournalSuperblock.FromBytes(badMagic));

        var badBlockType = (byte[])bytes.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(badBlockType.AsSpan(4), 9);
        Assert.Throws<FileSystemCorruptedException>(() => JournalSuperblock.FromBytes(badBlockType));
    }

    [Fact]
    public void JournalSuperblock_FromBytes_AcceptsLegacySuperblockV1BlockType()
    {
        var bytes = JournalSuperblock.CreateEmpty(4096, 8).ToBytes();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), 3);

        Assert.Equal(3u, JournalSuperblock.FromBytes(bytes).HeaderBlockType);
    }

    [Fact]
    public void JournalSuperblock_FeatureFlags_DeriveFromIncompatMask()
    {
        var parsed = JournalSuperblock.FromBytes(SuperblockBytes(
            start: 0, sequence: 1,
            featureIncompat: JournalSuperblock.FeatureIncompatRevoke | FeatureIncompat64Bit
                | JournalSuperblock.FeatureIncompatAsyncCommit | FeatureIncompatCsumV2
                | JournalSuperblock.FeatureIncompatFastCommit));

        Assert.True(parsed.HasRevoke);
        Assert.True(parsed.Has64Bit);
        Assert.True(parsed.HasCsumV2);
        Assert.True(parsed.HasAsyncCommit);
        Assert.False(parsed.HasCsumV3);
        Assert.Equal(0x20u, JournalSuperblock.FeatureIncompatFastCommit);
    }
}