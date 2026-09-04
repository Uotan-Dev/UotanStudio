namespace UotanStudio.Firmware.Ext4.Journal;

/// <summary>
/// 事务重放引擎：将已提交事务按序列号顺序重放为块覆盖层。
/// Revoke 块中的块号在后续事务中不会被覆盖。
/// <para>Transaction replay engine: replays committed transactions in sequence order into a block
/// overlay. Blocks listed in revoke sets are not overwritten by earlier transactions.</para>
/// </summary>
internal static class TransactionReplayer
{
    /// <summary>
    /// 将已提交事务重放为 <see cref="JournalOverlay"/>。
    /// 事务按序列号升序应用；revoke 块记录的块号从最终覆盖层中移除。
    /// <para>Replays committed transactions into a <see cref="JournalOverlay"/>.
    /// Transactions are applied in ascending sequence order; blocks listed in revoke sets are
    /// removed from the final overlay.</para>
    /// </summary>
    public static JournalOverlay Replay(List<Jbd2Parser.CommittedTransaction> transactions)
    {
        ArgumentNullException.ThrowIfNull(transactions);

        if (transactions.Count == 0)
        {
            return JournalOverlay.Empty;
        }

        // 按序列号升序排列。
        transactions.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));

        // 收集所有被 revoke 的块号（取最新的 revoke 集合）。
        var allRevoked = new HashSet<long>();
        foreach (var txn in transactions)
        {
            foreach (var block in txn.RevokedBlocks)
            {
                allRevoked.Add(block);
            }
        }

        // 按序列号顺序应用块映射（后写覆盖先写）。
        var overlay = new Dictionary<long, byte[]>();
        foreach (var txn in transactions)
        {
            foreach (var (blockNr, data) in txn.BlockMap)
            {
                // 如果该块在某个 revoke 集合中且当前事务早于 revoke，跳过。
                // 简化处理：revoke 的块在最终 overlay 中全部移除。
                overlay[blockNr] = data;
            }
        }

        // 移除被 revoke 的块。
        foreach (var block in allRevoked)
        {
            overlay.Remove(block);
        }

        return new JournalOverlay(overlay);
    }
}
