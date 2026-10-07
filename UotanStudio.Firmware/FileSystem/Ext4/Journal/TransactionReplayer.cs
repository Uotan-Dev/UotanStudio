namespace UotanStudio.Firmware.FileSystem.Ext4.Journal;

internal static class TransactionReplayer
{
    /// <summary>
    /// Replays committed transactions into a JournalOverlay.
    /// <para>将已提交事务重放为 JournalOverlay。</para>
    /// </summary>
    /// <param name="transactions">Committed transactions in any order; the list is sorted by sequence in place and each transaction contributes its dirty blocks and revoked block numbers. An empty list yields the shared empty overlay.
    /// <para>顺序任意的已提交事务列表；会按序列号原地排序，每个事务贡献其脏块与被撤销块号。空列表返回共享的空覆盖层。</para></param>
    /// <returns>Overlay mapping each filesystem block number to the bytes of the highest sequence that rewrote it, with blocks revoked by any transaction absent from the map.
    /// <para>覆盖层把每个文件系统块号映射到重写它的最大序列号对应的字节，任一事务撤销过的块不会出现在映射中。</para></returns>
    public static JournalOverlay Replay(List<Jbd2Parser.CommittedTransaction> transactions)
    {
        ArgumentNullException.ThrowIfNull(transactions);

        if (transactions.Count == 0)
        {
            return JournalOverlay.Empty;
        }


        transactions.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));


        var allRevoked = new HashSet<long>();
        foreach (var txn in transactions)
        {
            foreach (var block in txn.RevokedBlocks)
            {
                allRevoked.Add(block);
            }
        }


        var overlay = new Dictionary<long, byte[]>();
        foreach (var txn in transactions)
        {
            foreach (var (blockNr, data) in txn.BlockMap)
            {


                overlay[blockNr] = data;
            }
        }


        foreach (var block in allRevoked)
        {
            overlay.Remove(block);
        }

        return new JournalOverlay(overlay);
    }
}