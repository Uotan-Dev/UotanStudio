namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.Selinux.Models;

/// <summary>
/// Read-only metadata from the bounded prefix of a SELinux binary policy.
/// <para>从 SELinux 二进制策略有界前缀读取的只读元数据。</para>
/// </summary>
public sealed record SelinuxPolicyMetadata
{
    /// <summary>The accepted SELinux kernel or module magic value.
    /// <para>已接受的 SELinux 内核策略或模块策略 magic 值。</para></summary>
    public required uint Magic { get; init; }

    /// <summary>The policy kind inferred from the magic and module type word.
    /// <para>根据 magic 与模块类型字段推断的策略种类。</para></summary>
    public required string PolicyType { get; init; }

    /// <summary>The bounded policy version read from the header.
    /// <para>从头部读取的有界策略版本。</para></summary>
    public required uint Version { get; init; }

    /// <summary>The target identifier string, normally <c>SE Linux</c>.
    /// <para>目标标识字符串，通常为 <c>SE Linux</c>。</para></summary>
    public required string Target { get; init; }

    /// <summary>The raw policy configuration flags.
    /// <para>原始策略配置标志。</para></summary>
    public required uint Configuration { get; init; }

    /// <summary>Whether the MLS configuration bit is set.
    /// <para>是否设置了 MLS 配置位。</para></summary>
    public required bool MlsEnabled { get; init; }

    /// <summary>The number of serialized symbol tables declared by libsepol compatibility data.
    /// <para>libsepol 兼容数据声明的序列化符号表数量。</para></summary>
    public required uint SymbolTableCount { get; init; }

    /// <summary>The number of object-context table families declared by the header.
    /// <para>头部声明的对象上下文表族数量。</para></summary>
    public required uint ObjectContextTableCount { get; init; }

    /// <summary>Offset immediately after the metadata header, before policy body sections.
    /// <para>元数据头之后、策略主体 section 之前的偏移。</para></summary>
    public required long BodyOffset { get; init; }

    /// <summary>The number of bytes remaining after the validated policy header.
    /// <para>通过头部校验后剩余的策略 body 字节数。</para></summary>
    public required long BodyLength { get; init; }

    /// <summary>Whether the input contains at least one policy body byte.
    /// <para>输入是否至少包含一个策略 body 字节。</para></summary>
    public bool HasBody => BodyLength > 0;

    /// <summary>Whether this parser enumerates policy rules.
    /// <para>当前解析器是否枚举策略规则。</para></summary>
    public bool RuleEnumerationSupported => false;

    /// <summary>Explains the read-only policy capability boundary.
    /// <para>说明只读策略解析能力边界。</para></summary>
    public string CapabilityStatus =>
        RuleEnumerationSupported
            ? "rules-enumerated"
            : "header-and-body-boundary-only";

    /// <summary>Always false because this bounded reader does not modify policy rules.
    /// <para>始终为 false，因为此有界读取器不修改策略规则。</para></summary>
    public bool RulesAreMutable => false;
}
