namespace UotanStudio.Firmware.BootSecurity.Avb.Verification;

/// <summary>
/// Reports partition content checks independently of VBMeta signatures and key trust.
/// <para>独立于 VBMeta 签名与密钥信任报告分区内容检查结果。</para>
/// </summary>
public sealed class AvbPartitionVerifyResult
{
    /// <summary>Identifies the descriptor's partition. <para>标识描述符中的分区。</para></summary>
    /// <value>The declared name, without filesystem path resolution. <para>声明的名称，不执行文件系统路径解析。</para></value>
    public string? PartitionName { get; init; }

    /// <summary>Indicates whether the partition content matches the descriptor. <para>表示分区内容是否匹配描述符。</para></summary>
    /// <value>True only after the content digest and, for hashtrees, every stored tree block match; this does not establish key trust.
    /// <para>仅当内容摘要匹配且哈希树模式下每个存储树块也匹配时为真；不建立密钥信任。</para></value>
    public bool ContentIsValid { get; init; }

    /// <summary>Reports the stored hashtree comparison. <para>报告存储哈希树的比较结果。</para></summary>
    /// <value>True or false after a tree comparison, or null when no tree comparison ran.
    /// <para>执行树比较后为真或假，未执行树比较时为 null。</para></value>
    public bool? StoredTreeIsValid { get; init; }

    /// <summary>Indicates whether the descriptor declares FEC data. <para>表示描述符是否声明 FEC 数据。</para></summary>
    /// <value>True when any FEC geometry field is nonzero. <para>任一 FEC 布局字段非零时为真。</para></value>
    public bool FecPresent { get; init; }

    /// <summary>Reports FEC parity validation separately from content checks. <para>独立于内容检查报告 FEC 校验结果。</para></summary>
    /// <value>Null when parity was not checked; content-only verification does not validate FEC parity.
    /// <para>未检查校验码时为 null；仅验证内容的入口不验证 FEC 校验码。</para></value>
    public bool? FecIsValid { get; init; }

    /// <summary>Describes a rejected layout, unsupported feature or mismatch. <para>描述被拒绝的布局、不支持的特性或不匹配。</para></summary>
    /// <value>An English diagnostic, or null after all requested content and parity checks pass.
    /// <para>英文诊断；请求的内容与校验码检查全部通过后为 null。</para></value>
    public string? ErrorMessage { get; init; }
}