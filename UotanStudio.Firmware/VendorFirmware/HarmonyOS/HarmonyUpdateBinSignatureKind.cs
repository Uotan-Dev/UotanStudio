namespace UotanStudio.Firmware.VendorFirmware.HarmonyOS;

/// <summary>
/// Metadata TLVs that follow the component table of a HarmonyOS <c>update.bin</c> package.
/// <para>HarmonyOS <c>update.bin</c> 包组件表之后的元数据 TLV 形态。</para>
/// </summary>
internal enum HarmonyUpdateBinSignatureKind
{
    /// <summary>A single signature TLV (<c>0x08</c>) follows the reserved field.
    /// <para>保留字段之后是单个签名 TLV（<c>0x08</c>）。</para></summary>
    Signature,

    /// <summary>A hash-info (<c>0x06</c>), hash-data (<c>0x07</c>) and signature (<c>0x08</c>) TLV chain follows the reserved field.
    /// <para>保留字段之后是 hash-info（<c>0x06</c>）、hash-data（<c>0x07</c>）与签名（<c>0x08</c>）TLV 链。</para></summary>
    HashAndSignature
}