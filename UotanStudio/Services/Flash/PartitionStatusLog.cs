namespace UotanStudio.Services.Flash;

/// <summary>
/// Decides which progress status texts a page log emits: the first report of every distinct text per partition is
/// logged and later repeats of that same text are dropped.
/// <para>决定页面日志输出哪些进度状态文本：每个分区每种不同文本的首次上报会写入日志，之后同一文本的重复上报被丢弃。</para>
/// </summary>
/// A flash reports one status per phase (session opened, sparse conversion, chunk re-cut) and the same status again
/// for every wire chunk of the transfer, so gating on percentages alone hides whole phases and repeats the chunk it
/// does show. Keying on the text keeps one line per phase in the order the phases run.
/// <para>一次刷写按阶段上报状态（打开会话、转换为稀疏、重新切割），同一次传输的每个线上分块还会重复同一状态；
/// 只按百分比门控会隐藏整个阶段并重复它显示出的那个分块。按文本去重使每个阶段恰好一行，并保持阶段顺序。</para>
internal sealed class PartitionStatusLog
{
    private readonly Dictionary<string, string> _lastStatusByKey = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Records the status and reports whether this is the first report of that text for the partition.
    /// <para>记录该状态并返回它是否为该分区此文本的首次上报。</para>
    /// </summary>
    /// <param name="partition">The partition the status belongs to, or <see langword="null"/> for a shared key.
    /// <para>该状态所属的分区；使用共享键时为 <see langword="null"/>。</para></param>
    /// <param name="status">The status text carried by the report. <para>上报携带的状态文本。</para></param>
    /// <returns><see langword="true"/> to log this report; <see langword="false"/> when the text repeats.
    /// <para>为 <see langword="true"/> 时记录本次上报；文本重复时为 <see langword="false"/>。</para></returns>
    internal bool TryAccept(string? partition, string status)
    {
        var key = partition ?? string.Empty;
        if (_lastStatusByKey.TryGetValue(key, out var previous) &&
            string.Equals(previous, status, StringComparison.Ordinal))
        {
            return false;
        }

        _lastStatusByKey[key] = status;
        return true;
    }
}