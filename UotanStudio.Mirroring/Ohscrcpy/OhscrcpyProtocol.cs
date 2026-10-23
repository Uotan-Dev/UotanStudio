using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Mirroring;

/// <summary>Video stream configuration announced by the ohscrcpy server.
/// <para>ohscrcpy 服务端宣告的视频流配置。</para></summary>
/// <param name="Codec">Codec identifier: <see cref="OhscrcpyConstants.VideoCodecH264"/>,
/// <see cref="OhscrcpyConstants.VideoCodecRawRgba"/> or <see cref="OhscrcpyConstants.VideoCodecJpeg"/>.
/// <para>编码标识：<see cref="OhscrcpyConstants.VideoCodecH264"/>、<see cref="OhscrcpyConstants.VideoCodecRawRgba"/>
/// 或 <see cref="OhscrcpyConstants.VideoCodecJpeg"/>。</para></param>
/// <param name="Width">Capture width in pixels; always even. <para>采集宽度（像素），始终为偶数。</para></param>
/// <param name="Height">Capture height in pixels; always even. <para>采集高度（像素），始终为偶数。</para></param>
/// <param name="FrameRate">Encoder frame rate in frames per second. <para>编码器帧率（帧/秒）。</para></param>
/// <param name="Sps">H.264 SPS NAL unit without a start code; empty for RAW/JPEG.
/// <para>不含起始码的 H.264 SPS NAL 单元；RAW/JPEG 时为空。</para></param>
/// <param name="Pps">H.264 PPS NAL unit without a start code; empty for RAW/JPEG.
/// <para>不含起始码的 H.264 PPS NAL 单元；RAW/JPEG 时为空。</para></param>
public sealed record OhscrcpyVideoConfig(byte Codec, int Width, int Height, int FrameRate, byte[] Sps, byte[] Pps)
{
    /// <summary>Whether the stream carries H.264 Annex-B access units.
    /// <para>码流是否承载 H.264 Annex-B 访问单元。</para></summary>
    /// <value><see langword="true"/> when <see cref="Codec"/> is <see cref="OhscrcpyConstants.VideoCodecH264"/>.
    /// <para><see cref="Codec"/> 为 <see cref="OhscrcpyConstants.VideoCodecH264"/> 时为 <see langword="true"/>。</para></value>
    public bool IsH264 => Codec == OhscrcpyConstants.VideoCodecH264;

    /// <summary>Codec name used in diagnostics. <para>用于诊断的编码名称。</para></summary>
    /// <value><c>H264</c>, <c>RAW_RGBA</c>, <c>JPEG</c> or <c>UNKNOWN(n)</c>. <para><c>H264</c>、<c>RAW_RGBA</c>、<c>JPEG</c> 或 <c>UNKNOWN(n)</c>。</para></value>
    public string CodecName => Codec switch
    {
        OhscrcpyConstants.VideoCodecH264 => "H264",
        OhscrcpyConstants.VideoCodecRawRgba => "RAW_RGBA",
        OhscrcpyConstants.VideoCodecJpeg => "JPEG",
        _ => $"UNKNOWN({Codec})",
    };

    /// <summary>
    /// Builds the Annex-B codec configuration the decoder pipeline consumes: each NAL unit prefixed with a
    /// four-byte start code.
    /// <para>构建解码管线消费的 Annex-B 编码配置：每个 NAL 单元前加四字节起始码。</para>
    /// </summary>
    /// <returns>The start-code-prefixed SPS and PPS, or an empty array for a non-H.264 codec.
    /// <para>带起始码前缀的 SPS 与 PPS；非 H.264 编码时为空数组。</para></returns>
    public byte[] BuildAnnexBConfiguration()
    {
        if (!IsH264) return [];
        var result = new byte[8 + Sps.Length + Pps.Length];
        BinaryPrimitives.WriteUInt32BigEndian(result, 1);
        Sps.CopyTo(result, 4);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4 + Sps.Length), 1);
        Pps.CopyTo(result, 8 + Sps.Length);
        return result;
    }
}

/// <summary>One device → client status message carried on the single ohscrcpy connection.
/// <para>单条 ohscrcpy 连接上承载的一条设备到客户端状态消息。</para></summary>
/// <param name="SubType">Status sub-type, for example <see cref="OhscrcpyConstants.DeviceStatusAppList"/>.
/// <para>状态子类型，例如 <see cref="OhscrcpyConstants.DeviceStatusAppList"/>。</para></param>
/// <param name="Payload">Status body after the sub-type byte. <para>子类型字节之后的状态正文。</para></param>
public sealed record OhscrcpyDeviceMessage(byte SubType, byte[] Payload);

/// <summary>One application entry returned by the device application list.
/// <para>设备应用列表返回的一个应用条目。</para></summary>
/// <param name="Bundle">Bundle name. <para>包名。</para></param>
/// <param name="Label">Display label, possibly empty. <para>显示名，可能为空。</para></param>
public sealed record OhscrcpyAppEntry(string Bundle, string Label);

/// <summary>Logical display geometry reported by the device.
/// <para>设备上报的逻辑显示几何信息。</para></summary>
/// <param name="Width">Logical display width. <para>逻辑显示宽度。</para></param>
/// <param name="Height">Logical display height. <para>逻辑显示高度。</para></param>
/// <param name="Rotation">Rotation index 0 through 3. <para>旋转索引 0 至 3。</para></param>
public sealed record OhscrcpyDisplayInfo(int Width, int Height, int Rotation);

/// <summary>
/// Parsers and encoders for the ohscrcpy packet payloads.
/// <para>ohscrcpy 包负载的解析器与编码器。</para>
/// </summary>
public static class OhscrcpyPayload
{
    /// <summary>Builds a framed packet: type (4, big-endian), length (4, big-endian), payload.
    /// <para>构建分帧数据包：类型（4 字节大端）、长度（4 字节大端）、负载。</para></summary>
    /// <param name="type">Packet type constant. <para>包类型常量。</para></param>
    /// <param name="payload">Payload bytes, possibly empty. <para>负载字节，可为空。</para></param>
    /// <returns>The complete packet. <para>完整数据包。</para></returns>
    public static byte[] Frame(uint type, ReadOnlySpan<byte> payload)
    {
        var packet = new byte[OhscrcpyConstants.HeaderLength + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(packet, type);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(packet.AsSpan(OhscrcpyConstants.HeaderLength));
        return packet;
    }

    /// <summary>Parses a video-configuration payload.
    /// <para>解析视频配置负载。</para></summary>
    /// <param name="payload">Payload after the packet header. <para>包头之后的负载。</para></param>
    /// <returns>The parsed configuration. <para>已解析的配置。</para></returns>
    /// <exception cref="InvalidDataException">The payload is too short, announces a non-positive size, or
    /// carries SPS/PPS lengths that overrun the payload.
    /// <para>负载过短、宣告了非正尺寸，或 SPS/PPS 长度越出负载。</para></exception>
    public static OhscrcpyVideoConfig ParseVideoConfig(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 13)
        {
            throw new InvalidDataException(
                $"ohscrcpy video config is {payload.Length} bytes; at least 13 are required.");
        }

        var codec = payload[0];
        var width = ReadInt32(payload[1..]);
        var height = ReadInt32(payload[5..]);
        var frameRate = ReadInt32(payload[9..]);
        if (width is < 1 or > 16384 || height is < 1 or > 16384)
        {
            throw new InvalidDataException($"ohscrcpy announced invalid video size {width}x{height}.");
        }

        if (codec != OhscrcpyConstants.VideoCodecH264)
        {
            return new OhscrcpyVideoConfig(codec, width, height, frameRate, [], []);
        }

        if (payload.Length < 15)
        {
            throw new InvalidDataException("ohscrcpy H.264 video config carries no SPS length.");
        }

        var offset = 13;
        var spsLength = BinaryPrimitives.ReadUInt16BigEndian(payload[offset..]);
        offset += 2;
        if (offset + spsLength + 2 > payload.Length)
        {
            throw new InvalidDataException("ohscrcpy H.264 video config overruns with its SPS length.");
        }

        var sps = payload.Slice(offset, spsLength).ToArray();
        offset += spsLength;
        var ppsLength = BinaryPrimitives.ReadUInt16BigEndian(payload[offset..]);
        offset += 2;
        if (offset + ppsLength > payload.Length)
        {
            throw new InvalidDataException("ohscrcpy H.264 video config overruns with its PPS length.");
        }

        var pps = payload.Slice(offset, ppsLength).ToArray();
        return new OhscrcpyVideoConfig(codec, width, height, frameRate, sps, pps);
    }

    /// <summary>Parses a video-frame payload header: key-frame flag (1) and presentation timestamp (8).
    /// <para>解析视频帧负载头：关键帧标志（1 字节）与显示时间戳（8 字节）。</para></summary>
    /// <param name="payload">Payload after the packet header. <para>包头之后的负载。</para></param>
    /// <returns>The key-frame flag and the timestamp in microseconds. <para>关键帧标志与微秒时间戳。</para></returns>
    /// <exception cref="InvalidDataException">The payload is shorter than the nine-byte header.
    /// <para>负载短于九字节头部。</para></exception>
    public static (bool KeyFrame, long TimestampMicroseconds) ParseVideoFrameHeader(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 9)
        {
            throw new InvalidDataException(
                $"ohscrcpy video frame is {payload.Length} bytes; at least 9 are required.");
        }

        var keyFrame = (payload[0] & 0x01) != 0;
        var timestamp = (long)BinaryPrimitives.ReadUInt64BigEndian(payload[1..]);
        return (keyFrame, timestamp);
    }

    /// <summary>Parses a display-info status payload: width (4), height (4), rotation (1).
    /// <para>解析显示信息状态负载：宽（4）、高（4）、旋转（1）。</para></summary>
    /// <param name="payload">Status payload after the sub-type byte. <para>子类型字节之后的状态负载。</para></param>
    /// <returns>The display geometry, or <see langword="null"/> when the payload is malformed.
    /// <para>显示几何信息；负载格式不符时为 <see langword="null"/>。</para></returns>
    public static OhscrcpyDisplayInfo? ParseDisplayInfo(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 9) return null;
        var width = ReadInt32(payload);
        var height = ReadInt32(payload[4..]);
        var rotation = payload[8];
        if (width <= 0 || height <= 0 || rotation > 3) return null;
        return new OhscrcpyDisplayInfo(width, height, rotation);
    }

    /// <summary>Parses an application-list status payload: count (2) followed by length-prefixed bundle and label strings.
    /// <para>解析应用列表状态负载：数量（2 字节）后跟带长度前缀的包名与显示名。</para></summary>
    /// <param name="payload">Status payload after the sub-type byte. <para>子类型字节之后的状态负载。</para></param>
    /// <returns>The entries parsed so far; malformed tails stop the parse instead of throwing.
    /// <para>已解析的条目；格式不符的尾部会终止解析而不会抛出。</para></returns>
    /// <exception cref="InvalidDataException">The payload is shorter than its two-byte count.
    /// <para>负载短于其两字节计数。</para></exception>
    public static IReadOnlyList<OhscrcpyAppEntry> ParseAppList(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2)
        {
            throw new InvalidDataException("ohscrcpy application list carries no entry count.");
        }

        var count = BinaryPrimitives.ReadUInt16BigEndian(payload);
        var offset = 2;
        var entries = new List<OhscrcpyAppEntry>(count < 256 ? count : 256);
        for (var index = 0; index < count; index++)
        {
            if (!TryReadString(payload, ref offset, out var bundle)) break;
            if (!TryReadString(payload, ref offset, out var label)) break;
            entries.Add(new OhscrcpyAppEntry(bundle, label));
        }

        return entries;
    }

    private static bool TryReadString(ReadOnlySpan<byte> payload, ref int offset, out string value)
    {
        value = string.Empty;
        if (offset + 2 > payload.Length) return false;
        var length = BinaryPrimitives.ReadUInt16BigEndian(payload[offset..]);
        offset += 2;
        if (offset + length > payload.Length) return false;
        value = Encoding.UTF8.GetString(payload.Slice(offset, length));
        offset += length;
        return true;
    }

    private static int ReadInt32(ReadOnlySpan<byte> source)
        => (int)BinaryPrimitives.ReadUInt32BigEndian(source);
}

/// <summary>
/// Encodes ohscrcpy control messages (client → device direction).
/// <para>ohscrcpy 控制消息编码器（客户端 → 设备方向）。</para>
/// </summary>
/// <remarks>
/// <para>
/// A control message is a normal framed packet of type <see cref="OhscrcpyConstants.PacketTypeControl"/>
/// whose payload starts with a one-byte sub-type followed by a fixed-layout body. Touch coordinates are
/// video pixel coordinates: the device scales them from the announced capture size to the real display.
/// </para>
/// <para>
/// 控制消息是普通分帧数据包，类型为 <see cref="OhscrcpyConstants.PacketTypeControl"/>，负载以一个字节的
/// 子类型开头，后接定长布局的正文。触摸坐标是视频像素坐标：设备会按宣告的采集尺寸缩放到真实屏幕。
/// </para>
/// </remarks>
public static class OhscrcpyControlMessageWriter
{
    /// <summary>Upper bound for UTF-8 text sent as one <c>TEXT_INPUT</c> message.
    /// <para>单条 <c>TEXT_INPUT</c> 消息允许的 UTF-8 文本上限。</para></summary>
    public const int MaxTextLength = 4096;

    /// <summary>Builds a control packet from a sub-type and body.
    /// <para>由子类型与正文构建控制数据包。</para></summary>
    /// <param name="subType">Control sub-type constant. <para>控制子类型常量。</para></param>
    /// <param name="body">Body after the sub-type byte, possibly empty. <para>子类型字节之后的正文，可为空。</para></param>
    /// <returns>The complete framed packet. <para>完整的分帧数据包。</para></returns>
    public static byte[] Encode(byte subType, ReadOnlySpan<byte> body)
    {
        var payload = new byte[1 + body.Length];
        payload[0] = subType;
        body.CopyTo(payload.AsSpan(1));
        return OhscrcpyPayload.Frame(OhscrcpyConstants.PacketTypeControl, payload);
    }

    /// <summary>Builds a touch message: x (4), y (4), pointer id (2), all big-endian.
    /// <para>构建触摸消息：x（4）、y（4）、指针 id（2），均为大端。</para></summary>
    /// <param name="subType">One of the touch sub-types. <para>触摸子类型之一。</para></param>
    /// <param name="x">Video pixel X. <para>视频像素 X。</para></param>
    /// <param name="y">Video pixel Y. <para>视频像素 Y。</para></param>
    /// <param name="pointerId">Pointer identifier; the device reuses its down time per id.
    /// <para>指针标识；设备按 id 复用按下时间。</para></param>
    /// <returns>The complete framed packet. <para>完整的分帧数据包。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pointerId"/> is outside the unsigned
    /// 16-bit range. <para><paramref name="pointerId"/> 超出无符号 16 位范围。</para></exception>
    public static byte[] Touch(byte subType, int x, int y, int pointerId)
    {
        if (pointerId is < 0 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(pointerId));
        var body = new byte[10];
        BinaryPrimitives.WriteUInt32BigEndian(body, (uint)Math.Max(x, 0));
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(4), (uint)Math.Max(y, 0));
        BinaryPrimitives.WriteUInt16BigEndian(body.AsSpan(8), (ushort)pointerId);
        return Encode(subType, body);
    }

    /// <summary>Builds a key event message: pressed (1), OpenHarmony key code (4).
    /// <para>构建按键事件消息：按下（1）、OpenHarmony 键码（4）。</para></summary>
    /// <param name="pressed">Whether the key is going down. <para>按键是否按下。</para></param>
    /// <param name="keyCode">OpenHarmony key code. <para>OpenHarmony 键码。</para></param>
    /// <returns>The complete framed packet. <para>完整的分帧数据包。</para></returns>
    public static byte[] KeyEvent(bool pressed, int keyCode)
    {
        var body = new byte[5];
        body[0] = pressed ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(1), keyCode);
        return Encode(OhscrcpyConstants.ControlKeyEvent, body);
    }

    /// <summary>Builds a UTF-8 text-input message.
    /// <para>构建 UTF-8 文本输入消息。</para></summary>
    /// <param name="text">Text to inject. <para>要注入的文本。</para></param>
    /// <returns>The complete framed packet. <para>完整的分帧数据包。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.
    /// <para><paramref name="text"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="text"/> encodes to more than
    /// <see cref="MaxTextLength"/> bytes. <para><paramref name="text"/> 的 UTF-8 编码超过
    /// <see cref="MaxTextLength"/> 字节。</para></exception>
    public static byte[] Text(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > MaxTextLength)
        {
            throw new ArgumentException(
                $"Text input exceeds {MaxTextLength} UTF-8 bytes. 文本输入超过 {MaxTextLength} 字节。", nameof(text));
        }

        return Encode(OhscrcpyConstants.ControlTextInput, bytes);
    }

    /// <summary>Builds a mouse scroll message: action (1), button (1), x (4), y (4), axis value (4, float).
    /// <para>构建鼠标滚轮消息：动作（1）、按键（1）、x（4）、y（4）、轴值（4，浮点）。</para></summary>
    /// <param name="x">Video pixel X. <para>视频像素 X。</para></param>
    /// <param name="y">Video pixel Y. <para>视频像素 Y。</para></param>
    /// <param name="axisValue">Signed scroll amount; positive values scroll down.
    /// <para>带符号滚动量；正值向下滚动。</para></param>
    /// <returns>The complete framed packet. <para>完整的分帧数据包。</para></returns>
    public static byte[] Scroll(int x, int y, float axisValue)
    {
        var body = new byte[14];
        body[0] = OhscrcpyConstants.MouseActionAxisUpdate;
        body[1] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(2), (uint)Math.Max(x, 0));
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(6), (uint)Math.Max(y, 0));
        BinaryPrimitives.WriteSingleBigEndian(body.AsSpan(10), axisValue);
        return Encode(OhscrcpyConstants.ControlMouseEvent, body);
    }

    /// <summary>Builds an empty-body message for a sub-type that takes none.
    /// <para>为不需要正文的子类型构建空正文消息。</para></summary>
    /// <param name="subType">Control sub-type constant. <para>控制子类型常量。</para></param>
    /// <returns>The complete framed packet. <para>完整的分帧数据包。</para></returns>
    public static byte[] Simple(byte subType) => Encode(subType, []);

    /// <summary>Builds the video-parameter change message: max short edge (4), bitrate (4), frame rate (4).
    /// <para>构建视频参数变更消息：短边上限（4）、码率（4）、帧率（4）。</para></summary>
    /// <param name="maxShortEdge">Maximum length of the shorter capture edge, in pixels.
    /// <para>采集短边上限（像素）。</para></param>
    /// <param name="bitrate">Target bitrate in bits per second. <para>目标码率（比特/秒）。</para></param>
    /// <param name="frameRate">Target frame rate. <para>目标帧率。</para></param>
    /// <returns>The complete framed packet. <para>完整的分帧数据包。</para></returns>
    public static byte[] ChangeVideoParams(int maxShortEdge, int bitrate, int frameRate)
    {
        var body = new byte[12];
        BinaryPrimitives.WriteInt32BigEndian(body, maxShortEdge);
        BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(4), bitrate);
        BinaryPrimitives.WriteInt32BigEndian(body.AsSpan(8), frameRate);
        return Encode(OhscrcpyConstants.ControlChangeVideoParams, body);
    }
}