namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Identifies the flash backend an <see cref="EdlSession"/> exposes after Sahara negotiation.
/// <para>标识 Sahara 协商后 <see cref="EdlSession"/> 暴露的刷写后端。</para>
/// </summary>
public enum EdlDownloadBackend
{
    /// <summary>No backend has been negotiated yet.<para>尚未协商任何后端。</para></summary>
    None,
    /// <summary>The Firehose programmer backend, reached after a successful programmer upload and configure.<para>Firehose programmer 后端，在 programmer 上传与配置成功后进入。</para></summary>
    Firehose,
    /// <summary>The Streaming DLOAD backend, reached when a command-mode Sahara target switches via exec 0x05.<para>Streaming DLOAD 后端，在命令模式 Sahara 目标经 exec 0x05 切换后进入。</para></summary>
    StreamDload
}