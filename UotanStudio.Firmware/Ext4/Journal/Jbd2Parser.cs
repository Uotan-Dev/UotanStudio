using System.Buffers.Binary;

namespace UotanStudio.Firmware.Ext4.Journal;

/// <summary>
/// JBD2 日志解析器：扫描 journal 日志中的事务（descriptor / commit / revoke 块），
/// 返回已提交事务列表。支持 circular log wrapping、v1/v2/v3 标签格式、64 位块号和 escape flag。
/// <para>JBD2 journal parser: scans committed transactions (descriptor / commit / revoke blocks)
/// from the journal log. Supports circular log wrapping, v1/v2/v3 tag formats, 64-bit block
/// numbers and the escape flag.</para>
/// </summary>
internal sealed class Jbd2Parser
{
    /// <summary>描述符块类型。<para>Descriptor block type.</para></summary>
    private const uint BlockTypeDescriptor = 1;

    /// <summary>提交块类型。<para>Commit block type.</para></summary>
    private const uint BlockTypeCommit = 2;

    /// <summary>超级块 v1 类型。<para>Superblock v1 type.</para></summary>
    private const uint BlockTypeSuperblockV1 = 3;

    /// <summary>超级块 v2 类型。<para>Superblock v2 type.</para></summary>
    private const uint BlockTypeSuperblockV2 = 4;

    /// <summary>Revoke 块类型。<para>Revoke block type.</para></summary>
    private const uint BlockTypeRevoke = 5;

    /// <summary>标签标志：escape（数据块首 4 字节为 JBD2 魔数的 XOR 取反）。<para>Tag flag: escape.</para></summary>
    private const uint FlagEscape = 0x1;

    /// <summary>标签标志：同一 UUID（v1/v2 标签省略 UUID 字段）。<para>Tag flag: same UUID.</para></summary>
    private const uint FlagSameUuid = 0x2;

    /// <summary>标签标志：已删除。<para>Tag flag: deleted.</para></summary>
    private const uint FlagDeleted = 0x4;

    /// <summary>标签标志：最后一个标签。<para>Tag flag: last tag.</para></summary>
    private const uint FlagLastTag = 0x8;

    /// <summary>安全上限：单个事务最多处理的块数，防止损坏日志造成过量分配。
    /// <para>Safety cap: maximum blocks per transaction to prevent corrupt logs from causing excessive allocation.</para></summary>
    private const int MaxBlocksPerTransaction = 1 << 20;

    /// <summary>
    /// 已提交事务。
    /// <para>A committed journal transaction.</para>
    /// </summary>
    public sealed record CommittedTransaction(uint Sequence, Dictionary<long, byte[]> BlockMap, HashSet<long> RevokedBlocks);

    /// <summary>
    /// 扫描 journal 日志并返回所有已提交事务。journal 数据块通过 <paramref name="readBlock"/>
    /// 回调按块号读取（块号 0 = journal 超级块，1..maxLen = 日志数据块）。
    /// <para>Scans the journal log and returns all committed transactions. Journal data blocks are
    /// read via the <paramref name="readBlock"/> callback by block number (0 = journal superblock,
    /// 1..maxLen = log data blocks).</para>
    /// </summary>
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

        // 当前事务的累积状态。
        var currentSeq = journalSb.Sequence;
        var currentBlockMap = new Dictionary<long, byte[]>();
        var currentRevoked = new HashSet<long>();
        var currentBlockCount = 0;
        var hasDescriptor = false;
        var journalBlock = journalSb.Start;

        // 防御：最多扫描 maxLen * 2 个块（允许一次完整环绕）。
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
                break; // 日志尾部或损坏。
            }

            var blockType = BinaryPrimitives.ReadUInt32BigEndian(data[4..8]);
            var seq = BinaryPrimitives.ReadUInt32BigEndian(data[8..12]);

            // 序列号不匹配 → 日志结束。
            if (seq != currentSeq)
            {
                // 如果当前事务有描述符和提交，先保存。
                if (hasDescriptor)
                {
                    // 不完整事务（缺 commit），跳过。
                }

                break;
            }

            switch (blockType)
            {
                case BlockTypeDescriptor:
                    hasDescriptor = true;
                    var (tags, dataBlocksExpected) = ParseDescriptorBlock(
                        data, blockSize, has64Bit, hasCsumV3, hasCsumV2);

                    // 读取后续数据块。
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
                            // 已删除块，跳过数据。
                            continue;
                        }

                        var blockData = readBlock(journalBlock);
                        if (blockData.Length < blockSize)
                        {
                            continue;
                        }

                        // Escape flag：首 4 字节是魔数 XOR 0xFFFFFFFF。
                        if ((tag.Flags & FlagEscape) != 0 && blockData.Length >= 4)
                        {
                            var first4 = BinaryPrimitives.ReadUInt32BigEndian(blockData.AsSpan(0, 4));
                            if (first4 == (JournalSuperblock.Magic ^ 0xFFFFFFFF))
                            {
                                // 还原魔数。
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

                    // 准备下一个事务。
                    currentSeq++;
                    currentBlockMap = new Dictionary<long, byte[]>();
                    currentRevoked = new HashSet<long>();
                    currentBlockCount = 0;
                    hasDescriptor = false;
                    break;

                case BlockTypeSuperblockV1:
                case BlockTypeSuperblockV2:
                    // 跳过 journal 超级块（只在块 0）。
                    break;
            }

            journalBlock = NextBlock(journalBlock, maxLen);
        }

        return transactions;
    }

    /// <summary>解析描述符块中的标签。<para>Parses tags from a descriptor block.</para></summary>
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

            // v1/v2 的 SAME_UUID 省略 16 字节 UUID。
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

    /// <summary>获取标签大小。<para>Gets the tag size.</para></summary>
    private static int GetTagSize(bool hasCsumV3, bool hasCsumV2, bool has64Bit)
    {
        if (hasCsumV3) return 32;
        if (hasCsumV2 && has64Bit) return 24;
        if (hasCsumV2) return 16;
        if (has64Bit) return 16;
        return 8;
    }

    /// <summary>解析单个标签。<para>Parses a single tag.</para></summary>
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

    /// <summary>解析 revoke 块。<para>Parses a revoke block.</para></summary>
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

    /// <summary>日志块号循环递增。<para>Circular increment of a journal block number.</para></summary>
    private static uint NextBlock(uint current, uint maxLen)
    {
        return current >= maxLen ? 1 : current + 1;
    }

    /// <summary>描述符标签。<para>A descriptor tag.</para></summary>
    private readonly record struct DescriptorTag(long BlockNumber, uint Flags);
}
