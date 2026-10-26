using System.Buffers.Binary;

namespace UotanStudio.Mirroring.AirPlay;

/// <summary>
/// Turns one mirrored video packet into the access units the shared decode pipeline consumes: it strips the
/// 128-byte mirror header, splits the length-prefixed NAL units, republishes SPS/PPS as a scrcpy configuration
/// packet and reports the key-frame flag from the NAL types.
/// <para>把一个镜像视频包转换为共用解码管线消费的访问单元：去除 128 字节镜像头、切分带长度前缀的 NAL 单元、
/// 把 SPS/PPS 重新发布为 scrcpy 配置包，并根据 NAL 类型报告关键帧标志。</para>
/// </summary>
/// <remarks>
/// <para>
/// The output is identical in shape to the scrcpy and ohscrcpy paths, so the host keeps one decoder and one
/// presentation loop. The mirror header carries a 32.32 fixed-point timestamp that this demuxer converts to
/// microseconds; the flag bits are the scrcpy packet flags taken from <see cref="ScrcpyConstants"/>.
/// </para>
/// <para>
/// 输出形状与 scrcpy、ohscrcpy 路径一致，因此宿主只需一套解码与呈现循环。镜像头携带 32.32 定点时间戳，
/// 本解复用器把它换算为微秒；标志位使用 <see cref="ScrcpyConstants"/> 中的 scrcpy 包标志。
/// </para>
/// </remarks>
internal sealed class AirPlayMirrorDemuxer
{
    private const int NaluHeaderLength = 1;
    private const int SpsNaluType = 7;
    private const int PpsNaluType = 8;
    private const int IdrNaluType = 5;
    private static readonly byte[] StartCode = [0x00, 0x00, 0x00, 0x01];

    private byte[] _sps = [];
    private byte[] _pps = [];
    private byte[] _configuration = [];
    private long _lastTimestamp;

    /// <summary>Metadata parsed from the most recent SPS, or <see langword="null"/> until one arrives.
    /// <para>从最近一个 SPS 解析出的元数据；在收到之前为 <see langword="null"/>。</para>
    /// </summary>
    public ScrcpyVideoMetadata? Metadata { get; private set; }

    /// <summary>Decomposes one decrypted mirror packet into zero, one or two frames.
    /// <para>把一个已解密的镜像包分解为零个、一个或两个帧。</para>
    /// </summary>
    /// <param name="payload">One complete mirror packet: 128-byte header followed by length-prefixed NAL units.
    /// <para>一个完整的镜像包：128 字节头，后接带长度前缀的 NAL 单元。</para></param>
    /// <param name="frames">Receives a configuration frame when SPS/PPS changed, then the media frame.
    /// <para>接收 SPS/PPS 变化时的配置帧，随后是媒体帧。</para></param>
    /// <exception cref="InvalidDataException">The packet is shorter than its header, the announced length
    /// exceeds the packet, or a NAL unit length leaves its packet.
    /// <para>包短于其头部、宣告长度超出包范围，或某个 NAL 单元长度越出所在包。</para></exception>
    public void Demux(ReadOnlySpan<byte> payload, List<ScrcpyVideoFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (payload.Length < AirPlayConstants.MirrorHeaderLength)
        {
            throw new InvalidDataException("The mirror packet is shorter than its header.");
        }

        var available = payload.Length - AirPlayConstants.MirrorHeaderLength;
        var declared = BinaryPrimitives.ReadUInt32BigEndian(payload[AirPlayConstants.PayloadLengthOffset..]);
        int length;
        if (declared == 0)
        {
            length = available;
        }
        else if (declared > (uint)available)
        {
            throw new InvalidDataException(
                "The mirror packet announces a payload longer than the packet carries.");
        }
        else
        {
            length = (int)declared;
        }

        if (length > AirPlayConstants.MaximumAccessUnitBytes)
        {
            throw new InvalidDataException("The mirror packet exceeds the access unit bound.");
        }

        var body = payload.Slice(AirPlayConstants.MirrorHeaderLength, length);
        var nalus = SplitNalUnits(body);
        if (nalus.Count == 0)
        {
            return;
        }

        var hasIdr = false;
        var hasSps = false;
        var hasPps = false;
        foreach (var nalu in nalus)
        {
            var type = nalu[0] & 0x1F;
            switch (type)
            {
                case SpsNaluType:
                    hasSps = true;
                    break;
                case PpsNaluType:
                    hasPps = true;
                    break;
                case IdrNaluType:
                    hasIdr = true;
                    break;
            }
        }

        if (hasSps && hasPps)
        {
            PublishParameters(nalus, frames);
        }

        var accessUnit = BuildAnnexB(nalus);
        var timestamp = ReadTimestamp(payload);
        if (timestamp <= _lastTimestamp)
        {
            timestamp = _lastTimestamp + 1;
        }

        _lastTimestamp = timestamp;
        var pts = (long)((ulong)timestamp & ScrcpyConstants.PacketTimestampMask);
        if (hasIdr)
        {
            pts |= (long)ScrcpyConstants.PacketFlagKeyFrame;
        }

        frames.Add(new ScrcpyVideoFrame(pts, accessUnit));
    }

    private void PublishParameters(List<byte[]> nalus, List<ScrcpyVideoFrame> frames)
    {
        var sps = FindNalu(nalus, SpsNaluType);
        var pps = FindNalu(nalus, PpsNaluType);
        if (sps is null || pps is null)
        {
            return;
        }

        var configuration = BuildAnnexB([sps, pps]);
        if (configuration.AsSpan().SequenceEqual(_configuration))
        {
            return;
        }

        _sps = sps;
        _pps = pps;
        _configuration = configuration;
        if (OhscrcpyProtocol.TryParseSpsDimensions(sps, out var width, out var height))
        {
            Metadata = new ScrcpyVideoMetadata(AirPlayConstants.H264FourCc, width, height);
        }

        frames.Add(new ScrcpyVideoFrame((long)ScrcpyConstants.PacketFlagConfig, configuration));
        _logSink?.Invoke($"AirPlay video configuration: {Metadata?.Width}x{Metadata?.Height}.");
    }

    private Action<string>? _logSink;

    /// <summary>Attaches a diagnostic sink used for configuration announcements.
    /// <para>附加一个用于配置宣告的诊断接收端。</para>
    /// </summary>
    /// <param name="logSink">Sink invoked when a new configuration is published.
    /// <para>发布新配置时调用的接收端。</para></param>
    public void AttachLogSink(Action<string>? logSink) => _logSink = logSink;

    private static byte[]? FindNalu(List<byte[]> nalus, int type)
    {
        foreach (var nalu in nalus)
        {
            if ((nalu[0] & 0x1F) == type)
            {
                return nalu;
            }
        }

        return null;
    }

    private static List<byte[]> SplitNalUnits(ReadOnlySpan<byte> body)
    {
        var nalus = new List<byte[]>(4);
        var offset = 0;
        while (offset < body.Length)
        {
            if (offset + 4 > body.Length)
            {
                throw new InvalidDataException("A mirrored NAL unit length is truncated.");
            }

            var length = BinaryPrimitives.ReadUInt32BigEndian(body[offset..]);
            offset += 4;
            if (length == 0)
            {
                continue;
            }

            if (length > (uint)(body.Length - offset))
            {
                throw new InvalidDataException("A mirrored NAL unit exceeds its packet.");
            }

            if (length <= NaluHeaderLength)
            {
                throw new InvalidDataException("A mirrored NAL unit carries no payload.");
            }

            nalus.Add(body.Slice(offset, (int)length).ToArray());
            offset += (int)length;
            if (nalus.Count > AirPlayConstants.MaximumNaluCount)
            {
                throw new InvalidDataException("The mirror packet carries too many NAL units.");
            }
        }

        return nalus;
    }

    private static byte[] BuildAnnexB(List<byte[]> nalus)
    {
        var total = nalus.Count * StartCode.Length;
        foreach (var nalu in nalus)
        {
            total += nalu.Length;
        }

        var buffer = new byte[total];
        var offset = 0;
        foreach (var nalu in nalus)
        {
            StartCode.CopyTo(buffer, offset);
            offset += StartCode.Length;
            nalu.CopyTo(buffer, offset);
            offset += nalu.Length;
        }

        return buffer;
    }

    private static long ReadTimestamp(ReadOnlySpan<byte> payload)
    {
        var raw = BinaryPrimitives.ReadUInt64BigEndian(payload[AirPlayConstants.TimestampOffset..]);
        var seconds = raw >> 32;
        var fraction = raw & 0xFFFF_FFFFUL;
        var microseconds = seconds * 1_000_000UL + ((fraction * 1_000_000UL) >> 32);
        return (long)microseconds;
    }
}