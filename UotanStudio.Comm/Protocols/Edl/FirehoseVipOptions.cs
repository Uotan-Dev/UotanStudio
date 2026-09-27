namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>Provides pre-signed qdl-style VIP tables matching the exact outgoing packet sequence.
/// <para>提供与精确出站包序列匹配、预先签名的 qdl 风格 VIP 表。</para></summary>
public interface IFirehoseVipTableProvider
{
    /// <summary>Opens the next exact table without generating signatures or changing its bytes.
    /// Implementations must not reenter the session.
    /// <para>打开下一张精确的表，不生成签名或修改其字节；实现不得重入会话。</para></summary>
    /// <param name="tableIndex">Zero for the signed table, then one-based chained table indices.
    /// <para>0 表示签名表，其后为从 1 开始的链式表索引。</para></param>
    /// <param name="cancellationToken">Cooperative cancellation including the table exchange deadline.
    /// <para>包含表交换截止时间的协作式取消令牌。</para></param>
    /// <returns>A readable seekable stream positioned at the exact table bytes, or null when exhausted.
    /// Ownership transfers to the protocol. The selected remainder is bounded to 16 MiB for the signed table
    /// and 8192 bytes for each chained table.
    /// <para>定位在精确表字节处的可读可定位流；表耗尽时返回 null。所有权转移给协议，
    /// 选定剩余部分的上限为签名表 16 MiB、每张链式表 8192 字节。</para></returns>
    Task<Stream?> OpenTableAsync(int tableIndex, CancellationToken cancellationToken);
}

/// <summary>
/// Selects the qdl VIP profile: 53 authenticated packets in the initial table and 255 in each chained table.
/// XML and raw data packets both consume entries; tables must match exact bytes and packet boundaries.
/// <para>选择 qdl VIP 配置：初始表包含 53 个认证包，每张链式表包含 255 个认证包。
/// XML 和 raw 数据包均消耗条目；表必须匹配精确字节和包边界。</para>
/// </summary>
public sealed record FirehoseVipOptions
{
    /// <summary>Gets the ordered table source.
    /// <para>获取按序的表来源。</para></summary>
    /// <value>The caller-selected provider, retained for the session lifetime.
    /// <para>调用方选择的提供方，在会话生命周期内保留。</para></value>
    public required IFirehoseVipTableProvider TableProvider { get; init; }

    /// <summary>Gets the raw program packet size used when preparing the signed tables.
    /// <para>获取准备签名表时使用的 raw program 包大小。</para></summary>
    /// <value>A positive sector-aligned size of at most 1 MiB; negotiation cannot silently change it.
    /// <para>不超过 1 MiB、按扇区对齐的正数大小；协商不得静默修改该值。</para></value>
    public required int ProgramPayloadSize { get; init; }

    /// <summary>Gets the cooperative budget for acquiring, sending and acknowledging one table.
    /// <para>获取取得、发送和确认单张表的协作式时间预算。</para></summary>
    /// <value>The deadline in milliseconds, from 1 through 86400000.
    /// <para>以毫秒计的截止时间，范围为 1 至 86400000。</para></value>
    public int TableTimeoutMs { get; init; } = 60000;

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(TableProvider);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ProgramPayloadSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ProgramPayloadSize, 1024 * 1024);
        FirehoseProtocol.ValidateAuthenticationTimeout(TableTimeoutMs);
    }
}