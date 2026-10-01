namespace UotanStudio.Firmware.BootSecurity.Avb.Verification;

/// <summary>Reports the requested offline checks without claiming hardware acceptance.
/// <para>报告请求的离线检查，不声明硬件接受。</para></summary>
public sealed class AvbChainVerifyResult
{
    /// <summary>Indicates whether all requested checks completed successfully. <para>表示所有请求的检查是否成功完成。</para></summary>
    /// <value>Declared FEC parity is checked, but success does not imply root trust, checked device rollback state or bootability.
    /// <para>已检查声明的 FEC 校验码，但成功不代表根信任、已检查设备回滚状态或可启动性。</para></value>
    public bool Success { get; init; }

    /// <summary>Reports the root-key pin check. <para>报告根密钥固定检查。</para></summary>
    /// <value>Null without an external pin, otherwise true only after the root signature and exact key match pass.
    /// <para>未提供外部固定密钥时为 null；否则仅在根签名与密钥精确匹配通过后为真。</para></value>
    public bool? RootKeyIsTrusted { get; init; }

    /// <summary>Reports checks against the supplied rollback map. <para>报告相对所提供回滚映射的检查。</para></summary>
    /// <value>Null without a map; true only after complete traversal with all visited counters meeting supplied minima.
    /// <para>未提供映射时为 null；仅在完整遍历且所有访问计数器均满足所提供下限后为真。</para></value>
    public bool? RollbackIndexesAreValid { get; init; }

    /// <summary>Identifies chains that cannot be used directly under libavb's top-level-only rule.
    /// <para>标识无法直接满足 libavb 仅顶层可声明链规则的链。</para></summary>
    /// <value>True once a chained image contains a further chain descriptor. <para>链式镜像包含进一步链描述符时为真。</para></value>
    public bool ContainsNestedChains { get; init; }

    /// <summary>Reports root enforcement flags without honoring bypass requests. <para>报告根强制校验标志，但不执行绕过请求。</para></summary>
    /// <value>The root header's flags, or null when it could not be read; content is checked even if flags disable enforcement.
    /// <para>根头部标志；无法读取时为 null。即使标志禁用强制校验，仍检查内容。</para></value>
    public uint? RootFlags { get; init; }

    /// <summary>Lists visited metadata images. <para>列出访问过的元数据镜像。</para></summary>
    /// <value>Per-image signature and rollback results in traversal order. <para>按遍历顺序排列的逐镜像签名与回滚结果。</para></value>
    public IReadOnlyList<AvbChainImageResult> Images { get; init; } = [];

    /// <summary>Lists content checks with resolved partition identities. <para>列出具有已解析分区身份的内容检查。</para></summary>
    /// <value>Checks actually executed before a failure; no absent check is implied to have passed.
    /// <para>失败前实际执行的检查；不暗示缺失检查已通过。</para></value>
    public IReadOnlyList<AvbChainPartitionResult> Partitions { get; init; } = [];

    /// <summary>Lists failures encountered during traversal. <para>列出遍历期间遇到的失败。</para></summary>
    /// <value>English diagnostics prefixed by the metadata partition. <para>以元数据分区为前缀的英文诊断。</para></value>
    public IReadOnlyList<string> Errors { get; init; } = [];
}

/// <summary>Describes checks on one VBMeta image. <para>描述单个 VBMeta 镜像的检查。</para></summary>
public sealed class AvbChainImageResult
{
    /// <summary>Identifies the physical partition. <para>标识物理分区。</para></summary>
    /// <value>The exact map key used to open the file. <para>用于打开文件的精确映射键。</para></value>
    public required string PartitionName { get; init; }

    /// <summary>Exposes the parsed header. <para>提供解析后的头部。</para></summary>
    /// <value>Null if metadata loading failed. <para>元数据加载失败时为 null。</para></value>
    public AvbVBMetaImageHeader? Header { get; internal set; }

    /// <summary>Reports both embedded hash and RSA signature checks. <para>报告内嵌哈希与 RSA 签名检查。</para></summary>
    /// <value>True only when both checks pass. <para>仅两项检查均通过时为真。</para></value>
    public bool SignatureIsValid { get; internal set; }

    /// <summary>Reports the expected-key comparison. <para>报告预期密钥比较。</para></summary>
    /// <value>Null if no expected key was supplied or comparison was not reached. <para>未提供预期密钥或尚未执行比较时为 null。</para></value>
    public bool? ExpectedKeyMatches { get; internal set; }

    /// <summary>Identifies the effective rollback slot. <para>标识实际使用的回滚槽位。</para></summary>
    /// <value>The root header slot or parent chain descriptor slot; null before loading metadata.
    /// <para>根头部槽位或父链描述符槽位；加载元数据前为 null。</para></value>
    public uint? RollbackIndexLocation { get; internal set; }

    /// <summary>Reports the comparison with a supplied rollback minimum. <para>报告与所提供回滚下限的比较。</para></summary>
    /// <value>Null when not evaluated. <para>未评估时为 null。</para></value>
    public bool? RollbackIndexIsValid { get; internal set; }
}

/// <summary>Associates a partition content result with its authenticated metadata source.
/// <para>将分区内容结果关联到其已认证的元数据来源。</para></summary>
public sealed class AvbChainPartitionResult
{
    /// <summary>Identifies the containing VBMeta partition. <para>标识所属 VBMeta 分区。</para></summary>
    /// <value>The exact metadata map key. <para>精确元数据映射键。</para></value>
    public required string MetadataPartition { get; init; }

    /// <summary>Identifies the resolved content partition. <para>标识解析后的内容分区。</para></summary>
    /// <value>The exact map key after applying the slot policy. <para>应用槽位策略后的精确映射键。</para></value>
    public required string PartitionName { get; init; }

    /// <summary>Exposes content, stored-tree and FEC results. <para>提供内容、存储树与 FEC 结果。</para></summary>
    /// <value>The descriptor-level verification result. <para>描述符级验证结果。</para></value>
    public required AvbPartitionVerifyResult Content { get; init; }
}