using System.Buffers.Binary;
using UotanStudio.Firmware.FileSystem.Ext4;
using UotanStudio.Firmware.FileSystem.Ext4.Builder;
using UotanStudio.Firmware.FileSystem.Ext4.Journal;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// End-to-end ext4 journal recovery tests. <c>Ext4Builder</c> emits a clean log (an empty journal superblock with
/// <c>s_start = 0</c>) and leaves INCOMPAT_RECOVER unset, so every builder image short-circuits to an empty overlay.
/// These tests flip the recovery bit, locate the log through its journal superblock magic, plant a dirty superblock
/// plus a descriptor/data/commit triple over a real file block, and assert the parser returns the journaled bytes
/// rather than the on-disk ones. They also pin the deliberate degradation: a log that cannot be parsed is swallowed
/// and the image falls back to its on-disk content instead of failing.
/// <para>ext4 日志恢复端到端测试。<c>Ext4Builder</c> 产出的是干净日志（空日志超级块，<c>s_start = 0</c>）
/// 且不置 INCOMPAT_RECOVER，因此每个构建镜像都短路为空覆盖层。本测试翻转恢复位、经日志超级块魔数定位日志、
/// 在真实文件块上植入脏超级块与描述符/数据/提交三元组，并断言解析器返回日志后字节而非磁盘原字节。
/// 同时钉住刻意的降级行为：无法解析的日志被吞掉，镜像回退到磁盘内容而不是失败。</para>
/// </summary>
public class Ext4JournalReplayTests
{
    private const int BlockSize = 4096;
    private const int SuperblockOffset = 1024;
    private const int FeatureIncompatOffset = SuperblockOffset + 96;
    private const uint FeatureIncompatRecover = 0x4;
    private const uint FeatureCompatHasJournal = 0x4;
    private const int FeatureCompatOffset = SuperblockOffset + 92;
    private const uint BlockTypeDescriptor = 1;
    private const uint BlockTypeCommit = 2;
    private const uint TagFlagLast = 0x8;
    private const string FileName = "journaled.bin";
    private const int JournalLogBlocks = 1024;

    private static readonly byte[] DiskContent = BuildPattern(0x11);

    private static readonly byte[] JournaledContent = BuildPattern(0x22);

    private static readonly byte[] LatestContent = BuildPattern(0x33);

    private static byte[] BuildPattern(byte seed)
    {
        var data = new byte[BlockSize];
        for (var index = 0; index < data.Length; index++)
        {
            data[index] = unchecked((byte)(seed + index * 7));
        }

        return data;
    }

    private static string BuildImageWithFile(string directory)
    {
        var source = Path.Combine(directory, "src");
        Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, FileName), DiskContent);
        var image = Path.Combine(directory, "image.ext4");
        Ext4Builder.Build(source, image, new Ext4Options { BlockSize = BlockSize, IncludeJournal = true });
        return image;
    }

    private static int LocateJournalStart(byte[] image)
    {
        Span<byte> magic = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(magic, JournalSuperblock.Magic);
        for (var block = 1; block * BlockSize + BlockSize <= image.Length; block++)
        {
            if (image.AsSpan(block * BlockSize, 4).SequenceEqual(magic))
            {
                return block;
            }
        }

        throw new InvalidDataException("The built ext4 image carries no journal superblock.");
    }

    private static int LocateFileDataBlock(byte[] image)
    {
        var offset = image.AsSpan().IndexOf(DiskContent);
        if (offset < 0 || offset % BlockSize != 0)
        {
            throw new InvalidDataException($"The file content was not found on a block boundary (offset {offset}).");
        }

        return offset / BlockSize;
    }

    private static void SetIncompatRecover(byte[] image)
    {
        var current = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(FeatureIncompatOffset, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(FeatureIncompatOffset, 4), current | FeatureIncompatRecover);
    }

    private static byte[] JournalHeader(uint blockType, uint sequence)
    {
        var block = new byte[BlockSize];
        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(0), JournalSuperblock.Magic);
        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(4), blockType);
        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(8), sequence);
        return block;
    }

    private static byte[] Descriptor(uint sequence, long targetBlock)
    {
        var block = JournalHeader(BlockTypeDescriptor, sequence);
        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(JournalSuperblock.HeaderSize), (uint)targetBlock);
        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(JournalSuperblock.HeaderSize + 4), TagFlagLast);
        return block;
    }

    private static void WriteDirtySuperblock(byte[] image, int journalStart, uint start, uint sequence, uint maxLen)
    {
        var stored = JournalSuperblock.FromBytes(image.AsSpan(journalStart * BlockSize, BlockSize));
        var bytes = JournalSuperblock.CreateEmpty(
            stored.BlockSize, maxLen, stored.Uuid, checksumV3: false).ToBytes();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0x18), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0x1C), start);
        bytes.CopyTo(image, journalStart * BlockSize);
    }

    private static void WriteTransaction(byte[] image, int journalStart, int logOffset, uint sequence, long targetBlock, byte[] data)
    {
        Descriptor(sequence, targetBlock).CopyTo(image, (journalStart + logOffset) * BlockSize);
        data.CopyTo(image, (journalStart + logOffset + 1) * BlockSize);
        JournalHeader(BlockTypeCommit, sequence).CopyTo(image, (journalStart + logOffset + 2) * BlockSize);
    }

    private static byte[] ReadFile(byte[] image)
    {
        using var parser = new Ext4Parser(new MemoryStream(image, writable: false));
        using var output = new MemoryStream();
        parser.ReadFile(FileName, output);
        return output.ToArray();
    }

    [Fact]
    public void Build_IncludedJournal_WritesCleanSuperblockWithoutRecoverBit()
    {
        using var temp = new TempDirectory();
        var image = File.ReadAllBytes(BuildImageWithFile(temp.Path));

        var journalStart = LocateJournalStart(image);
        var superblock = JournalSuperblock.FromBytes(image.AsSpan(journalStart * BlockSize, BlockSize));

        Assert.True(superblock.IsEmpty);
        Assert.Equal(0u, superblock.Start);
        Assert.Equal(1u, superblock.Sequence);
        Assert.Equal(JournalLogBlocks, (int)superblock.MaxLen);
        Assert.Equal(BlockSize, (int)superblock.BlockSize);
        Assert.Equal(FeatureCompatHasJournal, BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(FeatureCompatOffset, 4)) & FeatureCompatHasJournal);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(FeatureIncompatOffset, 4)) & FeatureIncompatRecover);
    }

    [Fact]
    public void ReadFile_CleanJournalWithoutRecoverBit_ReturnsDiskContent()
    {
        using var temp = new TempDirectory();
        var image = File.ReadAllBytes(BuildImageWithFile(temp.Path));

        Assert.Equal(DiskContent, ReadFile(image));
    }

    [Fact]
    public void ReadFile_DirtyCommittedTransaction_ReturnsJournaledContentAndLeavesDiskBytes()
    {
        using var temp = new TempDirectory();
        var image = File.ReadAllBytes(BuildImageWithFile(temp.Path));
        var journalStart = LocateJournalStart(image);
        var targetBlock = LocateFileDataBlock(image);

        SetIncompatRecover(image);
        WriteTransaction(image, journalStart, logOffset: 1, sequence: 1, targetBlock, JournaledContent);
        WriteDirtySuperblock(image, journalStart, start: 1, sequence: 1, maxLen: JournalLogBlocks);

        Assert.Equal(JournaledContent, ReadFile(image));
        Assert.Equal(DiskContent, image.AsSpan(targetBlock * BlockSize, BlockSize).ToArray());
    }

    [Fact]
    public void ReadFile_RecoverBitSetButCleanJournal_ReturnsDiskContent()
    {
        using var temp = new TempDirectory();
        var image = File.ReadAllBytes(BuildImageWithFile(temp.Path));
        var journalStart = LocateJournalStart(image);
        var targetBlock = LocateFileDataBlock(image);

        SetIncompatRecover(image);
        WriteTransaction(image, journalStart, logOffset: 1, sequence: 1, targetBlock, JournaledContent);

        Assert.Equal(DiskContent, ReadFile(image));
    }

    [Fact]
    public void ReadFile_JournaledBlockOutsideTheFile_LeavesFileContentUntouched()
    {
        using var temp = new TempDirectory();
        var image = File.ReadAllBytes(BuildImageWithFile(temp.Path));
        var journalStart = LocateJournalStart(image);
        var targetBlock = LocateFileDataBlock(image);

        SetIncompatRecover(image);
        WriteTransaction(image, journalStart, logOffset: 1, sequence: 1, targetBlock + 64, JournaledContent);
        WriteDirtySuperblock(image, journalStart, start: 1, sequence: 1, maxLen: JournalLogBlocks);

        Assert.Equal(DiskContent, ReadFile(image));
    }

    [Fact]
    public void ReadFile_UncommittedTransaction_ReturnsDiskContent()
    {
        using var temp = new TempDirectory();
        var image = File.ReadAllBytes(BuildImageWithFile(temp.Path));
        var journalStart = LocateJournalStart(image);
        var targetBlock = LocateFileDataBlock(image);

        SetIncompatRecover(image);
        WriteTransaction(image, journalStart, logOffset: 1, sequence: 1, targetBlock, JournaledContent);
        image.AsSpan((journalStart + 3) * BlockSize, BlockSize).Clear();
        WriteDirtySuperblock(image, journalStart, start: 1, sequence: 1, maxLen: JournalLogBlocks);

        Assert.Equal(DiskContent, ReadFile(image));
    }

    [Fact]
    public void ReadFile_SecondCommittedTransactionWins_ReturnsLatestJournaledContent()
    {
        using var temp = new TempDirectory();
        var image = File.ReadAllBytes(BuildImageWithFile(temp.Path));
        var journalStart = LocateJournalStart(image);
        var targetBlock = LocateFileDataBlock(image);

        SetIncompatRecover(image);
        WriteTransaction(image, journalStart, logOffset: 1, sequence: 1, targetBlock, JournaledContent);
        WriteTransaction(image, journalStart, logOffset: 4, sequence: 2, targetBlock, LatestContent);
        WriteDirtySuperblock(image, journalStart, start: 1, sequence: 1, maxLen: JournalLogBlocks);

        Assert.Equal(LatestContent, ReadFile(image));
    }

    [Fact]
    public void ReadFile_UnparsableJournalSuperblock_DegradesToDiskContentWithoutThrowing()
    {
        using var temp = new TempDirectory();
        var image = File.ReadAllBytes(BuildImageWithFile(temp.Path));
        var journalStart = LocateJournalStart(image);

        SetIncompatRecover(image);
        image.AsSpan(journalStart * BlockSize, JournalSuperblock.SerializedSize).Clear();

        Assert.Equal(DiskContent, ReadFile(image));
    }

    [Fact]
    public void ReadFile_DirtyJournalWithZeroLogLength_DegradesToDiskContentWithoutThrowing()
    {
        using var temp = new TempDirectory();
        var image = File.ReadAllBytes(BuildImageWithFile(temp.Path));
        var journalStart = LocateJournalStart(image);
        var targetBlock = LocateFileDataBlock(image);

        SetIncompatRecover(image);
        WriteTransaction(image, journalStart, logOffset: 1, sequence: 1, targetBlock, JournaledContent);
        WriteDirtySuperblock(image, journalStart, start: 1, sequence: 1, maxLen: JournalLogBlocks);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(journalStart * BlockSize + 0x10), 0);

        Assert.Equal(DiskContent, ReadFile(image));
    }

    [Fact]
    public void ReadFile_JournalLogBlocksWithoutMagic_StopsScanningAndReturnsDiskContent()
    {
        using var temp = new TempDirectory();
        var image = File.ReadAllBytes(BuildImageWithFile(temp.Path));
        var journalStart = LocateJournalStart(image);
        var targetBlock = LocateFileDataBlock(image);

        SetIncompatRecover(image);
        JournaledContent.CopyTo(image, (journalStart + 2) * BlockSize);
        WriteDirtySuperblock(image, journalStart, start: 1, sequence: 1, maxLen: JournalLogBlocks);

        Assert.Equal(DiskContent, ReadFile(image));
    }

    [Fact]
    public void ListFiles_DirtyJournalEntry_KeepsStoredInodeSize()
    {
        using var temp = new TempDirectory();
        var image = File.ReadAllBytes(BuildImageWithFile(temp.Path));
        var journalStart = LocateJournalStart(image);
        var targetBlock = LocateFileDataBlock(image);

        SetIncompatRecover(image);
        WriteTransaction(image, journalStart, logOffset: 1, sequence: 1, targetBlock, JournaledContent);
        WriteDirtySuperblock(image, journalStart, start: 1, sequence: 1, maxLen: JournalLogBlocks);

        using var parser = new Ext4Parser(new MemoryStream(image, writable: false));
        var entry = Assert.Single(parser.ListFiles(), candidate => candidate.Path == FileName);

        Assert.Equal(BlockSize, entry.Size);
        Assert.Equal(Ext4Parser.Ext4EntryKind.File, entry.Kind);
    }
}