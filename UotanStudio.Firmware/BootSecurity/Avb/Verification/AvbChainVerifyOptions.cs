namespace UotanStudio.Firmware.BootSecurity.Avb.Verification;

/// <summary>Configures offline AVB graph verification without device access.
/// <para>配置不访问设备的离线 AVB 图验证。</para></summary>
public sealed class AvbChainVerifyOptions
{
    /// <summary>Selects the exact root partition-map key. <para>选择分区映射中的精确根键。</para></summary>
    /// <value>A physical partition name, including its slot suffix if present. <para>物理分区名，包含已存在的槽位后缀。</para></value>
    public required string RootPartitionName { get; init; }

    /// <summary>Maps authorized partition names to raw or Android Sparse image files. <para>将授权分区名映射到原始或 Android Sparse 镜像文件。</para></summary>
    /// <value>Exact ordinal keys; descriptor names never become filesystem paths. Sparse images use a checked logical stream without disk expansion.
    /// <para>精确序数键；描述符名称不会转换为文件系统路径。Sparse 镜像使用已校验的逻辑流，不展开到磁盘。</para></value>
    public required IReadOnlyDictionary<string, string> PartitionPaths { get; init; }

    /// <summary>Selects the suffix applied to descriptors without DO_NOT_USE_AB. <para>指定用于未设置 DO_NOT_USE_AB 描述符的后缀。</para></summary>
    /// <value>Empty, or an underscore followed by at most 15 ASCII letters or digits. <para>空值，或下划线后接至多 15 个 ASCII 字母或数字。</para></value>
    public string SlotSuffix { get; init; } = string.Empty;

    /// <summary>Pins the root signer independently of the image. <para>独立于镜像固定根签名者。</para></summary>
    /// <value>A complete avbtool public-key blob, or null for self-consistency inspection without a trust claim.
    /// <para>完整 avbtool 公钥 Blob；为 null 时仅检查自洽性，不声明信任。</para></value>
    public byte[]? TrustedRootPublicKey { get; init; }

    /// <summary>Supplies externally obtained rollback minima. <para>提供外部获得的回滚索引下限。</para></summary>
    /// <value>Slots 0 through 31 and their minima; a missing visited slot fails when the map is supplied. Null leaves rollback policy unverified.
    /// <para>槽位 0 至 31 及下限；提供映射时，访问的槽位缺失即失败。为 null 时不验证回滚策略。</para></value>
    public IReadOnlyDictionary<uint, ulong>? MinimumRollbackIndexes { get; init; }

    /// <summary>Allows offline traversal beyond libavb's top-level-only chain rule. <para>允许超出 libavb 仅顶层可声明链规则的离线遍历。</para></summary>
    /// <value>False by default; true still reports nested chains and does not establish boot-policy compatibility.
    /// <para>默认为假；设为真时仍报告嵌套链，不建立启动策略兼容性。</para></value>
    public bool InspectNestedChains { get; init; }
}