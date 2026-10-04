using System.Runtime.InteropServices;
using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

/// <summary>
/// Sahara protocol handshake tests: HELLO/HELLO_RESPONSE packet layout, READ_DATA / READ_DATA64
/// image upload, DONE flow, Firehose-already-running detection and error paths — all scripted
/// through <see cref="ScriptedCommTransport"/> (no real hardware).
/// <para>Sahara 协议握手测试：HELLO/HELLO_RESPONSE 包布局、READ_DATA/READ_DATA64 镜像上传、
/// DONE 流程、Firehose 已在运行检测与错误路径——全部通过 <see cref="ScriptedCommTransport"/>
/// 脚本化模拟（无真实硬件）。</para>
/// </summary>
public partial class SaharaTests
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
        Assert.Equal(4096u, sahara.MaxPacketLength);
        Assert.False(sahara.FirehoseAlreadyRunning);

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
        EnqueueEndOfImage(transport, status: 1);

        var sahara = new SaharaProtocol(transport);
        var exception = await Assert.ThrowsAsync<SaharaException>(
            () => sahara.HandshakeAsync(ImagesWithFirehose(), TestContext.Current.CancellationToken));
        Assert.Contains("non-zero status", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandshakeAsync_EndOfImageRejected_SendsDoneBeforeThrowing()
    {
        var transport = new ScriptedCommTransport();
        EnqueueHello(transport);
        transport.Enqueue([0x04, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x07, 0x00, 0x00, 0x00]);

        var sahara = new SaharaProtocol(transport);
        var exception = await Assert.ThrowsAsync<SaharaException>(
            () => sahara.HandshakeAsync(ImagesWithFirehose(), TestContext.Current.CancellationToken));

        Assert.Equal("END_OF_IMAGE with non-zero status: 7 (invalid image received)", exception.Message);
        Assert.Equal(2, transport.Writes.Count);
        Assert.Equal([0x05, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00], transport.Writes[1]);
    }

    [Fact]
    public async Task HandshakeAsync_EndOfImageRejectedAndCleanupWriteRefused_ThrowsOriginalStatusError()
    {
        var transport = new ScriptedCommTransport
        {
            RejectWrite = (_, _, length) => length == 8
        };
        EnqueueHello(transport);
        transport.Enqueue([0x04, 0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00]);

        var sahara = new SaharaProtocol(transport);
        var exception = await Assert.ThrowsAsync<SaharaException>(
            () => sahara.HandshakeAsync(ImagesWithFirehose(), TestContext.Current.CancellationToken));

        Assert.Equal("END_OF_IMAGE with non-zero status: 2 (protocol mismatch between host and target)", exception.Message);
        Assert.Single(transport.Writes);
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
        transport.Enqueue(BuildHelloPacket(minVersion: 4, version: 4));

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
                return text.Contains("<nop", StringComparison.Ordinal)
                    ? [Encoding.UTF8.GetBytes("""<data><log value="booted"/></data><data><response value="ACK"/></data>""")]
                    : [];
            }
        };

        var sahara = new SaharaProtocol(transport);
        var mode = await sahara.HandshakeAsync(new Dictionary<uint, Stream>(), TestContext.Current.CancellationToken);

        Assert.Equal(SaharaMode.ImageTxComplete, mode);
        Assert.True(sahara.FirehoseAlreadyRunning);
        Assert.Single(transport.Writes);
        Assert.Contains("<nop value=\"ping\"", Encoding.UTF8.GetString(transport.Writes[0]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandshakeAsync_FirstReadTimeout_NoFirehose_Rethrows()
    {
        var transport = new ScriptedCommTransport();

        var sahara = new SaharaProtocol(transport);
        var exception = await Assert.ThrowsAsync<EdlException>(
            () => sahara.HandshakeAsync(new Dictionary<uint, Stream>(), TestContext.Current.CancellationToken));
        Assert.Equal(EdlErrorCode.ReadPortTimeout, exception.ErrorCode);
    }

    [Fact]
    public void HelloPacket_Layout_MatchesSpec()
    {
        Assert.Equal(48, Marshal.SizeOf<SaharaHelloPacket>());
        Assert.Equal(8, Marshal.SizeOf<SaharaPacketHeader>());
        Assert.Equal(20, Marshal.SizeOf<SaharaReadDataPacket>());
        Assert.Equal(32, Marshal.SizeOf<SaharaReadData64Packet>());
        Assert.Equal(16, Marshal.SizeOf<SaharaEndOfImagePacket>());
        Assert.Equal(12, Marshal.SizeOf<SaharaDoneResponsePacket>());
    }

    [Fact]
    public async Task HandshakeAsync_WriteDataCommand_ReportsVersion3Refusal()
    {
        var transport = new ScriptedCommTransport();
        EnqueueHello(transport);
        EnqueueRawCommand(transport, SaharaCommand.WriteData);

        var sahara = new SaharaProtocol(transport);
        var exception = await Assert.ThrowsAsync<SaharaException>(
            () => sahara.HandshakeAsync(ImagesWithFirehose(), TestContext.Current.CancellationToken));
        Assert.Contains("WRITE_DATA", exception.Message, StringComparison.Ordinal);
        Assert.Contains("3.0", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandshakeAsync_ResetStateCommand_ReportsRefusal()
    {
        var transport = new ScriptedCommTransport();
        EnqueueHello(transport);
        EnqueueRawCommand(transport, SaharaCommand.ResetState);

        var sahara = new SaharaProtocol(transport);
        var exception = await Assert.ThrowsAsync<SaharaException>(
            () => sahara.HandshakeAsync(ImagesWithFirehose(), TestContext.Current.CancellationToken));
        Assert.Contains("RESET_STATE", exception.Message, StringComparison.Ordinal);
    }

    private static void EnqueueRawCommand(ScriptedCommTransport transport, SaharaCommand command)
    {
        var packet = new SaharaPacketHeader
        {
            Command = command,
            Length = (uint)Marshal.SizeOf<SaharaPacketHeader>()
        };
        transport.Enqueue(StructToBytes(packet));
    }

    private static IReadOnlyDictionary<uint, Stream> ImagesWithFirehose(int size = 4096)
        => new Dictionary<uint, Stream> { [(uint)SaharaImageId.Firehose] = new MemoryStream(new byte[size]) };

    private static void EnqueueHello(ScriptedCommTransport transport, uint minVersion = 1, uint version = EdlConstants.SaharaVersion)
        => transport.Enqueue(BuildHelloPacket(minVersion, version));

    private static byte[] BuildHelloPacket(uint minVersion = 1, uint version = EdlConstants.SaharaVersion)
    {
        var hello = new SaharaHelloPacket
        {
            Command = SaharaCommand.Hello,
            Length = (uint)Marshal.SizeOf<SaharaHelloPacket>(),
            Version = version,
            MinVersion = minVersion,
            MaxCommandLength = 3,
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