using UotanStudio.Comm.Protocols.Edl;

namespace UotanStudio.Services.Flash;

/// <summary>
/// <para>把用户选择的表文件当作 qdl VIP 序列的签名表（索引 0）交付：每次调用都返回覆盖精确字节的新只读流，
/// 所有权按 <see cref="IFirehoseVipTableProvider"/> 约定转移给协议；链式表索引一律返回 null，
/// 由协议把缺失的链式表作为失败上报，而不是猜测文件里没有的表。</para>
/// Serves the user-chosen table file as the signed table (index 0) of a qdl VIP sequence: every call hands
/// back a fresh read-only stream over the exact bytes, with ownership transferring to the protocol per the
/// <see cref="IFirehoseVipTableProvider"/> contract; chained table indices always yield null so a missing
/// chained table is reported as a failure by the protocol instead of being guessed from bytes the file
/// never contained.
/// </summary>
/// <param name="tableBytes">
/// <para>签名表的精确字节；必须为 1 到 16 MiB（协议对索引 0 的选择余量上限），构造时复制以避免外部修改。</para>
/// The exact bytes of the signed table; 1 byte through 16 MiB (the protocol's selected-remainder ceiling for
/// index 0), copied at construction so the caller cannot mutate it.
/// </param>
public sealed class EdlVipTableFileProvider(byte[] tableBytes) : IFirehoseVipTableProvider
{
    /// <summary>
    /// <para>签名表的上限，与协议在索引 0 上执行的 16 MiB 选择余量校验一致。</para>
    /// The signed-table ceiling, matching the 16 MiB selected-remainder check the protocol applies at
    /// index 0.
    /// </summary>
    public const int SignedTableMaximumBytes = 16 * 1024 * 1024;

    private readonly byte[] _tableBytes = tableBytes is { Length: > 0 } bytes && bytes.Length <= SignedTableMaximumBytes
        ? (byte[])bytes.Clone()
        : throw new ArgumentOutOfRangeException(nameof(tableBytes),
            $"The VIP table file must hold 1 to {SignedTableMaximumBytes} bytes. VIP 表文件必须为 1 到 {SignedTableMaximumBytes} 字节。");

    /// <summary>
    /// <para>索引 0 返回覆盖签名表字节的新只读内存流；任何链式表索引（1 起）返回 null 表示序列已耗尽。</para>
    /// Index 0 returns a fresh read-only memory stream over the signed-table bytes; any chained index
    /// (from 1 on) returns null, meaning the sequence is exhausted.
    /// </summary>
    /// <param name="tableIndex">
    /// <para>0 表示签名表，其后为从 1 开始的链式表索引。</para>
    /// Zero for the signed table, then one-based chained table indices.
    /// </param>
    /// <param name="cancellationToken"><para>协作式取消令牌。</para>Cooperative cancellation token.</param>
    /// <returns>
    /// <para>定位在表字节起始处的可读可定位流；链式表索引返回 null。</para>
    /// A readable seekable stream positioned at the start of the table bytes; null for chained indices.
    /// </returns>
    public Task<Stream?> OpenTableAsync(int tableIndex, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<Stream?>(tableIndex == 0
            ? new MemoryStream(_tableBytes, writable: false)
            : null);
    }
}