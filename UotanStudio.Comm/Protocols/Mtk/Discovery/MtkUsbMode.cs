namespace UotanStudio.Comm.Protocols.Mtk.Discovery;

/// <summary>
/// The MediaTek USB boot stage a device endpoint presents, derived from descriptor evidence only.
/// <para>仅由描述符证据推导出的联发科 USB 启动阶段。</para>
/// A descriptor-level stage never proves that a download agent is already executing; the XFlash
/// connection-agent query remains the cross-check after the byte-stream handshake.
/// <para>描述符级阶段绝不证明下载代理已在运行；字节流握手之后仍需使用 XFlash 连接代理查询进行交叉验证。</para>
/// </summary>
public enum MtkUsbMode
{
    /// <summary>
    /// The endpoint identity is missing, contradictory, or outside the known MediaTek boot PIDs;
    /// callers must not bind command interfaces by enumeration order when this is returned.
    /// <para>端点身份缺失、互相矛盾，或不属于已知联发科启动 PID；返回本值时调用方不得按枚举顺序绑定命令接口。</para>
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Boot ROM stage (VID <c>0E8D</c> PID <c>0003</c>).
    /// <para>Boot ROM 阶段（VID <c>0E8D</c>、PID <c>0003</c>）。</para>
    /// </summary>
    BootRom,

    /// <summary>
    /// Preloader download stage presenting a single vendor bulk command pair and no active logging
    /// bulk-in interface; a running DA on the same PID cannot be excluded from descriptors alone.
    /// <para>Preloader 下载阶段，仅呈现单个厂商 bulk 命令对且无活动日志 bulk-IN 接口；
    /// 仅凭描述符无法排除同 PID 上已运行的 DA。</para>
    /// </summary>
    Preloader,

    /// <summary>
    /// Download-agent runtime stage: a preloader/DA PID exposing one command bulk pair plus at
    /// least one logging-only bulk-in interface; still requires the connection-agent cross-check.
    /// <para>下载代理运行阶段：preloader/DA PID 呈现一个命令 bulk 对以及至少一个仅日志的 bulk-IN 接口；
    /// 仍需连接代理交叉验证。</para>
    /// </summary>
    DaRuntime,

    /// <summary>
    /// The endpoint exposes only logging (bulk-IN without a paired bulk-OUT) vendor interfaces;
    /// commands must never be sent here.
    /// <para>端点仅暴露日志类厂商接口（bulk-IN 且无配对 bulk-OUT）；绝不得在此发送命令。</para>
    /// </summary>
    LoggingOnly,

    /// <summary>
    /// An agentless MediaTek debug-bridge endpoint presenting the <c>ff/42/01</c> ADB interface
    /// without a separate vendor bulk command pair.
    /// <para>无代理联发科调试桥端点，呈现 <c>ff/42/01</c> ADB 接口且无独立厂商 bulk 命令对。</para>
    /// </summary>
    AgentlessAdb
}