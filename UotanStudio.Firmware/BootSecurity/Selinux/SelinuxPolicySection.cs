namespace UotanStudio.Firmware.BootSecurity.Selinux;

/// <summary>
/// One absolute byte span reported by the bounded SELinux policy section cursor.
/// <para>有界 SELinux 策略 section 游标报告的一个绝对字节区间。</para>
/// </summary>
/// <remarks>The policy body is serialized as one sequential little-endian stream with no
/// section index table, so spans are reported in that order and a
/// <see cref="SelinuxSectionStatus.PhaseLimited"/> span only states that the remaining bytes were
/// not inspected by this build.
/// <para>策略主体按单条顺序小端流序列化且不含 section 索引表，因此区间按该顺序报告，
/// 而 <see cref="SelinuxSectionStatus.PhaseLimited"/> 区间仅表示其余字节未被当前构建检查。</para></remarks>
public sealed record SelinuxPolicySection
{
    /// <summary>The libsepol field name of the section, such as <c>policycaps</c>.
    /// <para>section 对应的 libsepol 字段名，例如 <c>policycaps</c>。</para></summary>
    /// <value>The stable section name. <para>稳定的 section 名称。</para></value>
    public required string Name { get; init; }

    /// <summary>Absolute offset of the first byte belonging to the section.
    /// <para>section 首个字节的绝对偏移。</para></summary>
    /// <value>An offset inside the policy stream. <para>策略流内的偏移。</para></value>
    public required long StartOffset { get; init; }

    /// <summary>Absolute offset one past the last byte the cursor actually consumed.
    /// <para>游标实际消费的最后一个字节之后的绝对偏移。</para></summary>
    /// <value>The span end for consumed, truncated and rejected sections, or <see langword="null"/>
    /// when the cursor stopped before consuming any byte of the section.
    /// <para>已消费、截断或拒绝 section 的区间末端；当游标在消费该 section 任何字节之前停止时为
    /// <see langword="null"/>。</para></value>
    public long? EndOffset { get; init; }

    /// <summary>The count or declared length the section header announces.
    /// <para>section 头部声明的数量或长度。</para></summary>
    /// <value>The declared value for ebitmap and string sections, otherwise <see langword="null"/>.
    /// <para>ebitmap 与字符串 section 的声明值，其余为 <see langword="null"/>。</para></value>
    public uint? DeclaredCount { get; init; }

    /// <summary>How far the bounded cursor advanced over this section.
    /// <para>有界游标在该 section 上的推进结果。</para></summary>
    /// <value>The recorded section status. <para>记录的 section 状态。</para></value>
    public required SelinuxSectionStatus Status { get; init; }
}