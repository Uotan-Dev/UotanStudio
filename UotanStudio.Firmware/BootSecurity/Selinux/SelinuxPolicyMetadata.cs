namespace UotanStudio.Firmware.BootSecurity.Selinux;

/// <summary>
/// Read-only metadata from the bounded prefix of a SELinux binary policy.
/// <para>从 SELinux 二进制策略有界前缀读取的只读元数据。</para>
/// </summary>
internal sealed record SelinuxPolicyMetadata
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

    /// <summary>Body sections reported by the bounded section cursor in libsepol read order.
    /// <para>有界 section 游标按 libsepol 读取顺序报告的 body section。</para></summary>
    /// <value>Never empty; the final entry is always the single phase-limited span beginning where the
    /// cursor stopped, and its <see cref="SelinuxPolicySection.EndOffset"/> is <see langword="null"/>
    /// when no byte is left.
    /// <para>始终非空；最后一项恒为从游标停止处开始的单个 phase 受限区间，其
    /// <see cref="SelinuxPolicySection.EndOffset"/> 在没有剩余字节时为 <see langword="null"/>。</para></value>
    public required IReadOnlyList<SelinuxPolicySection> Sections { get; init; }

    /// <summary>Whether every body byte was covered by a section the cursor actually parsed.
    /// <para>body 的每个字节是否都被游标真正解析过的 section 覆盖。</para></summary>
    /// <value><see langword="true"/> only when the cursor reached the body end without truncation
    /// or rejection; a phase-limited tail keeps this <see langword="false"/>.
    /// <para>仅在游标在未截断且未拒绝的情况下到达 body 末尾时为 <see langword="true"/>；
    /// 存在 phase 受限尾部时保持 <see langword="false"/>。</para></value>
    public required bool BodyFullyConsumed { get; init; }

    /// <summary>Whether the input contains at least one policy body byte.
    /// <para>输入是否至少包含一个策略 body 字节。</para></summary>
    public bool HasBody => BodyLength > 0;

    /// <summary>Body bytes not covered by any parsed section.
    /// <para>未被任何已解析 section 覆盖的 body 字节数。</para></summary>
    /// <value>The body length minus the bytes of sections reported as
    /// <see cref="SelinuxSectionStatus.Consumed"/>, which equals <see cref="BodyLength"/> when the
    /// cursor parsed nothing and equals the phase-limited tail span when the cursor stopped cleanly.
    /// <para>body 长度减去报告为 <see cref="SelinuxSectionStatus.Consumed"/> 的 section 字节数；
    /// 游标未解析任何内容时等于 <see cref="BodyLength"/>，游标正常停止时等于 phase 受限区间的长度。</para></value>
    public long UnconsumedTailLength
    {
        get
        {
            var consumed = 0L;
            foreach (var section in Sections)
            {
                if (section is { Status: SelinuxSectionStatus.Consumed, EndOffset: { } end })
                    consumed = checked(consumed + (end - section.StartOffset));
            }
            return checked(BodyLength - consumed);
        }
    }

    /// <summary>Whether this parser enumerates policy rules.
    /// <para>当前解析器是否枚举策略规则。</para></summary>
    public bool RuleEnumerationSupported => false;

    /// <summary>Explains the read-only policy capability boundary.
    /// <para>说明只读策略解析能力边界。</para></summary>
    public string CapabilityStatus =>
        RuleEnumerationSupported
            ? "rules-enumerated"
            : "header-and-bounded-section-cursor";

    /// <summary>Always false because this bounded reader does not modify policy rules.
    /// <para>始终为 false，因为此有界读取器不修改策略规则。</para></summary>
    public bool RulesAreMutable => false;
}