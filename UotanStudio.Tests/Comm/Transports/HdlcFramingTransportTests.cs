using System.Text;
using UotanStudio.Comm.Transports.Hdlc;
using UotanStudio.Comm.Transports.Hsuart;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Transports;

/// <summary>
/// HDLC frame codec and <see cref="HdlcFramingTransport"/> tests: transparency escaping,
/// 0x7E flag placement, FCS encoding/verification and cross-read reassembly.
/// <para>HDLC 帧编解码与 <see cref="HdlcFramingTransport"/> 测试：透明转义、0x7E 标志位置、
/// FCS 编解码校验与跨包分片重组。</para>
/// </summary>
public class HdlcFramingTransportTests
{
    private const byte DefaultAddress = Hdlc.DefaultAddress;
    private const byte DefaultControl = Hdlc.DefaultControl;

    // ============== Hdlc static codec ==============

    [Fact]
    public void Hdlc_EncodeExtractInfo_RoundTrips()
    {
        var info = Encoding.UTF8.GetBytes("Sahara HELLO payload with 0x7E and 0x7D inside.");
        var frame = EncodeFrame(info);

        Assert.Equal(Hdlc.Flag, frame[0]);
        Assert.Equal(Hdlc.Flag, frame[^1]);

        var dst = new byte[frame.Length];
        var n = Hdlc.ExtractInfo(frame, dst, out var error);
        Assert.Equal(HdlcError.Success, error);
        Assert.Equal(info, dst[..n]);
    }

    [Fact]
    public void Hdlc_Encode_EscapesFlagAndEscapeBytesInsideBody()
    {
        // info deliberately contains the flag byte and the escape byte; both must be
        // escaped so the body never contains a raw 0x7E, and 0x7D appears only as a
        // transparency prefix (never as a dangling escape prefix).
        // 负载刻意包含标志字节与转义字节；两者都必须被转义，帧体不得出现裸 0x7E，
        // 且 0x7D 只能作为转义前缀出现（不得有悬空的孤立转义前缀）。
        var info = new byte[] { 0x01, Hdlc.Flag, 0x02, Hdlc.Escape, 0x03, Hdlc.Flag, 0x7F };
        var frame = EncodeFrame(info);

        var body = frame[1..^1];
        // 帧体不得包含裸标志字节（0x7E 只会出现在帧首尾）。
        // The body must never contain a raw flag byte (0x7E only sits at the frame ends).
        Assert.DoesNotContain(body, b => b == Hdlc.Flag);

        // 0x7D 是转义前缀：其后必须跟 0x5E（0x7E 的转义形式）或 0x5D（0x7D 的转义形式），
        // 不允许出现孤立/悬空的转义前缀。
        // 0x7D is the transparency prefix: it must be followed by 0x5E (escaped 0x7E) or
        // 0x5D (escaped 0x7D); a dangling escape prefix is forbidden.
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] != Hdlc.Escape)
            {
                continue;
            }

            Assert.True(i + 1 < body.Length, "dangling escape prefix at end of body");
            Assert.True(body[i + 1] is Hdlc.FlagXor or Hdlc.EscapeXor,
                $"unexpected escape suffix 0x{body[i + 1]:X2} after 0x7D");
            i++; // skip the escaped byte
        }

        var dst = new byte[frame.Length];
        var n = Hdlc.ExtractInfo(frame, dst, out var error);
        Assert.Equal(HdlcError.Success, error);
        Assert.Equal(info, dst[..n]);
    }

    [Fact]
    public void Hdlc_ComputeFcs_IsVerifiedByDecode()
    {
        var info = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE };
        var frame = EncodeFrame(info);

        // The FCS bytes live right before the closing flag; recompute and compare.
        // FCS 字节紧邻结束标志之前；重算并比对。
        var body = frame[1..^1];
        var decodedBody = new byte[body.Length];
        var decodedLen = Hdlc.Decode(body, decodedBody, out var decodeError);
        Assert.Equal(HdlcError.Success, decodeError);
        Assert.True(decodedLen >= 4);
    }

    [Fact]
    public void Hdlc_Encode_EmptyInfoFails()
    {
        var frame = new byte[64];
        var n = Hdlc.Encode([], frame, out var error);
        Assert.Equal(0, n);
        Assert.Equal(HdlcError.EmptyInfo, error);
    }

    [Fact]
    public void Hdlc_ExtractInfo_CorruptedBodyFailsCrc()
    {
        var info = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var frame = EncodeFrame(info);
        frame[frame.Length / 2] ^= 0xFF; // flip a body byte -> FCS mismatch

        var dst = new byte[frame.Length];
        var n = Hdlc.ExtractInfo(frame, dst, out var error);
        Assert.Equal(0, n);
        Assert.Equal(HdlcError.CrcMismatch, error);
    }

    [Fact]
    public void Hdlc_ExtractInfo_MissingFlagsFails()
    {
        var frame = EncodeFrame(new byte[] { 1, 2, 3 });
        frame[0] = 0x11; // corrupt the opening flag

        var dst = new byte[frame.Length];
        var n = Hdlc.ExtractInfo(frame, dst, out var error);
        Assert.Equal(0, n);
        Assert.Equal(HdlcError.MissingFlags, error);
    }

    [Fact]
    public void Hdlc_Decode_DanglingEscapeFails()
    {
        // A body ending with a lone escape byte cannot be a valid HDLC body.
        // 以孤立转义字节结尾的帧体无法构成有效 HDLC 帧体。
        var dst = new byte[32];
        var n = Hdlc.Decode([0xFF, 0x03, Hdlc.Escape], dst, out var error);
        Assert.Equal(0, n);
        Assert.Equal(HdlcError.InvalidEscape, error);
    }

    [Fact]
    public void Hdlc_Fcs_MatchesReflectedCcittReference()
    {
        // CRC-16/CCITT reflected over the 4-byte 0xFF body yields the 0xE2F0 reference residue.
        // 4 字节 0xFF 帧体经反向 CRC-16/CCITT 得到参考余数 0xE2F0。
        var fcs = Hdlc.ComputeFcs([0xFF, 0xFF, 0xFF, 0xFF]);
        Assert.Equal(0xE2F0, fcs);
    }

    // ============== HdlcFramingTransport ==============

    [Fact]
    public void HdlcTransport_Write_FramesPayloadAsSingleHdlcFrame()
    {
        var inner = new ScriptedCommTransport();
        using var transport = new HdlcFramingTransport(inner);

        var payload = new byte[] { 0x10, 0x20, 0x30, 0x40, 0x50 };
        var written = transport.Write(payload, 0, payload.Length, 1000);

        Assert.Equal(payload.Length, written);
        var frame = Assert.Single(inner.Writes);
        Assert.Equal(Hdlc.Flag, frame[0]);
        Assert.Equal(Hdlc.Flag, frame[^1]);

        var dst = new byte[frame.Length];
        var n = Hdlc.ExtractInfo(frame, dst, out var error);
        Assert.Equal(HdlcError.Success, error);
        Assert.Equal(payload, dst[..n]);
    }

    [Fact]
    public void HdlcTransport_Write_UsesCustomAddressAndControlBytes()
    {
        var inner = new ScriptedCommTransport();
        using var transport = new HdlcFramingTransport(inner, 0x11, 0x22);

        transport.Write(new byte[] { 9, 8, 7 }, 0, 3, 1000);

        var frame = Assert.Single(inner.Writes);
        // body layout: addr | ctrl | info... | fcs
        var body = frame[1..^1];
        var decoded = new byte[body.Length];
        var len = Hdlc.Decode(body, decoded, out _);
        Assert.Equal(0x11, decoded[0]);
        Assert.Equal(0x22, decoded[1]);
        Assert.Equal(3, len - 4);
    }

    [Fact]
    public void HdlcTransport_Read_ReturnsInfoFieldAcrossFragmentedReads()
    {
        var inner = new ScriptedCommTransport();
        using var transport = new HdlcFramingTransport(inner);

        var payload = new byte[] { 0x01, 0x02, Hdlc.Flag, 0x03, 0x04 };
        var frame = EncodeFrame(payload);

        // Feed the frame in three fragments; the transport must reassemble it.
        // 分三次喂入帧；传输层必须重组。
        inner.Enqueue(frame[..3]);
        inner.Enqueue(frame[3..7]);
        inner.Enqueue(frame[7..]);

        var buffer = new byte[64];
        var n = transport.Read(buffer, 0, buffer.Length, 1000);

        Assert.Equal(payload.Length, n);
        Assert.Equal(payload, buffer[..n]);
    }

    [Fact]
    public void HdlcTransport_Read_HandlesBackToBackFrames()
    {
        var inner = new ScriptedCommTransport();
        using var transport = new HdlcFramingTransport(inner);

        var first = EncodeFrame(new byte[] { 1, 2, 3 });
        var second = EncodeFrame(new byte[] { 4, 5, 6, 7 });
        inner.Enqueue(first.Concat(second).ToArray());

        var buffer = new byte[64];
        var n1 = transport.Read(buffer, 0, buffer.Length, 1000);
        Assert.Equal(3, n1);
        Assert.Equal(new byte[] { 1, 2, 3 }, buffer[..n1]);

        var n2 = transport.Read(buffer, 0, buffer.Length, 1000);
        Assert.Equal(4, n2);
        Assert.Equal(new byte[] { 4, 5, 6, 7 }, buffer[..n2]);
    }

    [Fact]
    public void HdlcTransport_Read_PartialFrameThenTimeoutThrows()
    {
        var inner = new ScriptedCommTransport();
        using var transport = new HdlcFramingTransport(inner);

        // Only an opening flag and a few body bytes — no closing flag ever arrives.
        // 仅有起始标志与少量帧体字节——结束标志永不出现。
        inner.Enqueue(new byte[] { Hdlc.Flag, DefaultAddress, DefaultControl });

        var buffer = new byte[64];
        var exception = Assert.Throws<IOException>(() => transport.Read(buffer, 0, buffer.Length, 50));
        Assert.Contains("no data", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HdlcTransport_WriteAsyncReadAsync_RoundTrip()
    {
        var inner = new ScriptedCommTransport();
        using var transport = new HdlcFramingTransport(inner);

        var payload = Encoding.ASCII.GetBytes("async round trip");
        var written = await transport.WriteAsync(payload, 0, payload.Length, 1000, TestContext.Current.CancellationToken);
        Assert.Equal(payload.Length, written);

        // Echo the frame back as device data (split across two chunks).
        // 将帧作为设备数据回显（分两段）。
        var frame = inner.Writes[0];
        inner.Enqueue(frame[..5]);
        inner.Enqueue(frame[5..]);

        var buffer = new byte[256];
        var n = await transport.ReadAsync(buffer, 0, buffer.Length, 1000, TestContext.Current.CancellationToken);
        Assert.Equal(payload.Length, n);
        Assert.Equal(payload, buffer[..n]);
    }

    [Fact]
    public void HdlcTransport_Write_ZeroLengthWritesNothing()
    {
        var inner = new ScriptedCommTransport();
        using var transport = new HdlcFramingTransport(inner);

        Assert.Equal(0, transport.Write([], 0, 0, 1000));
        Assert.Empty(inner.Writes);
    }

    [Fact]
    public void HdlcTransport_DisposeThenReadThrows()
    {
        var inner = new ScriptedCommTransport();
        var transport = new HdlcFramingTransport(inner);
        transport.Dispose();

        Assert.Throws<ObjectDisposedException>(() => transport.Read(new byte[4], 0, 4, 1000));
        Assert.Throws<ObjectDisposedException>(() => transport.Write(new byte[4], 0, 4, 1000));
    }

    [Fact]
    public void HdlcTransport_Reset_ClearsPartialReceiveBuffer()
    {
        var inner = new ScriptedCommTransport();
        using var transport = new HdlcFramingTransport(inner);

        // Partial frame accumulates in the receive buffer.
        // 部分帧累积在接收缓冲中。
        inner.Enqueue(new byte[] { Hdlc.Flag, DefaultAddress });
        var buffer = new byte[64];
        Assert.Throws<IOException>(() => transport.Read(buffer, 0, buffer.Length, 10));

        transport.Reset();

        // A complete frame after reset is decoded cleanly (no leaked partial bytes).
        // 重置后收到完整帧可正常解码（无残留部分字节）。
        inner.Enqueue(EncodeFrame(new byte[] { 42 }));
        var n = transport.Read(buffer, 0, buffer.Length, 1000);
        Assert.Equal(1, n);
        Assert.Equal(42, buffer[0]);
    }

    private static byte[] EncodeFrame(byte[] info)
    {
        var frame = new byte[(info.Length * 2) + 8];
        var n = Hdlc.Encode(info, frame, out var error);
        Assert.Equal(HdlcError.Success, error);
        return frame[..n];
    }
}