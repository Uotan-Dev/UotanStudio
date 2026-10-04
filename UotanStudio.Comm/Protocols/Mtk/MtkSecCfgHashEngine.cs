namespace UotanStudio.Comm.Protocols.Mtk.Security;

/// <summary>
/// Transforms the version 4 secure-boot configuration hash with a target-driven engine, so layouts whose
/// stored digest is not reproducible off-device can be read and rewritten.
/// <para>用目标驱动的引擎变换版本 4 安全启动配置摘要，使存储摘要无法在设备外复现的布局也能读取与重写。</para>
/// </summary>
public interface IMtkSecCfgHashEngine
{
    /// <summary>Applies the transform of the given documented kind in the requested direction.
    /// <para>按请求方向应用指定已记载种类的变换。</para></summary>
    /// <param name="kind">The transform kind; <see cref="MtkSecCfgHashKind.Software"/> and
    /// <see cref="MtkSecCfgHashKind.Unresolved"/> are host-reproducible or unknown and are refused here.
    /// <para>变换种类；<see cref="MtkSecCfgHashKind.Software"/> 与 <see cref="MtkSecCfgHashKind.Unresolved"/>
    /// 属主机可复现或未知种类，在此被拒绝。</para></param>
    /// <param name="hash">The block-aligned digest bytes. <para>按块对齐的摘要字节。</para></param>
    /// <param name="encrypt"><see langword="true"/> for the forward transform, <see langword="false"/> for the
    /// inverse the documented reader applies.
    /// <para><see langword="true"/> 为正向变换，<see langword="false"/> 为已记载读取器应用的逆变换。</para></param>
    /// <param name="cancellationToken">Cancellation for every target exchange.
    /// <para>每次目标交换的取消令牌。</para></param>
    /// <returns>The transformed bytes, the same length as <paramref name="hash"/>.
    /// <para>变换后的字节，长度与 <paramref name="hash"/> 相同。</para></returns>
    Task<byte[]> TransformAsync(MtkSecCfgHashKind kind, ReadOnlyMemory<byte> hash, bool encrypt,
        CancellationToken cancellationToken);
}