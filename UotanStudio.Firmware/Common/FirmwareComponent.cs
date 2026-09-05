namespace UotanStudio.Firmware.Common;

/// <summary>
/// A structured component entry inside a firmware container (e.g. a payload partition or a
/// boot-image kernel/ramdisk), carrying its name and size for list display.
/// <para>固件容器内的结构化组件条目（如 payload 分区、boot 镜像的 kernel/ramdisk），
/// 携带名称与大小供列表展示。</para>
/// </summary>
public sealed class FirmwareComponent
{
    /// <summary>
    /// Gets the component name (e.g. "system" or "kernel").
    /// <para>获取组件名称（如 "system" 或 "kernel"）。</para>
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the component size in bytes; 0 when unknown.
    /// <para>获取组件字节大小；未知时为 0。</para>
    /// </summary>
    public long SizeBytes { get; init; }

    /// <summary>
    /// Creates a component entry.
    /// <para>创建组件条目。</para>
    /// </summary>
    public static FirmwareComponent Of(string name, long sizeBytes) => new() { Name = name, SizeBytes = sizeBytes };
}