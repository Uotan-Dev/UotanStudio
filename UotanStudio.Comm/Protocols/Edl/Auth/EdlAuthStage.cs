namespace UotanStudio.Comm.Protocols.Edl.Auth;

/// <summary>
/// The explicit vendor authentication stages an EDL initialization can pass through.
/// <para>EDL 初始化可能经过的显式厂商认证阶段。</para>
/// Stages are evidence labels, not a per-stage rewrite of the wire protocol: the session records the
/// ordered stages it attempted together with their outcomes, so a failed run can be classified as
/// continuable, reconnect-required or invalid without parsing log text.
/// <para>阶段是证据标签，而非对线上协议的逐阶段重写：会话按序记录已尝试的阶段及其结果，
/// 失败运行可在不解析日志文本的情况下被归类为可继续、需重连或已失效。</para>
/// </summary>
public enum EdlAuthStage
{
    /// <summary>
    /// No vendor authentication flow was selected.
    /// <para>未选择任何厂商认证流程。</para>
    /// </summary>
    NotRequired,

    /// <summary>
    /// The pre-signed VIP packet profile was installed before probing and configuration.
    /// <para>在探测与配置之前安装了预签名 VIP 包配置。</para>
    /// </summary>
    VipEnabled,

    /// <summary>
    /// The Xiaomi RSA signature challenge exchange ran after the programmer upload.
    /// <para>小米 RSA 签名质询交换在 programmer 上传之后执行。</para>
    /// </summary>
    XiaomiRsaAuth,

    /// <summary>
    /// The core Firehose configure step that precedes project authentication and SetNetType.
    /// <para>先于项目认证与 SetNetType 执行的 Firehose 核心 configure 步骤。</para>
    /// </summary>
    ConfigureBeforeAuth,

    /// <summary>
    /// A OnePlus or Nothing project verification exchange ran (serial precheck included).
    /// <para>OnePlus 或 Nothing 项目校验交换已执行（含序列号预检）。</para>
    /// </summary>
    ProjectAuth,

    /// <summary>
    /// The parameterless OnePlus SetNetType command exchange ran after configure.
    /// <para>无参数的 OnePlus SetNetType 命令交换在 configure 之后执行。</para>
    /// </summary>
    OnePlusNetType,

    /// <summary>
    /// Every selected authentication stage completed.
    /// <para>所有已选认证阶段均已完成。</para>
    /// </summary>
    Completed,

    /// <summary>
    /// A selected authentication stage failed and the run was finalized.
    /// <para>某个已选认证阶段失败，运行已定格。</para>
    /// </summary>
    Failed
}