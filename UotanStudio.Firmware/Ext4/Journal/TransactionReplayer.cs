namespace UotanStudio.Firmware.Ext4.Journal;

internal static class TransactionReplayer
{
    /// <summary>
    /// Replays committed transactions into a JournalOverlay.
    /// <para>将已提交事务重放为 JournalOverlay。</para>
    /// </summary>
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
