namespace UotanStudio.Comm.Transports.Hsuart;

/// <summary>
/// HDLC frame codec per the Sahara protocol spec §2.2.
/// <para>HDLC 帧编解码器（Sahara spec §2.2）。</para>
/// Frame layout: <c>0x7E | Addr | Ctrl | Info... | FCS(2) | 0x7E</c>.
/// Transparency: on send <c>0x7E→0x7D 0x5E</c>, <c>0x7D→0x7D 0x5D</c>;
/// on receive the inverse. FCS uses CRC-16/CCITT reflected (XModem reversed, poly 0x1021,
/// init 0xFFFF, low byte first).
/// <para>帧格式：<c>0x7E | Addr | Ctrl | Info... | FCS(2) | 0x7E</c>。
/// 透明化：发送时 <c>0x7E→0x7D 0x5E</c>、<c>0x7D→0x7D 0x5D</c>；
/// 接收时反向还原。FCS 采用 CRC-16/CCITT 反向（XModem 反序，多项式 0x1021，初始 0xFFFF，先低字节）。</para>
/// </summary>
public static class Hdlc
{
    /// <summary>HDLC 帧起始/结束标志字节。Start/End flag byte.</summary>
    public const byte Flag = 0x7E;

    /// <summary>HDLC 转义前缀字节。Escape prefix byte.</summary>
    public const byte Escape = 0x7D;

    /// <summary>0x7E 转义后的数据字节。Escaped form of 0x7E.</summary>
    public const byte FlagXor = 0x5E;

    /// <summary>0x7D 转义后的数据字节。Escaped form of 0x7D.</summary>
    public const byte EscapeXor = 0x5D;

    /// <summary>默认地址字节（Sahara 点对点链路）。Default address byte.</summary>
    public const byte DefaultAddress = 0xFF;

    /// <summary>默认控制字节（UI 帧，无序列号）。Default control byte (UI frame, no sequence number).</summary>
    public const byte DefaultControl = 0x03;

    /// <summary>
    /// Computes the CRC-16/CCITT FCS for a Sahara HDLC frame.
    /// <para>计算 Sahara HDLC 帧的 CRC-16/CCITT FCS。</para>
    /// Sahara spec §2.2 uses reflected CRC-16/CCITT (poly 0x1021, init 0xFFFF, both input and
    /// output reflected). This reuses the <see cref="Crc16"/> lookup table, which uses the same
    /// polynomial, to keep a single source of truth.
    /// <para>Sahara spec §2.2 采用反向 CRC-16/CCITT（多项式 0x1021，初始 0xFFFF，输入输出均反向）。
    /// 本实现复用 <see cref="Crc16"/> 的查表（其多项式与 CCITT 一致），以保持单一真相源。</para>
    /// </summary>
    /// <param name="data">Bytes to checksum. <para>参与校验的字节。</para></param>
    /// <returns>2-byte FCS (little-endian, low byte first). <para>2 字节 FCS（小端序，先低字节）。</para></returns>
    public static ushort ComputeFcs(ReadOnlySpan<byte> data) => Crc16.Calculate(data);

    /// <summary>
    /// Frames <paramref name="info"/> into an HDLC frame (with transparency, FCS, and flag bytes).
    /// <para>对 <paramref name="info"/> 进行 HDLC 帧封装（含转义、FCS、标志字节）。</para>
    /// </summary>
    /// <param name="info">The information field bytes. <para>信息字段（即原始字节）。</para></param>
    /// <param name="dst">Destination buffer, must be at least <c>info.Length * 2 + 8</c>. <para>目标缓冲区，需足够容纳最坏情况（info.Length*2 + 8）。</para></param>
    /// <param name="address">The HDLC address byte. <para>HDLC 地址字节。</para></param>
    /// <param name="control">The HDLC control byte. <para>HDLC 控制字节。</para></param>
    /// <param name="error">The result error code. <para>结果错误码。</para></param>
    /// <returns>Bytes written to <paramref name="dst"/>; 0 on error. <para>写入 <paramref name="dst"/> 的字节数；出错返回 0。</para></returns>
    public static int Encode(ReadOnlySpan<byte> info, Span<byte> dst,
        out HdlcError error, byte address = DefaultAddress, byte control = DefaultControl)
    {
        error = HdlcError.Success;

        if (info.IsEmpty)
        {
            error = HdlcError.EmptyInfo;
            return 0;
        }

        // Worst case: every byte escaped to 2 bytes, plus 2 flags + addr + ctrl + 2 FCS = 6 fixed bytes.
        // 最坏情况：每个字节都可能转义为 2 字节，加上 2 个 flag + addr + ctrl + 2 FCS = 6 字节固定开销。
        var maxNeeded = (info.Length * 2) + 8;
        if (dst.Length < maxNeeded)
        {
            error = HdlcError.DestinationTooSmall;
            return 0;
        }

        // Assemble the un-escaped frame body in a temp buffer: addr + ctrl + info + FCS(2).
        // 在临时缓冲中拼装未转义帧体：addr + ctrl + info + FCS(2)。
        var body = new byte[2 + info.Length + 2];
        body[0] = address;
        body[1] = control;
        info.CopyTo(body.AsSpan(2));
        var fcs = ComputeFcs(body.AsSpan(0, 2 + info.Length));
        // FCS low byte transmitted first (spec §2.2 HDLC LSB first).
        // FCS 低字节先发（spec §2.2 HDLC LSB first）。
        body[2 + info.Length] = (byte)(fcs & 0xFF);
        body[2 + info.Length + 1] = (byte)((fcs >> 8) & 0xFF);

        var dstIndex = 0;
        // Opening flag is not escaped.
        // 起始标志不转义。
        dst[dstIndex++] = Flag;

        // Write frame body with transparency escaping.
        // 帧体转义写入。
        for (var i = 0; i < body.Length; i++)
        {
            var b = body[i];
            if (b is Flag or Escape)
            {
                dst[dstIndex++] = Escape;
                dst[dstIndex++] = (byte)(b ^ 0x20);
            }
            else
            {
                dst[dstIndex++] = b;
            }
        }

        // Closing flag is not escaped.
        // 结束标志不转义。
        dst[dstIndex++] = Flag;

        return dstIndex;
    }

    /// <summary>
    /// Decodes an escaped HDLC frame body (without leading/trailing flags) back to the raw
    /// frame body and verifies the FCS.
    /// <para>从已转义的 HDLC 帧体（不含首尾 flag）还原原始帧体，并校验 FCS。</para>
    /// </summary>
    /// <param name="escapedBody">Escaped body with flags stripped. <para>已剥离 flag 的转义帧体。</para></param>
    /// <param name="dst">Destination for the decoded body (addr+ctrl+info+FCS). <para>解码后帧体的目标缓冲区（addr+ctrl+info+FCS）。</para></param>
    /// <param name="error">The result error code. <para>结果错误码。</para></param>
    /// <returns>Decoded body length (including FCS); 0 on error. <para>还原后的帧体长度（含 FCS）；出错返回 0。</para></returns>
    public static int Decode(ReadOnlySpan<byte> escapedBody, Span<byte> dst, out HdlcError error)
    {
        error = HdlcError.Success;

        if (escapedBody.IsEmpty || dst.IsEmpty)
        {
            error = HdlcError.EmptyInput;
            return 0;
        }

        var dstIndex = 0;
        for (var i = 0; i < escapedBody.Length; i++)
        {
            var b = escapedBody[i];
            if (b == Escape)
            {
                if (i + 1 >= escapedBody.Length)
                {
                    error = HdlcError.InvalidEscape;
                    return 0;
                }
                var next = escapedBody[++i];
                if (dstIndex >= dst.Length)
                {
                    error = HdlcError.DestinationTooSmall;
                    return 0;
                }
                dst[dstIndex++] = (byte)(next ^ 0x20);
            }
            else
            {
                if (dstIndex >= dst.Length)
                {
                    error = HdlcError.DestinationTooSmall;
                    return 0;
                }
                dst[dstIndex++] = b;
            }
        }

        // Decoded body needs at least addr(1)+ctrl(1)+FCS(2)=4 bytes.
        // 还原后帧体至少需 addr(1)+ctrl(1)+FCS(2)=4 字节。
        if (dstIndex < 4)
        {
            error = HdlcError.FrameTooShort;
            return 0;
        }

        // Verify FCS: compute CRC over [addr+ctrl+info] and compare with the trailing 2-byte FCS.
        // 校验 FCS：对 [addr+ctrl+info] 计算 CRC，与帧尾 2 字节 FCS 比较。
        var bodyLen = dstIndex - 2;
        var computed = ComputeFcs(dst[..bodyLen]);
        var receivedFcs = (ushort)(dst[bodyLen] | (dst[bodyLen + 1] << 8));
        if (computed != receivedFcs)
        {
            error = HdlcError.CrcMismatch;
            return 0;
        }

        return dstIndex;
    }

    /// <summary>
    /// Extracts the information field from a complete HDLC frame (including leading/trailing flags).
    /// <para>从完整 HDLC 帧（含首尾 flag）提取信息字段。</para>
    /// </summary>
    /// <param name="frame">Complete frame (must start and end with 0x7E). <para>完整帧（必须以 0x7E 开头和结尾）。</para></param>
    /// <param name="dst">Destination buffer for the info field. <para>信息字段的目标缓冲区。</para></param>
    /// <param name="error">The result error code. <para>结果错误码。</para></param>
    /// <returns>Info field length; 0 on error. <para>信息字段长度；出错返回 0。</para></returns>
    public static int ExtractInfo(ReadOnlySpan<byte> frame, Span<byte> dst, out HdlcError error)
    {
        error = HdlcError.Success;

        if (frame.Length < 6) // min: flag + addr + ctrl + fcs(2) + flag = 6
        {
            error = HdlcError.FrameTooShort;
            return 0;
        }

        if (frame[0] != Flag || frame[^1] != Flag)
        {
            error = HdlcError.MissingFlags;
            return 0;
        }

        // Strip leading/trailing flags, decode the escaped body in between.
        // 剥离首尾 flag，对中间转义帧体解码。
        var escapedBody = frame[1..^1];
        var body = new byte[escapedBody.Length]; // decoded never grows
        var bodyLen = Decode(escapedBody, body, out error);
        if (bodyLen == 0)
        {
            return 0;
        }

        // Info field = body - addr(1) - ctrl(1) - FCS(2).
        // 信息字段 = 帧体 - addr(1) - ctrl(1) - FCS(2)。
        var infoLen = bodyLen - 4;
        if (infoLen <= 0)
        {
            error = HdlcError.FrameTooShort;
            return 0;
        }

        if (dst.Length < infoLen)
        {
            error = HdlcError.DestinationTooSmall;
            return 0;
        }

        body.AsSpan(2, infoLen).CopyTo(dst);
        return infoLen;
    }
}

/// <summary>
/// HDLC frame codec error codes.
/// <para>HDLC 帧编解码错误码。</para>
/// </summary>
public enum HdlcError
{
    /// <summary>
    /// Operation completed successfully.
    /// <para>操作成功完成。</para>
    /// </summary>
    Success,

    /// <summary>
    /// The information field is empty.
    /// <para>信息字段为空。</para>
    /// </summary>
    EmptyInfo,

    /// <summary>
    /// The input is empty.
    /// <para>输入为空。</para>
    /// </summary>
    EmptyInput,

    /// <summary>
    /// The destination buffer is too small.
    /// <para>目标缓冲区过小。</para>
    /// </summary>
    DestinationTooSmall,

    /// <summary>
    /// A dangling escape byte was found at the end of the input.
    /// <para>输入末尾存在悬空的转义字节。</para>
    /// </summary>
    InvalidEscape,

    /// <summary>
    /// The frame is too short to be valid.
    /// <para>帧过短，无法构成有效帧。</para>
    /// </summary>
    FrameTooShort,

    /// <summary>
    /// The required opening/closing flag bytes are missing.
    /// <para>缺少必需的起始/结束标志字节。</para>
    /// </summary>
    MissingFlags,

    /// <summary>
    /// The FCS check failed.
    /// <para>FCS 校验失败。</para>
    /// </summary>
    CrcMismatch
}