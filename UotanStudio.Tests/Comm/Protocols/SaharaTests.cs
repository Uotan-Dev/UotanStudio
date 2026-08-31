using System.Runtime.InteropServices;
using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols;

/// <summary>
/// Sahara protocol handshake tests: HELLO/HELLO_RESPONSE packet layout, READ_DATA / READ_DATA64
/// image upload, DONE flow, Firehose-already-running detection and error paths — all scripted
/// through <see cref="ScriptedCommTransport"/> (no real hardware).
/// <para>Sahara 协议握手测试：HELLO/HELLO_RESPONSE 包布局、READ_DATA/READ_DATA64 镜像上传、
/// DONE 流程、Firehose 已在运行检测与错误路径——全部通过 <see cref="ScriptedCommTransport"/>
/// 脚本化模拟（无真实硬件）。</para>
/// </summary>
public class SaharaTests
{
    [Fact]
    public async Task HandshakeAsync_CompletesFullFlowWithHelloAndDone()
    {
        var transport = new ScriptedCommTransport();
        EnqueueHello(transport);
        EnqueueEndOfImage(transport, status: 0);
        EnqueueDoneResponse(transport, status: 1);
        EnqueueCommandReady(transport);

        var sahara = new SaharaProtocol(transport);
        var mode = await sahara.HandshakeAsync(ImagesWithFirehose(), TestContext.Current.CancellationToken);

        Assert.Equal(SaharaMode.ImageTxPending, mode);
        Assert.Equal(EdlConstants.DefaultMaxPacketLength, sahara.MaxPacketLength);
        Assert.False(sahara.FirehoseAlreadyRunning);

        // [0] HELLO_RESPONSE, [1] DONE.
        var helloResponse = ParseHeader(transport.Writes[0]);
        Assert.Equal(SaharaCommand.HelloResponse, helloResponse.Command);
        Assert.Equal((uint)Marshal.SizeOf<SaharaHelloResponsePacket>(), helloResponse.Length);

        var done = ParseHeader(transport.Writes[1]);
        Assert.Equal(SaharaCommand.Done, done.Command);
    }

    [Fact]
    public async Task HandshakeAsync_ReadDataUploadsImageBytes()
    {
        var image = new byte[8192];
        for (var i = 0; i < image.Length; i++)
        {
            image[i] = (byte)(i * 31);
        }

        var transport = new ScriptedCommTransport();
        EnqueueHello(transport);
        EnqueueReadData(transport, imageId: (uint)SaharaImageId.Firehose, offset: 0, length: (uint)image.Length);
        EnqueueEndOfImage(transport, status: 0);
        EnqueueDoneResponse(transport, status: 1);
        EnqueueCommandReady(transport);

        var sahara = new SaharaProtocol(transport);
        using var imageStream = new MemoryStream(image);
        var mode = await sahara.HandshakeAsync(
            new Dictionary<uint, Stream> { [(uint)SaharaImageId.Firehose] = imageStream },
            TestContext.Current.CancellationToken);

        Assert.Equal(SaharaMode.ImageTxPending, mode);

        // Writes: [0] HELLO_RESPONSE, [1] image payload, [2] DONE.
        Assert.Equal(3, transport.Writes.Count);
        Assert.Equal(image, transport.Writes[1]);
        Assert.Equal(SaharaCommand.Done, ParseHeader(transport.Writes[2]).Command);
    }

    [Fact]
    public async Task HandshakeAsync_ReadData64UploadsLargeImage()
    {
        var image = new byte[128 * 1024];
        Random.Shared.NextBytes(image);

        var transport = new ScriptedCommTransport();
        EnqueueHello(transport);
        EnqueueReadData64(transport, imageId: (uint)SaharaImageId.Firehose, offset: 0, length: (ulong)image.Length);
        EnqueueEndOfImage(transport, status: 0);
        EnqueueDoneResponse(transport, status: 1);
        EnqueueCommandReady(transport);

        var sahara = new SaharaProtocol(transport);
        using var imageStream = new MemoryStream(image);
        var mode = await sahara.HandshakeAsync(
            new Dictionary<uint, Stream> { [(uint)SaharaImageId.Firehose] = imageStream },
            TestContext.Current.CancellationToken);

        Assert.Equal(SaharaMode.ImageTxPending, mode);
        // 128KB 镜像大于 64KB 传输缓冲，被拆为两次写入：
        // [0] HELLO_RESPONSE, [1..2] 分块 payload, [3] DONE。
        // A 128KB image exceeds the 64KB transfer buffer, so it is written in two chunks:
        // [0] HELLO_RESPONSE, [1..2] chunked payload, [3] DONE.
        Assert.Equal(4, transport.Writes.Count);
        var payload = transport.Writes[1].Concat(transport.Writes[2]).ToArray();
        Assert.Equal(image, payload);
        Assert.Equal(SaharaCommand.Done, ParseHeader(transport.Writes[3]).Command);
    }

    [Fact]
    public async Task HandshakeAsync_NoImagesProvided_Throws()
    {
        var transport = new ScriptedCommTransport();
        EnqueueHello(transport);

        var sahara = new SaharaProtocol(transport);
        var exception = await Assert.ThrowsAsync<SaharaException>(
            () => sahara.HandshakeAsync(new Dictionary<uint, Stream>(), TestContext.Current.CancellationToken));
        Assert.Contains("no programmer image", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task HandshakeAsync_DetectsFirehoseAlreadyRunningFromXmlFirstData()
    {
        var transport = new ScriptedCommTransport();
        var xml = Encoding.ASCII.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\" ?>");
        Array.Resize(ref xml, 48);
        for (var i = 0; i < xml.Length; i++)
        {
            if (xml[i] == 0)
            {
                xml[i] = (byte)' ';
            }
        }

        transport.Enqueue(xml);

        var sahara = new SaharaProtocol(transport);
        var mode = await sahara.HandshakeAsync(new Dictionary<uint, Stream>(), TestContext.Current.CancellationToken);

        Assert.Equal(SaharaMode.ImageTxComplete, mode);
        Assert.True(sahara.FirehoseAlreadyRunning);
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task HandshakeAsync_UnknownImageId_Throws()
    {
        var transport = new ScriptedCommTransport();
        EnqueueHello(transport);
        EnqueueReadData(transport, imageId: 0x55, offset: 0, length: 16);

        var sahara = new SaharaProtocol(transport);
        using var imageStream = new MemoryStream(new byte[16]);
        var exception = await Assert.ThrowsAsync<SaharaException>(() => sahara.HandshakeAsync(
            new Dictionary<uint, Stream> { [(uint)SaharaImageId.Firehose] = imageStream },
            TestContext.Current.CancellationToken));

        Assert.Contains("Unknown image id", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandshakeAsync_EndOfImageNonZeroStatus_Throws()
    {
        var transport = new ScriptedCommTransport();
        EnqueueHello(transport);
        EnqueueEndOfImage(transport, status: 1); // device rejected the image

        var sahara = new SaharaProtocol(transport);
        var exception = await Assert.ThrowsAsync<SaharaException>(
            () => sahara.HandshakeAsync(ImagesWithFirehose(), TestContext.Current.CancellationToken));
        Assert.Contains("non-zero status", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandshakeAsync_ReadDataOutOfRange_Throws()
    {
        var transport = new ScriptedCommTransport();
        EnqueueHello(transport);
        EnqueueReadData(transport, imageId: (uint)SaharaImageId.Firehose, offset: 4096, length: 8192);

        var sahara = new SaharaProtocol(transport);
        using var imageStream = new MemoryStream(new byte[4096]);
        var exception = await Assert.ThrowsAsync<SaharaException>(() => sahara.HandshakeAsync(
            new Dictionary<uint, Stream> { [(uint)SaharaImageId.Firehose] = imageStream },
            TestContext.Current.CancellationToken));

        Assert.Contains("invalid range", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandshakeAsync_MinVersionTooHigh_Throws()
    {
        var transport = new ScriptedCommTransport();
        transport.Enqueue(BuildHelloPacket(minVersion: 3));

        var sahara = new SaharaProtocol(transport);
        var exception = await Assert.ThrowsAsync<SaharaException>(
            () => sahara.HandshakeAsync(ImagesWithFirehose(), TestContext.Current.CancellationToken));
        Assert.Contains("min protocol version", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandshakeAsync_FirstReadTimeout_ProbesFirehoseAndDetectsIt()
    {
        var transport = new ScriptedCommTransport
        {
            OnWrite = bytes =>
            {
                var text = Encoding.UTF8.GetString(bytes);
                // When the probe <nop> is sent, answer like a running Firehose programmer.
                // 当探测 <nop> 发出时，模拟运行中的 Firehose programmer 应答。
                return text.Contains("<nop", StringComparison.Ordinal)
                    ? [Encoding.UTF8.GetBytes("""<data><log value="booted"/></data><data><response value="ACK"/></data>""")]
                    : [];
            }
        };

        var sahara = new SaharaProtocol(transport);
        var mode = await sahara.HandshakeAsync(new Dictionary<uint, Stream>(), TestContext.Current.CancellationToken);

        Assert.Equal(SaharaMode.ImageTxComplete, mode);
        Assert.True(sahara.FirehoseAlreadyRunning);
        // The probe nop XML was written, then no further handshake packets.
        // 写入了探测 nop XML，此后无更多握手包。
        Assert.Single(transport.Writes);
        Assert.Contains("<nop value=\"ping\"", Encoding.UTF8.GetString(transport.Writes[0]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandshakeAsync_FirstReadTimeout_NoFirehose_Rethrows()
    {
        var transport = new ScriptedCommTransport(); // empty: no HELLO, no nop response

        var sahara = new SaharaProtocol(transport);
        var exception = await Assert.ThrowsAsync<EdlException>(
            () => sahara.HandshakeAsync(new Dictionary<uint, Stream>(), TestContext.Current.CancellationToken));
        Assert.Equal(EdlErrorCode.ReadPortTimeout, exception.ErrorCode);
    }

    [Fact]
    public void HelloPacket_Layout_MatchesSpec()
    {
        // The HELLO packet is 48 bytes: header + version fields + 6 reserved uints.
        // HELLO 包为 48 字节：包头 + 版本字段 + 6 个保留 uint。
        Assert.Equal(48, Marshal.SizeOf<SaharaHelloPacket>());
        Assert.Equal(8, Marshal.SizeOf<SaharaPacketHeader>());
        Assert.Equal(20, Marshal.SizeOf<SaharaReadDataPacket>());
        Assert.Equal(32, Marshal.SizeOf<SaharaReadData64Packet>());
        Assert.Equal(16, Marshal.SizeOf<SaharaEndOfImagePacket>());
        Assert.Equal(12, Marshal.SizeOf<SaharaDoneResponsePacket>());
    }

    // ============== scripted device helpers ==============

    private static IReadOnlyDictionary<uint, Stream> ImagesWithFirehose(int size = 4096)
        => new Dictionary<uint, Stream> { [(uint)SaharaImageId.Firehose] = new MemoryStream(new byte[size]) };

    private static void EnqueueHello(ScriptedCommTransport transport, uint minVersion = 1)
        => transport.Enqueue(BuildHelloPacket(minVersion));

    private static byte[] BuildHelloPacket(uint minVersion = 1)
    {
        var hello = new SaharaHelloPacket
        {
            Command = SaharaCommand.Hello,
            Length = (uint)Marshal.SizeOf<SaharaHelloPacket>(),
            Version = EdlConstants.SaharaVersion,
            MinVersion = minVersion,
            MaxPacketLength = EdlConstants.DefaultMaxPacketLength,
            Mode = SaharaMode.ImageTxPending
        };
        return StructToBytes(hello);
    }

    private static void EnqueueReadData(ScriptedCommTransport transport, uint imageId, uint offset, uint length)
    {
        var packet = new SaharaReadDataPacket
        {
            Command = SaharaCommand.ReadData,
            Length = (uint)Marshal.SizeOf<SaharaReadDataPacket>(),
            ImageId = imageId,
            DataOffset = offset,
            DataLength = length
        };
        transport.Enqueue(StructToBytes(packet));
    }

    private static void EnqueueReadData64(ScriptedCommTransport transport, ulong imageId, ulong offset, ulong length)
    {
        var packet = new SaharaReadData64Packet
        {
            Command = SaharaCommand.ReadData64,
            Length = (uint)Marshal.SizeOf<SaharaReadData64Packet>(),
            ImageId = imageId,
            DataOffset = offset,
            DataLength = length
        };
        transport.Enqueue(StructToBytes(packet));
    }

    private static void EnqueueEndOfImage(ScriptedCommTransport transport, uint status)
    {
        var packet = new SaharaEndOfImagePacket
        {
            Command = SaharaCommand.EndOfImage,
            Length = (uint)Marshal.SizeOf<SaharaEndOfImagePacket>(),
            ImageId = (uint)SaharaImageId.Firehose,
            Status = status
        };
        transport.Enqueue(StructToBytes(packet));
    }

    private static void EnqueueDoneResponse(ScriptedCommTransport transport, uint status)
    {
        var packet = new SaharaDoneResponsePacket
        {
            Command = SaharaCommand.DoneResponse,
            Length = (uint)Marshal.SizeOf<SaharaDoneResponsePacket>(),
            Status = status
        };
        transport.Enqueue(StructToBytes(packet));
    }

    private static void EnqueueCommandReady(ScriptedCommTransport transport)
    {
        var packet = new SaharaPacketHeader
        {
            Command = SaharaCommand.CommandReady,
            Length = (uint)Marshal.SizeOf<SaharaPacketHeader>()
        };
        transport.Enqueue(StructToBytes(packet));
    }

    private static SaharaPacketHeader ParseHeader(byte[] data)
        => MemoryMarshal.Read<SaharaPacketHeader>(data);

    private static byte[] StructToBytes<T>(T value) where T : struct
    {
        var bytes = new byte[Marshal.SizeOf<T>()];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            Marshal.StructureToPtr(value, handle.AddrOfPinnedObject(), fDeleteOld: false);
        }
        finally
        {
            handle.Free();
        }

        return bytes;
    }
}
