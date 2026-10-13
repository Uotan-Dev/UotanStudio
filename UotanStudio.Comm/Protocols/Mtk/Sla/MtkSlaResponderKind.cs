namespace UotanStudio.Comm.Protocols.Mtk.Sla;

/// <summary>
/// Which class of response provider served one authorization flow.
/// <para>标识一次授权流程由哪一类响应提供方服务。</para>
/// Every provider is caller-supplied, so the classifier distinguishes only "none" from "external"
/// without inspecting a provider's internals, because Comm never holds keys itself.
/// <para>所有提供方均由调用方提供，因此分类器只区分"无提供方"与"外部提供方"，不检查提供方的内部实现，
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
    ExternalProvider
}