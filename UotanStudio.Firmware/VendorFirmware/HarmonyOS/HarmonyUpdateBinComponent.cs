namespace UotanStudio.Firmware.VendorFirmware.HarmonyOS;

/// <summary>
/// One component entry of a HarmonyOS <c>update.bin</c> package, resolved to its payload range inside the container.
/// <para>HarmonyOS <c>update.bin</c> 包中的一个组件条目，已解析到容器内的载荷区间。</para>
/// </summary>
internal sealed class HarmonyUpdateBinComponent
{
    /// <summary>Component name as stored in the table, with the leading and trailing slashes removed.
    /// <para>表中存储的组件名，已去掉首尾斜杠。</para></summary>
    /// <value>A non-empty simple name such as <c>system</c> or <c>board_list</c>.
    /// <para>非空简单名称，例如 <c>system</c> 或 <c>board_list</c>。</para></value>
    public required string Name { get; init; }

    /// <summary>File name written when the component is extracted.
    /// <para>解出该组件时写出的文件名。</para></summary>
    /// <value>The component name plus an extension derived from <see cref="ComponentType"/>, or one of the
    /// package-manifest names such as <c>BOARD.list</c>.
    /// <para>组件名加上由 <see cref="ComponentType"/> 推导的扩展名，或 <c>BOARD.list</c> 之类的包清单名。</para></value>
    public required string OutputName { get; init; }

    /// <summary>Component type stored in the table: <c>0</c> for a raw image, <c>1</c> for a ZIP payload.
    /// <para>表中存储的组件类型：<c>0</c> 表示原始镜像，<c>1</c> 表示 ZIP 载荷。</para></summary>
    /// <value>The raw type byte; values other than <c>0</c> and <c>1</c> keep the bare component name as the output name.
    /// <para>原始类型字节；非 <c>0</c>、<c>1</c> 的值以裸组件名作为输出名。</para></value>
    public required byte ComponentType { get; init; }

    /// <summary>Declared payload length in bytes.
    /// <para>声明的载荷长度（字节）。</para></summary>
    /// <value>A non-negative length that fits between the payload start and the end of the container.
    /// <para>位于载荷起点与容器末尾之间的非负长度。</para></value>
    public required long Size { get; init; }

    /// <summary>Absolute offset of the first payload byte inside the container.
    /// <para>首个载荷字节在容器内的绝对偏移。</para></summary>
    /// <value>A non-negative offset derived from the payload start plus the preceding component sizes.
    /// <para>由载荷起点加上前面各组件大小推导出的非负偏移。</para></value>
    public required long DataOffset { get; init; }

    /// <summary>SHA-256 digest recorded in the component-table record.
    /// <para>组件表记录中记录的 SHA-256 摘要。</para></summary>
    /// <value>Exactly 32 bytes; an all-zero digest means the package recorded no verifiable digest.
    /// <para>恰好 32 字节；全零表示该包没有记录可校验的摘要。</para></value>
    public required byte[] Sha256 { get; init; }
}