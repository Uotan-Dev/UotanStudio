using UotanStudio.Comm.Protocols.Mtk.Exploit;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>
/// The outcome of one explicitly requested MediaTek bypass attempt, including the security bits the target
/// reports after the stage-one payload ran.
/// <para>单次显式请求的联发科绕过尝试的结果，包含阶段一负载运行之后目标上报的安全位。</para>
/// </summary>
/// <remarks>
/// <para>The reference client infers that its exploit worked from a single acknowledgement word
/// (<c>mtkclient/Library/Exploit/kamakiri.py</c>, lines 146-160) and then keeps using whatever the target
/// answers. This record separates the two observations instead: the acknowledgement proves the payload ran, and
/// <see cref="SignatureChecksOpen"/> reports only what the re-queried target configuration word says afterwards.
/// Neither observation proves that a subsequent download agent will be accepted, and nothing here is a claim that
/// a device was tested.</para>
/// <para>参考实现只凭一个确认字就推断漏洞成功（<c>mtkclient/Library/Exploit/kamakiri.py</c> 第 146-160 行），随后继续沿用
/// 目标的任意应答。本记录把两项观测分开：确认字只证明负载确实运行过，而 <see cref="SignatureChecksOpen"/> 只重新查询
/// 目标配置字之后的取值。两项观测都不能证明后续下载代理会被接受，这里也不是任何“已在设备上验证”的声明。</para>
/// </remarks>
/// <param name="Family">The exploit family that was run. <para>实际运行的漏洞家族。</para></param>
/// <param name="HardwareCode">The hardware code the bypass was run against.
/// <para>执行绕过时对应的硬件码。</para></param>
/// <param name="ChipName">The reference chip name recorded for that hardware code.
/// <para>该硬件码在参考实现中记录的芯片名称。</para></param>
/// <param name="LandingAddress">The payload landing address taken from the chip table, or zero for a family that
/// places no payload. <para>取自芯片表的负载放置地址；不放置负载的家族为零。</para></param>
/// <param name="PayloadLength">Prepared payload bytes written to the target.
/// <para>写入目标的、已准备好的负载字节数。</para></param>
/// <param name="ControlProbeStalled">Whether the closing vendor control probe answered with an error, which is the
/// expected outcome of the Kamakiri crash step. <para>收尾的厂商控制探测是否以错误应答；这是 Kamakiri 崩溃步骤的预期结果。</para></param>
/// <param name="TargetConfigurationBefore">The target security bitfield read before the payload ran.
/// <para>负载运行之前读取的目标安全位字段。</para></param>
/// <param name="TargetConfigurationAfter">The target security bitfield re-queried after the payload acknowledged.
/// <para>负载确认之后重新查询的目标安全位字段。</para></param>
/// <param name="Device">The identity refreshed by the post-exploit handshake.
/// <para>由绕过后的握手刷新的设备身份。</para></param>
public sealed record MtkBypassResult(MtkExploitFamily Family, ushort HardwareCode, string ChipName,
    uint LandingAddress, int PayloadLength, bool ControlProbeStalled, uint TargetConfigurationBefore,
    uint TargetConfigurationAfter, MtkDeviceInfo Device)
{
    /// <summary>Gets whether the target no longer reports any signature or authentication requirement.
    /// <para>获取目标是否已不再上报任何签名或认证要求。</para></summary>
    /// <value>
    /// <see langword="true"/> only when the re-queried target configuration leaves the secure-boot, serial-link
    /// authentication, download-agent-authentication and root-certificate bits clear. The bit positions follow the
    /// reference decoder (<c>mtkclient/Library/mtk_preloader.py</c>, lines 510-520).
    /// <para>只有重新查询的目标配置中安全启动、串行链路认证、下载代理认证与根证书这四个位保持清零时才为
    /// <see langword="true"/>。位位置遵循参考解码器（<c>mtkclient/Library/mtk_preloader.py</c> 第 510-520 行）。</para>
    /// </value>
    public bool SignatureChecksOpen => (TargetConfigurationAfter & SignatureCheckBits) == 0;

    /// <summary>Gets whether the re-queried target configuration differs from the pre-exploit reading.
    /// <para>获取重新查询的目标配置是否不同于绕过前的读取结果。</para></summary>
    /// <value><see langword="true"/> when any bit changed. <para>有任意一位变化时为 <see langword="true"/>。</para></value>
    public bool TargetConfigurationChanged => TargetConfigurationBefore != TargetConfigurationAfter;

    private const uint SignatureCheckBits = 0x1 | 0x2 | 0x4 | 0x10;
}