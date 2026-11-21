using System.Buffers.Binary;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>Maps host baud rates onto the speed codes the download agent accepts on DA_SPEED_CMD.
/// <para>把主机波特率映射为下载代理在 DA_SPEED_CMD 上接受的速度码。</para></summary>
/// <remarks>
/// The agent does not take a numeric rate on this command. It takes a one-byte index into the fixed
/// table below, which is the <c>UART_BAUDRATE</c> enumeration of the vendor tool's download header;
/// the same enumeration is the only place a speed code is defined. Sending the numeric rate instead
/// selects whatever table entry that byte pattern happens to name, so the mapping is authoritative
/// and an unlisted rate is refused rather than guessed.
/// <para>代理在该命令上不接受数值速率，而是接受下表的一字节索引；该表就是厂商工具下载头中的
/// <c>UART_BAUDRATE</c> 枚举，也是速度码唯一的定义处。发送数值速率会选中该字节模式恰好指向的表项，
/// 因此映射必须权威：未列出的速率一律拒绝，不做猜测。</para>
/// </remarks>
internal static class MtkLegacyBaudRate
{
    /// <summary>The DA_SPEED_CMD selector that precedes the speed code.
    /// <para>位于速度码之前的 DA_SPEED_CMD 选择器。</para></summary>
    internal const byte SpeedCommand = 0xD2;

    /// <summary>The vendor table, ordered by code, from fastest to slowest.
    /// <para>厂商速度表，按码值从快到慢排列。</para></summary>
    private static readonly (int Baudrate, byte Code)[] Table =
    [
        (921600, 0x01), (460800, 0x02), (230400, 0x03), (115200, 0x04),
        (57600, 0x05), (38400, 0x06), (19200, 0x07), (9600, 0x08),
        (4800, 0x09), (2400, 0x0A), (1200, 0x0B), (300, 0x0C), (110, 0x0D)
    ];

    /// <summary>Resolves the speed code for a host baud rate.
    /// <para>为给定主机波特率解析速度码。</para></summary>
    /// <param name="baudrate">The requested rate.<para>请求的速率。</para></param>
    /// <returns>The one-byte code the agent expects.
    /// <para>代理期望的一字节码。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">The rate is absent from the vendor table; no byte was sent.
    /// <para>该速率不在厂商表中；未发送任何字节。</para></exception>
    internal static byte FromBaudrate(int baudrate)
    {
        foreach (var (rate, code) in Table)
        {
            if (rate == baudrate)
            {
                return code;
            }
        }
        throw new ArgumentOutOfRangeException(nameof(baudrate),
            "The requested baud rate is absent from the download agent's fixed speed table.");
    }

    /// <summary>Reports the fastest rate the table offers, for callers that just want the maximum.
    /// <para>报告该表提供的最高速率，供只想要最大值的调用方使用。</para></summary>
    internal static int FastestBaudrate => Table[0].Baudrate;
}