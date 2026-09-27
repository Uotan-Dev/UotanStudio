namespace UotanStudio.Comm.Protocols.Mtk.Sla;

/// <summary>
/// Which class of response provider served one authorization flow.
/// <para>标识一次授权流程由哪一类响应提供方服务。</para>
/// The classifier records the identity of the built-in responders only; every other provider is
/// reported as external without inspecting its internals, because Comm never holds keys itself.
/// <para>分类器仅记录内置响应提供方的身份；其他所有提供方一律报告为外部提供方且不检查其内部实现，
/// 因为 Comm 自身从不持有密钥。</para>
/// </summary>
public enum MtkSlaResponderKind
{
    /// <summary>No responder was supplied or reached; recorded when a device-required flow fails
    /// closed before any challenge response is written.
    /// <para>未提供或未触达响应方；设备要求认证的流程在写出任何挑战响应之前失败关闭时记录。</para></summary>
    None = 0,

    /// <summary>A caller-supplied provider implementing the responder contract.
    /// <para>调用方实现的响应方契约提供方。</para></summary>
    ExternalProvider,

    /// <summary>The bundled RSA Boot ROM responder backed by an explicitly configured key store.
    /// <para>由显式配置密钥库支撑的内置 RSA Boot ROM 响应方。</para></summary>
    BuiltinRsaResponder
}