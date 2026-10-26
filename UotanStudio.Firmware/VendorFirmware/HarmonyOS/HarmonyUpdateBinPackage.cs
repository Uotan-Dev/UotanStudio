namespace UotanStudio.Firmware.VendorFirmware.HarmonyOS;

/// <summary>
/// Parsed index of a HarmonyOS <c>update.bin</c> package: the component-table generation, the resolved components
/// and the metadata TLV generation that precedes the payload.
/// <para>HarmonyOS <c>update.bin</c> 包的解析索引：组件表代际、已解析组件以及载荷之前的元数据 TLV 形态。</para>
/// </summary>
internal sealed class HarmonyUpdateBinPackage
{
    /// <summary>Component-table generation detected for this package.
    /// <para>本包检测到的组件表代际。</para></summary>
    /// <value><see cref="HarmonyUpdateBinLayout.L1"/> or <see cref="HarmonyUpdateBinLayout.L2"/>.
    /// <para><see cref="HarmonyUpdateBinLayout.L1"/> 或 <see cref="HarmonyUpdateBinLayout.L2"/>。</para></value>
    public required HarmonyUpdateBinLayout Layout { get; init; }

    /// <summary>Components in table order, each carrying its absolute payload offset.
    /// <para>按表顺序排列的组件，每个组件都带有其绝对载荷偏移。</para></summary>
    /// <value>A non-empty list whose combined payload fits inside the container.
    /// <para>非空列表，其载荷总和位于容器范围内。</para></value>
    public required IReadOnlyList<HarmonyUpdateBinComponent> Components { get; init; }

    /// <summary>Absolute offset of the first component payload byte.
    /// <para>首个组件载荷字节的绝对偏移。</para></summary>
    /// <value>An offset after the header, table, reserved field and metadata TLVs.
    /// <para>头部、组件表、保留字段与元数据 TLV 之后的偏移。</para></value>
    public required long DataOffset { get; init; }

    /// <summary>Total declared payload length of every component.
    /// <para>全部组件声明的载荷总长度。</para></summary>
    /// <value>The sum of <see cref="HarmonyUpdateBinComponent.Size"/> across all components.
    /// <para>所有组件的 <see cref="HarmonyUpdateBinComponent.Size"/> 之和。</para></value>
    public required long PayloadLength { get; init; }

    /// <summary>Metadata TLV generation that precedes the payload.
    /// <para>载荷之前的元数据 TLV 形态。</para></summary>
    /// <value>The detected <see cref="HarmonyUpdateBinSignatureKind"/>.
    /// <para>检测到的 <see cref="HarmonyUpdateBinSignatureKind"/>。</para></value>
    public required HarmonyUpdateBinSignatureKind SignatureKind { get; init; }
}