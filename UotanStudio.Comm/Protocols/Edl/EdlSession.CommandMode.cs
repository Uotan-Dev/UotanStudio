namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// The session-side gate for Sahara exec command-mode diagnostics.
/// <para>面向 Sahara exec 命令模式诊断的会话侧护栏。</para>
/// </summary>
public sealed partial class EdlSession
{
    /// <summary>Resolves the Sahara instance that carries the command-mode exchanges.
    /// <para>取得承载命令模式交换的 Sahara 实例。</para></summary>
    /// <remarks>
    /// The instance the session created during initialization wins because it already holds the recorded identity and the negotiated version, and since a target HELLO appears only once per link every later diagnostic has to land on that same negotiated instance. A link that already uploaded a programmer, switched to Firehose or entered the streaming downloader has left command mode, so this refuses outright. The guard inspects recorded state instead of probing, because probing would consume the HELLO and change the negotiation sequence on the target; only the stages the session already recorded are read, and the target's own HELLO remains the authoritative answer for the mode.
    /// <para>会话在初始化期间创建的那份优先，因为它已经带着记录到的身份与协商版本；一条链路上的 HELLO 只 出现一次，所以后续诊断必须落在同一个已协商的实例上。链路一旦上传过 programmer、切换过 Firehose 或 进入流式下载器，命令模式就已经结束，此处直接拒绝。该护栏是状态检查而非探测：探测会消耗 HELLO， 也会改变目标侧的协商序列，因此这里只读取会话已经记录的阶段，并以目标 HELLO 自报的模式作为权威答案。</para>
    /// </remarks>
    /// <returns>The session's Sahara instance, created on the transport this session already leased.
    /// <para>会话的 Sahara 实例，建立在本会话已经租下的传输之上。</para></returns>
    /// <exception cref="InvalidOperationException">The link stored a configuration, uploaded a programmer, switched to Firehose or entered Streaming DLOAD.
    /// <para>链路已写入配置、上传过 programmer、切换到 Firehose 或进入流式 DLOAD。</para></exception>
    public SaharaProtocol EnsureCommandModeSahara()
    {
        ThrowIfDisposed();
        if (IsConfigured || Backend != EdlDownloadBackend.None
            || _protocolState is EdlProtocolState.Firehose or EdlProtocolState.Streaming
            || LinkStage is EdlLinkStage.ProgrammerUploading or EdlLinkStage.AwaitingReenumeration
                or EdlLinkStage.FirehoseVerifying or EdlLinkStage.FirehoseReady)
        {
            throw new InvalidOperationException(
                "Sahara exec diagnostics only run on a link that never uploaded a programmer and never switched " +
                "to Firehose or Streaming DLOAD. Sahara exec 诊断只能作用于从未上传 programmer、也从未切换 " +
                "Firehose 或流式 DLOAD 的链路。");
        }

        return Sahara ??= new SaharaProtocol(Transport);
    }
}