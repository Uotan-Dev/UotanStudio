namespace UotanStudio.Firmware.SharedKernel;

/// <summary>
/// One named artifact produced or touched by a firmware operation, together with its byte size.
/// <para>固件操作产生或涉及的单个已命名条目，附带其字节大小。</para>
/// </summary>
public sealed class FirmwareComponent
{
    /// <summary>Display name of the component, as reported by the operation.
    /// <para>组件的显示名称，按操作上报的内容呈现。</para></summary>
    /// <value>
    /// The component name, never <see langword="null"/> once the object is initialized.
    /// <para>组件名称，对象初始化后绝不为 <see langword="null"/>。</para>
    /// </value>
    public required string Name { get; init; }

    /// <summary>Component size in bytes; <c>0</c> when the operation did not measure it.
    /// <para>组件字节大小；操作未测量时为 <c>0</c>。</para></summary>
    /// <value>
    /// Byte size as reported by the operation; the property itself applies no bounds check.
    /// <para>操作上报的字节大小；属性自身不做范围校验。</para>
    /// </value>
    public long SizeBytes { get; init; }

    /// <summary>
    /// Creates a component entry.
    /// <para>创建组件条目。</para>
    /// </summary>
    /// <param name="name">
    /// Display name of the component.
    /// <para>组件的显示名称。</para>
    /// </param>
    /// <param name="sizeBytes">
    /// Component size in bytes.
    /// <para>组件字节大小。</para>
    /// </param>
    /// <returns>The populated component. <para>已填充的组件。</para></returns>
    public static FirmwareComponent Of(string name, long sizeBytes) => new() { Name = name, SizeBytes = sizeBytes };
}