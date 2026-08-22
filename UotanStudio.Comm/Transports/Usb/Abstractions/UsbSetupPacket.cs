namespace UotanStudio.Comm.Transports.Usb.Abstractions;

/// <summary>
/// Describes a USB control transfer setup packet.
/// <para>描述 USB 控制传输的 setup 包。</para>
/// </summary>
public struct UsbSetupPacket
{
    /// <summary>
    /// Gets or sets the request type byte.
    /// <para>获取或设置请求类型字节。</para>
    /// </summary>
    public byte RequestType { get; set; }

    /// <summary>
    /// Gets or sets the request byte.
    /// <para>获取或设置请求字节。</para>
    /// </summary>
    public byte Request { get; set; }

    /// <summary>
    /// Gets or sets the value field.
    /// <para>获取或设置 value 字段。</para>
    /// </summary>
    public ushort Value { get; set; }

    /// <summary>
    /// Gets or sets the index field.
    /// <para>获取或设置 index 字段。</para>
    /// </summary>
    public ushort Index { get; set; }

    /// <summary>
    /// Gets or sets the length field.
    /// <para>获取或设置 length 字段。</para>
    /// </summary>
    public ushort Length { get; set; }
}