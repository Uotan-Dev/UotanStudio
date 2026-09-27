namespace UotanStudio.Comm.Protocols.Mtk.Sla;

/// <summary>
/// The distinct MTK authorization flows that must never be collapsed into one another.
/// <para>彼此独立、绝不可互相折叠的 MTK 授权流程。</para>
/// BROM SLA, DA SLA, direct device authorization and boot-resource authorization each have their
/// own challenge framing, responder lifetime and failure semantics; a grant on one flow conveys
/// nothing about any other flow.
/// <para>BROM SLA、DA SLA、直接设备授权与 boot 资源授权各自拥有独立的挑战帧格式、响应方生命周期与
/// 失败语义；任一流程获得授权不代表其他流程获得任何权限。</para>
/// </summary>
public enum MtkSlaFlowKind
{
    /// <summary>The byte-stream Boot ROM SLA exchange entered after a D7 request reports 0x1D0D.
    /// <para>D7 请求返回 0x1D0D 后进入的字节流 Boot ROM SLA 交换。</para></summary>
    BootRomSla,

    /// <summary>The DA2 SLA exchange on the XFlash control wire.
    /// <para>XFlash 控制线上的 DA2 SLA 交换。</para></summary>
    DaSlaXFlash,

    /// <summary>The DA2 SLA exchange on the XML DA wire.
    /// <para>XML DA 线上的 DA2 SLA 交换。</para></summary>
    DaSlaXml,

    /// <summary>Direct device authorization against device firmware information, kept separate from
    /// DA SLA because its challenge framing and validity lifetime differ.
    /// <para>针对设备固件信息的直接设备授权；因其挑战帧格式与有效期不同，与 DA SLA 分开建模。</para></summary>
    DirectDeviceAuthorization,

    /// <summary>Authorization to upload a vendor-issued boot resource (root certificate or
    /// authentication blob) over the byte-stream wire.
    /// <para>通过字节流线上传厂商签发 boot 资源（根证书或认证资源）的授权。</para></summary>
    BootResourceAuthorization
}