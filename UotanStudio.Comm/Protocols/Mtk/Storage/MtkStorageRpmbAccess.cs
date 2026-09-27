namespace UotanStudio.Comm.Protocols.Mtk.Storage;

/// <summary>
/// How the reporting profile exposed the replay-protected area to the capability contract.
/// <para>上报配置向能力契约暴露重放保护区域的方式。</para>
/// A replay-protected region is never ordinary storage on any path; <see cref="None"/> only means
/// the profile reported no authenticated region, which is weaker than proving none exists.
/// <para>在任何路径上，重放保护区域都不是普通存储；<see cref="None"/> 仅表示该配置未上报需认证的区域，
/// 这比证明其不存在要弱。</para>
/// </summary>
public enum MtkStorageRpmbAccess
{
    /// <summary>The profile reported no authenticated region for this family. <para>该配置未上报此家族的需认证区域。</para></summary>
    None,
    /// <summary>An authenticated region with reported capacity exists; ordinary block access to it is refused.
    /// <para>存在带上报容量的需认证区域；对其的普通块访问会被拒绝。</para></summary>
    Reported,
    /// <summary>
    /// A dedicated authenticated RPMB protocol is available for the reported region. No built-in
    /// UotanStudio.Comm path produces this value yet; it is reserved for an authenticated client.
    /// <para>存在面向已上报区域的专用认证 RPMB 协议。UotanStudio.Comm 目前没有任何内置路径产生此值；
    /// 它为认证客户端而预留。</para>
    /// </summary>
    AuthenticatedAvailable
}