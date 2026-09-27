namespace UotanStudio.Comm.Protocols.Mtk.XmlProfile;

/// <summary>
/// Selects how unknown fields in a device-authored XML DA message are graded.
/// <para>决定设备侧 XML DA 消息中未知字段的分级方式。</para>
/// <see cref="Strict"/> keeps the historical behavior: any field outside the profile whitelist is
/// rejected before the message is dispatched. <see cref="TolerantUnknown"/> downgrades only
/// profile-declared ignorable fields (bounded, scalar, unqualified, non-duplicate) to warning
/// evidence; structural violations, command-keyword duplicates, <c>CMD:</c> prefix violations,
/// namespace or depth violations, and unknown profile versions are always rejected under both
/// policies and never dispatched to storage or file handling.
/// <para><see cref="Strict"/> 保持历史行为：配置白名单之外的任何字段都会在消息分发前被拒绝。
/// <see cref="TolerantUnknown"/> 仅将配置声明为可忽略的字段（有界、标量、无命名空间、不重复）降级为
/// warning 证据；结构违规、命令关键字重名、<c>CMD:</c> 前缀违规、命名空间或深度违规以及未知配置版本
/// 在两种策略下都必须拒绝，绝不进入存储或文件处理。</para>
/// </summary>
public enum MtkXmlFieldPolicy
{
    /// <summary>Every unknown field is an invalid-data failure. This is the default and preserves
    /// the pre-profile parsing semantics.
    /// <para>任何未知字段都是无效数据失败。此为默认值，保持配置化之前的解析语义。</para></summary>
    Strict = 0,

    /// <summary>Profile-declared ignorable fields are recorded as warning evidence and otherwise
    /// dropped without changing the meaning of the known fields.
    /// <para>配置声明的可忽略字段会记录为 warning 证据并被丢弃，不改变已知字段的语义。</para></summary>
    TolerantUnknown
}