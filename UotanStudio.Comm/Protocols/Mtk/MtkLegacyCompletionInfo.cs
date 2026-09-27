namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>Selects the expected Legacy startup completion framing.
/// <para>选择预期的 Legacy 启动完成帧布局。</para></summary>
public enum MtkLegacyCompletionProfile
{
    /// <summary>One ACK, two big-endian status words and SOC_OK.
    /// <para>一个 ACK、两个大端状态字和 SOC_OK。</para></summary>
    Standard,
    /// <summary>The SDK framing permits one A5 processing marker before the ACK and status words.
    /// <para>SDK 帧布局允许在 ACK 和状态字前出现一次 A5 处理标记。</para></summary>
    SdkPostProcessWait
}

/// <summary>Preserves the verified Legacy startup completion response without inferring boot-style semantics.
/// <para>保留经过校验的 Legacy 启动完成响应，不推测引导类型语义。</para></summary>
/// <param name="WaitedForProcessing">Whether the selected SDK profile consumed an A5 processing marker.
/// <para>选定的 SDK 配置是否消费了 A5 处理标记。</para></param>
/// <param name="DownloadStatus">The original big-endian download status, zero on successful startup.
/// <para>原始大端下载状态，启动成功时为零。</para></param>
/// <param name="BootStyle">The unmodified boot-style word, not interpreted as a success status.
/// <para>未修改的引导类型字，不解释为成功状态。</para></param>
/// <param name="ResponseHex">The exact ten-byte or eleven-byte response, including SOC_OK.
/// <para>精确的十字节或十一字节响应，包含 SOC_OK。</para></param>
public sealed record MtkLegacyCompletionInfo(bool WaitedForProcessing, uint DownloadStatus, uint BootStyle, string ResponseHex);