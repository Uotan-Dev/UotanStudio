namespace UotanStudio.Comm.Protocols.Hdc;

/// <summary>
/// A bounded HDC task channel shared by the direct device route and the HDC server route.
/// <para>直连设备路由与 HDC server 路由共用的有界 HDC 任务通道。</para>
/// </summary>
internal interface IHdcTaskChannel
{
    /// <summary>
    /// Gets whether the channel reaches its end of stream.
    /// <para>获取通道是否已到达流末尾。</para>
    /// </summary>
    /// <value><see langword="true"/> after a local close, a peer close or a fault.
    /// <para>本地关闭、对端关闭或发生故障后为 <see langword="true"/>。</para></value>
    bool IsClosed { get; }

    /// <summary>
    /// Reads the next command envelope within a wait budget.
    /// <para>在等待预算内读取下一个命令信封。</para>
    /// </summary>
    /// <param name="timeoutMs">Maximum wait for the next envelope in milliseconds; <see cref="Timeout.Infinite"/> waits indefinitely.
    /// <para>等待下一个信封的最大毫秒数；<see cref="Timeout.Infinite"/> 表示无限等待。</para></param>
    /// <param name="cancellationToken">Cancels the wait before a frame starts.
    /// <para>在帧开始之前取消等待。</para></param>
    /// <returns>The next envelope, or <see langword="null"/> when the budget elapses or the channel is closed.
    /// <para>下一个信封；预算耗尽或通道已关闭时为 <see langword="null"/>。</para></returns>
    HdcMessage? ReadMessage(int timeoutMs, CancellationToken cancellationToken);

    /// <summary>
    /// Sends one command envelope with its payload.
    /// <para>发送一个命令信封及其负载。</para>
    /// </summary>
    /// <param name="command">Command carried by the envelope.<para>信封携带的命令。</para></param>
    /// <param name="payload">Payload bytes; the array is not retained.<para>负载字节；不会保留该数组。</para></param>
    void SendCommand(HdcCommand command, byte[] payload);
}