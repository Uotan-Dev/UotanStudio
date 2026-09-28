namespace UotanStudio.Firmware.BootSecurity.Selinux;

/// <summary>
/// Describes how far the bounded SELinux policy section cursor advanced over one section.
/// <para>描述有界 SELinux 策略 section 游标对单个 section 的处理结果。</para>
/// </summary>
public enum SelinuxSectionStatus
{
    /// <summary>The section layout was validated and every declared byte was consumed.
    /// <para>已校验 section 布局并消费了其声明的全部字节。</para></summary>
    Consumed,

    /// <summary>The section is well formed but this build deliberately stops before it.
    /// <para>section 本身格式良好，但当前构建有意在其之前停止解析。</para></summary>
    /// <remarks>This is a parser scope limit, not a defect in the inspected input.
    /// <para>这是解析器范围限制，不是被检查输入的缺陷。</para></remarks>
    PhaseLimited,

    /// <summary>The byte stream ended inside a phase-one section.
    /// <para>字节流在 phase-one section 内部结束。</para></summary>
    Truncated,

    /// <summary>The section violates a documented libsepol structural rule.
    /// <para>section 违反了 libsepol 已声明的结构规则。</para></summary>
    Rejected
}