using System.Buffers.Binary;

namespace UotanStudio.Firmware.FileSystem.Ext4.Journal;

internal sealed class Jbd2Parser
{
    private const uint BlockTypeDescriptor = 1;

    private const uint BlockTypeCommit = 2;

    private const uint BlockTypeSuperblockV1 = 3;

    private const uint BlockTypeSuperblockV2 = 4;

    private const uint BlockTypeRevoke = 5;

    private const uint FlagEscape = 0x1;

    private const uint FlagSameUuid = 0x2;

    private const uint FlagDeleted = 0x4;

    private const uint FlagLastTag = 0x8;

    private const int MaxBlocksPerTransaction = 1 << 20;

    public sealed record CommittedTransaction(uint Sequence, Dictionary<long, byte[]> BlockMap, HashSet<long> RevokedBlocks);

    /// <summary>
    /// Scans the journal log and returns all committed transactions.
    /// <para>扫描 journal 日志并返回所有已提交事务。</para>
    /// </summary>
    /// <param name="journalSb">Parsed journal superblock supplying the start block, first sequence number, block count, block size and feature flags; a superblock without journal contents ends the scan immediately.
    /// <para>已解析的日志超级块，提供起始块号、首个事务序列号、块数、块大小与特性标志；没有日志内容时扫描立即结束。</para></param>
    /// <param name="readBlock">Reads one journal block by its 1-based block number and returns its bytes; a block shorter than the journal block size is left out of the transaction.
    /// <para>按从 1 开始的块号读取单个日志块并返回其字节；短于日志块大小的块不计入事务。</para></param>
    /// <returns>Transactions holding both a descriptor block and a matching commit block, in ascending sequence order; the walk covers only the consecutive blocks from the start block that keep the recorded sequence.
    /// <para>同时具备描述符块与匹配提交块的事务，按序列号升序排列；扫描只覆盖从起始块开始且序列号连续的相邻块。</para></returns>
    public static List<CommittedTransaction> ScanTransactions(
        JournalSuperblock journalSb,
        Func<uint, byte[]> readBlock)
    {
        ArgumentNullException.ThrowIfNull(journalSb);
        ArgumentNullException.ThrowIfNull(readBlock);

        if (journalSb.IsEmpty)
        {
            return [];
        }

        var maxLen = journalSb.MaxLen;
        var has64Bit = journalSb.Has64Bit;
        var hasCsumV3 = journalSb.HasCsumV3;
        var hasCsumV2 = journalSb.HasCsumV2;
        var blockSize = checked((int)journalSb.BlockSize);
        var transactions = new List<CommittedTransaction>();


        var currentSeq = journalSb.Sequence;
        var currentBlockMap = new Dictionary<long, byte[]>();
        var currentRevoked = new HashSet<long>();
        var currentBlockCount = 0;
        var hasDescriptor = false;
        var journalBlock = journalSb.Start;


        var maxScan = checked((long)maxLen * 2);
        for (var scanned = 0; scanned < maxScan && journalBlock >= 1 && journalBlock <= maxLen; scanned++)
        {
            var data = readBlock(journalBlock);
            if (data.Length < JournalSuperblock.HeaderSize)
            {
                break;
            }

            var magic = BinaryPrimitives.ReadUInt32BigEndian(data[0..4]);
            if (magic != JournalSuperblock.Magic)
            {
                break;
            }

            var blockType = BinaryPrimitives.ReadUInt32BigEndian(data[4..8]);
            var seq = BinaryPrimitives.ReadUInt32BigEndian(data[8..12]);


            if (seq != currentSeq)
            {

                if (hasDescriptor)
                {

                }

                break;
            }

            switch (blockType)
            {
                case BlockTypeDescriptor:
                    hasDescriptor = true;
                    var (tags, dataBlocksExpected) = ParseDescriptorBlock(
                        data, blockSize, has64Bit, hasCsumV3, hasCsumV2);


                    for (var i = 0; i < tags.Count; i++)
                    {
                        journalBlock = NextBlock(journalBlock, maxLen);
                        if (journalBlock < 1 || journalBlock > maxLen)
                        {
                            break;
                        }

                        var tag = tags[i];
                        if ((tag.Flags & FlagDeleted) != 0)
                        {

                            continue;
                        }

                        var blockData = readBlock(journalBlock);
                        if (blockData.Length < blockSize)
                        {
                            continue;
                        }


                        if ((tag.Flags & FlagEscape) != 0 && blockData.Length >= 4)
                        {
                            var first4 = BinaryPrimitives.ReadUInt32BigEndian(blockData.AsSpan(0, 4));
                            if (first4 == (JournalSuperblock.Magic ^ 0xFFFFFFFF))
                            {

                                BinaryPrimitives.WriteUInt32BigEndian(blockData.AsSpan(0, 4), JournalSuperblock.Magic);
                            }
                        }

                        currentBlockMap[tag.BlockNumber] = blockData;
                        currentBlockCount++;

                        if (currentBlockCount > MaxBlocksPerTransaction)
                        {
                            break;
                        }
                    }

                    break;

                case BlockTypeRevoke:
                    ParseRevokeBlock(data, has64Bit, currentRevoked);
                    break;

                case BlockTypeCommit:
                    if (hasDescriptor)
                    {
                        transactions.Add(new CommittedTransaction(currentSeq, currentBlockMap, currentRevoked));
                    }


                    currentSeq++;
                    currentBlockMap = new Dictionary<long, byte[]>();
                    currentRevoked = new HashSet<long>();
                    currentBlockCount = 0;
                    hasDescriptor = false;
                    break;

                case BlockTypeSuperblockV1:
                case BlockTypeSuperblockV2:

                    break;
            }

            journalBlock = NextBlock(journalBlock, maxLen);
        }

        return transactions;
    }

    /// <summary>
    /// Parses tags from a descriptor block.
    /// <para>解析描述符块中的标签。</para>
    /// </summary>
    private static (List<DescriptorTag> Tags, int DataBlockCount) ParseDescriptorBlock(
        byte[] data, int blockSize, bool has64Bit, bool hasCsumV3, bool hasCsumV2)
    {
        var tags = new List<DescriptorTag>();
        var offset = JournalSuperblock.HeaderSize;
        var tagSize = GetTagSize(hasCsumV3, hasCsumV2, has64Bit);

        while (offset + tagSize <= blockSize)
        {
            var tag = ParseTag(data.AsSpan(offset, tagSize), hasCsumV3, hasCsumV2, has64Bit);
            var effectiveTagSize = tagSize;


            if (!hasCsumV3 && (tag.Flags & FlagSameUuid) != 0)
            {
                effectiveTagSize = Math.Max(tagSize - 16, 8);
            }

            tags.Add(tag);
            offset += effectiveTagSize;

            if ((tag.Flags & FlagLastTag) != 0)
            {
                break;
            }
        }

        return (tags, tags.Count);
    }

    /// <summary>
    /// Gets the tag size.
    /// <para>获取标签大小。</para>
    /// </summary>
    private static int GetTagSize(bool hasCsumV3, bool hasCsumV2, bool has64Bit)
    {
        if (hasCsumV3) return 32;
        if (hasCsumV2 && has64Bit) return 24;
        if (hasCsumV2) return 16;
        if (has64Bit) return 16;
        return 8;
    }

    /// <summary>
    /// Parses a single tag.
    /// <para>解析单个标签。</para>
    /// </summary>
    private static DescriptorTag ParseTag(ReadOnlySpan<byte> data, bool hasCsumV3, bool hasCsumV2, bool has64Bit)
    {
        var blockNr = (long)BinaryPrimitives.ReadUInt32BigEndian(data[0..4]);
        var flags = BinaryPrimitives.ReadUInt32BigEndian(data[4..8]);

        if (has64Bit || hasCsumV3)
        {
            var high = (long)BinaryPrimitives.ReadUInt32BigEndian(data[8..12]);
            blockNr |= high << 32;
        }

        return new DescriptorTag(blockNr, flags);
    }

    /// <summary>
    /// Parses a revoke block.
    /// <para>解析 revoke 块。</para>
    /// </summary>
    private static void ParseRevokeBlock(byte[] data, bool has64Bit, HashSet<long> revoked)
    {
        if (data.Length < JournalSuperblock.HeaderSize + 4)
        {
            return;
        }

        var rCount = BinaryPrimitives.ReadUInt32BigEndian(
            data.AsSpan(JournalSuperblock.HeaderSize, 4));
        var entrySize = has64Bit ? 8 : 4;
        var entryOffset = JournalSuperblock.HeaderSize + 4;
        var entryEnd = Math.Min((int)rCount, data.Length);

        var pos = entryOffset;
        while (pos + entrySize <= entryEnd)
        {
            long blockNr;
            if (has64Bit)
            {
                blockNr = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(pos, 8));
            }
            else
            {
                blockNr = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos, 4));
            }

            revoked.Add(blockNr);
            pos += entrySize;
        }
    }

    /// <summary>
    /// Circular increment of a journal block number.
    /// <para>日志块号循环递增。</para>
    /// </summary>
    private static uint NextBlock(uint current, uint maxLen)
    {
        return current >= maxLen ? 1 : current + 1;
    }

    private readonly record struct DescriptorTag(long BlockNumber, uint Flags);
}