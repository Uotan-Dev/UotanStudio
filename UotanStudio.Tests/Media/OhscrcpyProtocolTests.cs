using System.Buffers.Binary;
using System.Text;
using UotanStudio.Mirroring;
using Xunit;

namespace UotanStudio.Tests.Media;

public sealed class OhscrcpyProtocolTests
{
    [Fact]
    public void BuildAnnexBConfiguration_PrefixesBothNalUnitsWithFourByteStartCodes()
    {
        var sps = new byte[] { 0x67, 0x01 };
        var pps = new byte[] { 0x68, 0x02 };

        var annexB = OhscrcpyProtocol.BuildAnnexBConfiguration(sps, pps);

        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x67, 0x01, 0, 0, 0, 1, 0x68, 0x02 }, annexB);
    }

    [Fact]
    public void TryGetParameterSets_ExtractsSpsAndPpsFromAnnexB()
    {
        var sps = BuildSps(widthInMacroblocks: 80, heightInMapUnits: 45, cropBottom: 0);
        var pps = new byte[] { 0x68, 0xCE, 0x3C, 0x80 };
        var idr = new byte[] { 0x65, 0x88, 0x84 };
        var accessUnit = AnnexB(sps, pps, idr);

        Assert.True(OhscrcpyProtocol.TryGetParameterSets(accessUnit, out var parsedSps, out var parsedPps));
        Assert.Equal(sps, parsedSps);
        Assert.Equal(pps, parsedPps);
    }

    [Fact]
    public void TryGetParameterSets_MissingPps_ReturnsFalse()
    {
        var accessUnit = AnnexB(BuildSps(80, 45, 0), new byte[] { 0x65, 0x88 });

        Assert.False(OhscrcpyProtocol.TryGetParameterSets(accessUnit, out var sps, out var pps));
        Assert.NotEmpty(sps);
        Assert.Empty(pps);
    }

    [Fact]
    public void IsKeyFrame_DetectsIdrAndIgnoresNonIdr()
    {
        var idr = AnnexB(new byte[] { 0x67, 0x42 }, new byte[] { 0x65, 0x88 });
        var nonIdr = AnnexB(new byte[] { 0x67, 0x42 }, new byte[] { 0x41, 0x9A });

        Assert.True(OhscrcpyProtocol.IsKeyFrame(idr));
        Assert.False(OhscrcpyProtocol.IsKeyFrame(nonIdr));
        Assert.False(OhscrcpyProtocol.IsKeyFrame([]));
    }

    [Fact]
    public void TryParseSpsDimensions_Baseline720p_Returns1280x720()
    {
        var sps = BuildSps(widthInMacroblocks: 80, heightInMapUnits: 45, cropBottom: 0);

        Assert.True(OhscrcpyProtocol.TryParseSpsDimensions(sps, out var width, out var height));
        Assert.Equal(1280, width);
        Assert.Equal(720, height);
    }

    [Fact]
    public void TryParseSpsDimensions_Cropped1080p_Returns1920x1080()
    {
        var sps = BuildSps(widthInMacroblocks: 120, heightInMapUnits: 68, cropBottom: 4);

        Assert.True(OhscrcpyProtocol.TryParseSpsDimensions(sps, out var width, out var height));
        Assert.Equal(1920, width);
        Assert.Equal(1080, height);
    }

    [Fact]
    public void TryParseSpsDimensions_Truncated_ReturnsFalse()
    {
        var sps = BuildSps(80, 45, 0);

        Assert.False(OhscrcpyProtocol.TryParseSpsDimensions(sps.AsSpan(0, 3), out _, out _));
        Assert.False(OhscrcpyProtocol.TryParseSpsDimensions(new byte[] { 0x67, 0xFF, 0xFF, 0xFF, 0xFF }, out _, out _));
    }

    [Fact]
    public void TryParseReply_ReadsDataReplyTypeAndVideoPayload()
    {
        var video = new byte[] { 0, 0, 0, 1, 0x65, 0x88 };
        var payload = BuildReply(video, "ok", 3);

        Assert.True(OhscrcpyGrpcProtocol.TryParseReply(payload, out var reply));
        Assert.NotNull(reply);
        Assert.Equal("ok", reply.Data);
        Assert.Equal(3, reply.ReplyType);
        Assert.Equal(video, reply.VideoPayload);
    }

    [Fact]
    public void TryParseReply_WithoutVideoPayload_ReturnsReply()
    {
        var payload = BuildReply(null, null, 0);

        Assert.True(OhscrcpyGrpcProtocol.TryParseReply(payload, out var reply));
        Assert.NotNull(reply);
        Assert.Null(reply!.VideoPayload);
    }

    [Fact]
    public void TryParseReply_TruncatedMapEntry_ReturnsFalse()
    {
        var payload = BuildReply([0x65, 0x88], null, 0);

        Assert.False(OhscrcpyGrpcProtocol.TryParseReply(payload.AsSpan(0, payload.Length - 2), out _));
    }

    [Fact]
    public void TryParseEndResult_ReadsResult()
    {
        Assert.True(OhscrcpyGrpcProtocol.TryParseEndResult(new byte[] { 0x08, 0x00 }, out var result));
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task ReadMessageAsync_RoundTripsFrame()
    {
        var expected = new byte[] { 1, 2, 3, 4, 5 };
        using var stream = new MemoryStream(BuildGrpcFrame(expected));

        var actual = await OhscrcpyGrpcProtocol.ReadMessageAsync(
            stream, OhscrcpyConstants.MaximumGrpcMessageLength, TestContext.Current.CancellationToken);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task ReadMessageAsync_CleanEnd_ReturnsNull()
    {
        using var stream = new MemoryStream();

        var actual = await OhscrcpyGrpcProtocol.ReadMessageAsync(
            stream, OhscrcpyConstants.MaximumGrpcMessageLength, TestContext.Current.CancellationToken);

        Assert.Null(actual);
    }

    [Fact]
    public async Task ReadMessageAsync_CompressedFlag_Throws()
    {
        var frame = BuildGrpcFrame([1, 2]);
        frame[0] = 1;
        using var stream = new MemoryStream(frame);

        await Assert.ThrowsAsync<InvalidDataException>(() => OhscrcpyGrpcProtocol.ReadMessageAsync(
            stream, OhscrcpyConstants.MaximumGrpcMessageLength, CancellationToken.None));
    }

    [Fact]
    public async Task ReadMessageAsync_OverlongMessage_Throws()
    {
        var frame = new byte[5];
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1), 1024);
        using var stream = new MemoryStream(frame);

        await Assert.ThrowsAsync<InvalidDataException>(() => OhscrcpyGrpcProtocol.ReadMessageAsync(
            stream, 16, TestContext.Current.CancellationToken));
    }

    private static byte[] AnnexB(params byte[][] nalUnits)
    {
        using var stream = new MemoryStream();
        foreach (var nal in nalUnits)
        {
            stream.Write([0, 0, 0, 1]);
            stream.Write(nal);
        }

        return stream.ToArray();
    }

    private static byte[] BuildGrpcFrame(byte[] payload)
    {
        var frame = new byte[OhscrcpyConstants.GrpcHeaderLength + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1), (uint)payload.Length);
        payload.CopyTo(frame, OhscrcpyConstants.GrpcHeaderLength);
        return frame;
    }

    private static byte[] BuildReply(byte[]? video, string? data, int replyType)
    {
        var output = new List<byte>();
        if (data is not null)
        {
            var text = Encoding.UTF8.GetBytes(data);
            output.Add(0x0A);
            WriteVarint(output, text.Length);
            output.AddRange(text);
        }

        if (replyType != 0)
        {
            output.Add(0x10);
            WriteVarint(output, replyType);
        }

        if (video is not null)
        {
            var entry = new List<byte>();
            entry.Add(0x0A);
            WriteVarint(entry, 4);
            entry.AddRange(Encoding.UTF8.GetBytes("data"));
            var value = new List<byte>();
            value.Add(0x2A);
            WriteVarint(value, video.Length);
            value.AddRange(video);
            entry.Add(0x12);
            WriteVarint(entry, value.Count);
            entry.AddRange(value);
            output.Add(0x1A);
            WriteVarint(output, entry.Count);
            output.AddRange(entry);
        }

        return [.. output];
    }

    private static void WriteVarint(List<byte> destination, int value)
    {
        var remaining = (uint)value;
        while (remaining >= 0x80)
        {
            destination.Add((byte)(remaining | 0x80));
            remaining >>= 7;
        }

        destination.Add((byte)remaining);
    }

    private static byte[] BuildSps(int widthInMacroblocks, int heightInMapUnits, int cropBottom)
    {
        var writer = new BitWriter();
        writer.WriteBits(66, 8); // profile_idc: baseline
        writer.WriteBits(0, 8);
        writer.WriteBits(31, 8); // level_idc
        writer.WriteUnsignedExpGolomb(0); // seq_parameter_set_id
        writer.WriteUnsignedExpGolomb(0); // log2_max_frame_num_minus4
        writer.WriteUnsignedExpGolomb(2); // pic_order_cnt_type
        writer.WriteUnsignedExpGolomb(1); // max_num_ref_frames
        writer.WriteBit(false); // gaps_in_frame_num_value_allowed_flag
        writer.WriteUnsignedExpGolomb(widthInMacroblocks - 1);
        writer.WriteUnsignedExpGolomb(heightInMapUnits - 1);
        writer.WriteBit(true); // frame_mbs_only_flag
        writer.WriteBit(true); // direct_8x8_inference_flag
        if (cropBottom > 0)
        {
            writer.WriteBit(true);
            writer.WriteUnsignedExpGolomb(0); // crop_left
            writer.WriteUnsignedExpGolomb(0); // crop_right
            writer.WriteUnsignedExpGolomb(0); // crop_top
            writer.WriteUnsignedExpGolomb(cropBottom);
        }
        else
        {
            writer.WriteBit(false);
        }

        writer.WriteBit(false); // vui_parameters_present_flag
        writer.WriteBit(true); // rbsp_stop_one_bit
        while (writer.BitCount % 8 != 0) writer.WriteBit(false);
        return [0x67, .. writer.ToArray()];
    }

    private sealed class BitWriter
    {
        private readonly List<bool> _bits = [];

        internal int BitCount => _bits.Count;

        internal void WriteBit(bool value) => _bits.Add(value);

        internal void WriteBits(int value, int count)
        {
            for (var index = count - 1; index >= 0; index--) _bits.Add(((value >> index) & 1) != 0);
        }

        internal void WriteUnsignedExpGolomb(int value)
        {
            var code = (uint)value + 1;
            var bitLength = 0;
            for (var remaining = code; remaining > 0; remaining >>= 1) bitLength++;
            for (var index = 0; index < bitLength - 1; index++) _bits.Add(false);
            for (var index = bitLength - 1; index >= 0; index--) _bits.Add(((code >> index) & 1) != 0);
        }

        internal byte[] ToArray()
        {
            var bytes = new byte[(_bits.Count + 7) / 8];
            for (var index = 0; index < _bits.Count; index++)
            {
                if (_bits[index]) bytes[index >> 3] |= (byte)(0x80 >> (index & 7));
            }

            return bytes;
        }
    }
}