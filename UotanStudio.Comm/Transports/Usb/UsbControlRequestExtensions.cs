using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Transports.Usb;

/// <summary>
/// Convenience helpers for USB control requests and CDC-ACM line coding on sessions.
/// <para>会话上的 USB 控制请求与 CDC-ACM 线路编码便捷辅助。</para>
/// These build the standard setup packets for you and delegate to the session's
/// <see cref="IUsbDeviceSession.ControlTransfer"/>, so protocol layers (MTK serial over
/// CDC-ACM, RNDIS, vendor requests) do not hand-roll the <see cref="UsbSetupPacket"/>.
/// <para>这些方法为你构造标准 setup 包并委托给会话的
/// <see cref="IUsbDeviceSession.ControlTransfer"/>，使协议层（基于 CDC-ACM 的 MTK 串口、
/// RNDIS、厂商请求）无需手工拼装 <see cref="UsbSetupPacket"/>。</para>
/// </summary>
internal static class UsbControlRequestExtensions
{
    /// <summary>
    /// Performs a device-to-host control read and returns the response bytes.
    /// <para>执行设备到主机的控制读取并返回响应字节。</para>
    /// </summary>
    /// <param name="session">The source session. <para>源会话。</para></param>
    /// <param name="requestType">The BmRequestType byte (direction bit 7 set for IN). <para>BmRequestType 字节（IN 方向需置位 bit 7）。</para></param>
    /// <param name="request">The BRequest byte. <para>BRequest 字节。</para></param>
    /// <param name="value">The WValue field. <para>WValue 字段。</para></param>
    /// <param name="index">The WIndex field. <para>WIndex 字段。</para></param>
    /// <param name="length">The number of bytes to read. <para>要读取的字节数。</para></param>
    /// <param name="timeoutMs">Timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <returns>The response bytes. <para>响应字节。</para></returns>
    public static byte[] ControlRead(this IUsbDeviceSession session, byte requestType, byte request, ushort value, ushort index, int length, int timeoutMs)
    {
        ArgumentNullException.ThrowIfNull(session);
        var buffer = new byte[length];
        var setup = new UsbSetupPacket { RequestType = requestType, Request = request, Value = value, Index = index, Length = (ushort)length };
        var transferred = session.ControlTransfer(setup, buffer, 0, length, timeoutMs);
        if (transferred == length)
        {
            return buffer;
        }

        var result = new byte[transferred];
        Buffer.BlockCopy(buffer, 0, result, 0, transferred);
        return result;
    }

    /// <summary>
    /// Performs a host-to-device control write.
    /// <para>执行主机到设备的控制写入。</para>
    /// </summary>
    /// <param name="session">The source session. <para>源会话。</para></param>
    /// <param name="requestType">The BmRequestType byte (direction bit 7 clear for OUT). <para>BmRequestType 字节（OUT 方向需清零 bit 7）。</para></param>
    /// <param name="request">The BRequest byte. <para>BRequest 字节。</para></param>
    /// <param name="value">The WValue field. <para>WValue 字段。</para></param>
    /// <param name="index">The WIndex field. <para>WIndex 字段。</para></param>
    /// <param name="data">The data to send. <para>要发送的数据。</para></param>
    /// <param name="timeoutMs">Timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <returns>The number of bytes transferred. <para>实际传输字节数。</para></returns>
    public static int ControlWrite(this IUsbDeviceSession session, byte requestType, byte request, ushort value, ushort index, byte[] data, int timeoutMs)
    {
        ArgumentNullException.ThrowIfNull(session);
        var setup = new UsbSetupPacket { RequestType = requestType, Request = request, Value = value, Index = index, Length = (ushort)(data?.Length ?? 0) };
        return session.ControlTransfer(setup, data, 0, data?.Length ?? 0, timeoutMs);
    }

    /// <summary>
    /// Sends the CDC-ACM SET_LINE_CODING request (class request 0x20) to an interface.
    /// <para>向接口发送 CDC-ACM SET_LINE_CODING 请求（类请求 0x20）。</para>
    /// </summary>
    /// <param name="session">The source session. <para>源会话。</para></param>
    /// <param name="interfaceNumber">The communication interface number. <para>通信接口编号。</para></param>
    /// <param name="baudRate">The data terminal rate in bits per second. <para>数据传输速率（位/秒）。</para></param>
    /// <param name="charFormat">bCharFormat: 0 = 1 stop bit, 1 = 1.5, 2 = 2. <para>bCharFormat：0 = 1 停止位，1 = 1.5，2 = 2。</para></param>
    /// <param name="parityType">bParityType: 0 none, 1 odd, 2 even, 3 mark, 4 space. <para>bParityType：0 无、1 奇、2 偶、3 标记、4 空号。</para></param>
    /// <param name="dataBits">bDataBits (5-8). <para>bDataBits（5-8）。</para></param>
    /// <param name="timeoutMs">Timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    public static void SetLineCoding(this IUsbDeviceSession session, byte interfaceNumber, uint baudRate, byte charFormat, byte parityType, byte dataBits, int timeoutMs)
    {
        ArgumentNullException.ThrowIfNull(session);
        var payload = BuildLineCodingPayload(baudRate, charFormat, parityType, dataBits);
        _ = session.ControlWrite(0x21, 0x20, 0, interfaceNumber, payload, timeoutMs);
    }

    /// <summary>
    /// Reads the CDC-ACM GET_LINE_CODING response (class request 0x21) from an interface.
    /// <para>从接口读取 CDC-ACM GET_LINE_CODING 响应（类请求 0x21）。</para>
    /// </summary>
    /// <param name="session">The source session. <para>源会话。</para></param>
    /// <param name="interfaceNumber">The communication interface number. <para>通信接口编号。</para></param>
    /// <param name="timeoutMs">Timeout in milliseconds. <para>超时时间（毫秒）。</para></param>
    /// <returns>The 7-byte line coding payload (dwDTERate LE + bCharFormat + bParityType + bDataBits). <para>7 字节线路编码负载（dwDTERate 小端 + bCharFormat + bParityType + bDataBits）。</para></returns>
    public static byte[] GetLineCoding(this IUsbDeviceSession session, byte interfaceNumber, int timeoutMs)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.ControlRead(0xA1, 0x21, 0, interfaceNumber, 7, timeoutMs);
    }

    private static byte[] BuildLineCodingPayload(uint baudRate, byte charFormat, byte parityType, byte dataBits)
    {
        return
        [
            (byte)(baudRate & 0xFF),
            (byte)((baudRate >> 8) & 0xFF),
            (byte)((baudRate >> 16) & 0xFF),
            (byte)((baudRate >> 24) & 0xFF),
            charFormat,
            parityType,
            dataBits
        ];
    }
}