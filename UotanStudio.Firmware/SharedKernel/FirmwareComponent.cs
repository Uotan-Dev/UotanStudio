namespace UotanStudio.Firmware.SharedKernel;

public sealed class FirmwareComponent
{
    public required string Name { get; init; }

    public long SizeBytes { get; init; }

    /// <summary>
    /// Creates a component entry.
    /// <para>创建组件条目。</para>
    /// </summary>
    public static FirmwareComponent Of(string name, long sizeBytes) => new() { Name = name, SizeBytes = sizeBytes };
}