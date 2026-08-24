namespace UotanStudio.Comm.Transports.Usb.Abstractions;

/// <summary>
/// Describes the outcome of a single packet-level USB read operation.
/// <para>描述单次数据包级 USB 读取操作的结果。</para>
/// Unlike the raw <c>int</c> returned by <see cref="IUsbDeviceSession.ReadInto(byte[],int,int,int)"/>,
/// this result distinguishes a short packet (device closed the transfer with fewer bytes than
/// requested) from a timeout (no data arrived within the deadline) — information protocol
/// layers (fastboot/EDL/bootrom) need to frame their Message boundaries correctly.
/// <para>与 <see cref="IUsbDeviceSession.ReadInto(byte[],int,int,int)"/> 返回的原始 <c>int</c>
/// 不同，此结果区分"短包"（设备以少于请求的字节数结束传输）与"超时"（在期限内没有数据到达）——
/// 协议层（fastboot/EDL/bootrom）需要这些信息来正确界定消息边界。</para>
/// </summary>
/// <remarks>
/// Initializes a new read result.
/// <para>初始化新的读取结果。</para>
/// </remarks>
/// <param name="count">The number of bytes read. <para>读取的字节数。</para></param>
/// <param name="isTimeout">Whether the operation timed out. <para>操作是否超时。</para></param>
/// <param name="isShortPacket">Whether the transfer ended as a short packet. <para>传输是否以短包结束。</para></param>
public readonly struct UsbReadResult(int count, bool isTimeout, bool isShortPacket)
{
    /// <summary>
    /// Gets the number of bytes actually read.
    /// <para>获取实际读取的字节数。</para>
    /// </summary>
    public int Count { get; } = count;

    /// <summary>
    /// Gets a value indicating whether the operation timed out (no data within the deadline).
    /// <para>获取一个值，指示操作是否超时（期限内没有数据）。</para>
    /// When <c>true</c>, <see cref="Count"/> holds any bytes that arrived before the deadline
    /// (possibly zero).
    /// <para>为 <c>true</c> 时，<see cref="Count"/> 保存超时前到达的字节数（可能为零）。</para>
    /// </summary>
    public bool IsTimeout { get; } = isTimeout;

    /// <summary>
    /// Gets a value indicating whether the device ended the transfer with fewer bytes than
    /// requested (a short packet — the USB Message boundary).
    /// <para>获取一个值，指示设备是否以少于请求的字节数结束传输（短包——USB 消息边界）。</para>
    /// A short packet is a successful, complete USB transfer; it is mutually exclusive with
    /// <see cref="IsTimeout"/>.
    /// <para>短包是成功且完整的 USB 传输；它与 <see cref="IsTimeout"/> 互斥。</para>
    /// </summary>
    public bool IsShortPacket { get; } = isShortPacket;

    /// <inheritdoc />
    public override string ToString()
    {
        return IsTimeout ? $"UsbReadResult(Count={Count}, Timeout)"
            : IsShortPacket ? $"UsbReadResult(Count={Count}, ShortPacket)"
            : $"UsbReadResult(Count={Count})";
    }
}