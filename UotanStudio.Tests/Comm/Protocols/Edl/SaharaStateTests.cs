using System.Buffers.Binary;
using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

public partial class SaharaTests
{
    [Fact]
    public async Task HandshakeAsync_PendingImage_ContinuesUntilFinalCompletion()
    {
        using var transport = new ScriptedCommTransport();
        EnqueueHello(transport);
        EnqueueEndOfImage(transport, 0);
        EnqueueDoneResponse(transport, 0);
        EnqueueHello(transport);
        EnqueueReadData(transport, 14, 0, 4);
        transport.Enqueue(StructToBytes(new SaharaEndOfImagePacket
        {
            Command = SaharaCommand.EndOfImage,
            Length = 16,
            ImageId = 14
        }));
        EnqueueDoneResponse(transport, 1);
        using var first = new MemoryStream([1, 2, 3, 4]);
        using var second = new MemoryStream([5, 6, 7, 8]);
        var protocol = new SaharaProtocol(transport);
        var completions = 0;
        protocol.HandshakeComplete += (_, _) => completions++;

        await protocol.HandshakeAsync(new Dictionary<uint, Stream> { [13] = first, [14] = second },
            TestContext.Current.CancellationToken);

        Assert.Equal(4, second.Position);
        Assert.Equal(1, completions);
        Assert.Equal(5, transport.Writes.Count);
        Assert.Equal(SaharaCommand.HelloResponse, ParseHeader(transport.Writes[2]).Command);
        Assert.Equal(new byte[] { 5, 6, 7, 8 }, transport.Writes[3]);
    }

    [Fact]
    public async Task HandshakeAsync_RepeatedHello_ConsumesEntirePacketAndAcknowledges()
    {
        using var transport = new ScriptedCommTransport();
        EnqueueHello(transport);
        EnqueueHello(transport);
        EnqueueEndOfImage(transport, 0);
        EnqueueDoneResponse(transport, 1);
        var protocol = new SaharaProtocol(transport);

        await protocol.HandshakeAsync(ImagesWithFirehose(), TestContext.Current.CancellationToken);

        Assert.Equal(new[] { SaharaCommand.HelloResponse, SaharaCommand.HelloResponse, SaharaCommand.Done },
            transport.Writes.Select(bytes => ParseHeader(bytes).Command));
    }

    [Theory]
    [InlineData(9u)]
    [InlineData(14u)]
    [InlineData(8u)]
    public async Task HandshakeAsync_UnsupportedImageCommand_RejectsWithoutSuccess(uint command)
    {
        using var transport = new ScriptedCommTransport();
        EnqueueHello(transport);
        transport.Enqueue(StructToBytes(new SaharaPacketHeader { Command = (SaharaCommand)command, Length = 8 }));
        var protocol = new SaharaProtocol(transport);
        var complete = false;
        protocol.HandshakeComplete += (_, _) => complete = true;

        await Assert.ThrowsAsync<SaharaException>(() => protocol.HandshakeAsync(
            ImagesWithFirehose(), TestContext.Current.CancellationToken));

        Assert.False(complete);
        Assert.Single(transport.Writes);
    }

    [Theory]
    [InlineData("NACK")]
    [InlineData("NAK")]
    [InlineData("false")]
    [InlineData("ACKNOWLEDGED")]
    public async Task HandshakeAsync_ProbeWithoutExactAck_DoesNotReportFirehose(string status)
    {
        using var transport = new ScriptedCommTransport
        {
            OnWrite = _ => [Encoding.UTF8.GetBytes(
                $"<data><log value=\"ACK is required\"/><response value=\"{status}\"/></data>")]
        };
        var protocol = new SaharaProtocol(transport);

        await Assert.ThrowsAsync<EdlException>(() => protocol.HandshakeAsync(
            new Dictionary<uint, Stream>(), TestContext.Current.CancellationToken));

        Assert.False(protocol.FirehoseAlreadyRunning);
    }

    [Fact]
    public async Task HandshakeAsync_FragmentedProbeDocuments_ParsesFinalAck()
    {
        using var transport = new ScriptedCommTransport
        {
            OnWrite = _ => [.. Encoding.UTF8.GetBytes(
                "<data><log value=\"starting\"/></data><data><response value=\"ACK\"/></data>")
                .Select(value => new byte[] { value })]
        };
        var protocol = new SaharaProtocol(transport);

        var mode = await protocol.HandshakeAsync(new Dictionary<uint, Stream>(), TestContext.Current.CancellationToken);

        Assert.Equal(SaharaMode.ImageTxComplete, mode);
        Assert.True(protocol.FirehoseAlreadyRunning);
    }

    [Fact]
    public async Task HandshakeAsync_CompletedTransfer_PreservesNextProtocolBytes()
    {
        using var transport = new ScriptedCommTransport();
        EnqueueHello(transport);
        EnqueueEndOfImage(transport, 0);
        EnqueueDoneResponse(transport, 1);
        var xml = Encoding.UTF8.GetBytes("<data><log value=\"Firehose ready\"/></data>");
        transport.Enqueue(xml);
        var protocol = new SaharaProtocol(transport);

        await protocol.HandshakeAsync(ImagesWithFirehose(), TestContext.Current.CancellationToken);

        var received = new byte[xml.Length];
        Assert.Equal(xml.Length, await transport.ReadAsync(received, 0, received.Length, 50, TestContext.Current.CancellationToken));
        Assert.Equal(xml, received);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(24)]
    [InlineData(uint.MaxValue)]
    public async Task HandshakeAsync_InvalidReadPacketLength_RejectsBeforePayloadWrite(uint length)
    {
        using var transport = new ScriptedCommTransport();
        EnqueueHello(transport);
        transport.Enqueue(StructToBytes(new SaharaPacketHeader { Command = SaharaCommand.ReadData, Length = length }));
        var protocol = new SaharaProtocol(transport);

        await Assert.ThrowsAsync<SaharaException>(() => protocol.HandshakeAsync(
            ImagesWithFirehose(), TestContext.Current.CancellationToken));

        Assert.Single(transport.Writes);
    }

    [Fact]
    public async Task HandshakeAsync_TruncatedFirstHello_DoesNotProbeAnotherProtocol()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(BuildHelloPacket(1)[..7]);
        var protocol = new SaharaProtocol(transport);

        await Assert.ThrowsAsync<SaharaException>(() => protocol.HandshakeAsync(
            ImagesWithFirehose(), TestContext.Current.CancellationToken));

        Assert.Empty(transport.Writes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task HandshakeAsync_PendingCompatibility_RequiresExplicitSingleImageProfile(bool enabled, bool multiple)
    {
        using var transport = new ScriptedCommTransport();
        EnqueueHello(transport);
        EnqueueEndOfImage(transport, 0);
        EnqueueDoneResponse(transport, 0);
        using var image = new MemoryStream(new byte[16]);
        var images = new Dictionary<uint, Stream> { [13] = image };
        if (multiple)
        {
            images[14] = image;
        }
        var protocol = new SaharaProtocol(transport) { AllowSingleFirehosePendingCompletion = enabled };

        if (enabled && !multiple)
        {
            await protocol.HandshakeAsync(images, TestContext.Current.CancellationToken);
        }
        else
        {
            await Assert.ThrowsAsync<EdlException>(() => protocol.HandshakeAsync(images, TestContext.Current.CancellationToken));
        }
        Assert.Equal(2, transport.Writes.Count);
    }

    [Fact]
    public async Task HandshakeAsync_DoneWithoutEndOfImage_RejectsCompletion()
    {
        using var transport = new ScriptedCommTransport();
        EnqueueHello(transport);
        EnqueueDoneResponse(transport, 1);
        var protocol = new SaharaProtocol(transport);

        await Assert.ThrowsAsync<SaharaException>(() => protocol.HandshakeAsync(
            ImagesWithFirehose(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HandshakeAsync_DeviceAdvertisesSmallerCommandLength_HostKeepsSuccessStatusAndFullRequestedPayload()
    {
        using var transport = new ScriptedCommTransport();
        var hello = new byte[48];
        BinaryPrimitives.WriteUInt32LittleEndian(hello.AsSpan(0, 4), 0x01);
        BinaryPrimitives.WriteUInt32LittleEndian(hello.AsSpan(4, 4), 48);
        BinaryPrimitives.WriteUInt32LittleEndian(hello.AsSpan(8, 4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(hello.AsSpan(12, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(hello.AsSpan(16, 4), 1500);
        transport.Enqueue(hello);
        byte[] image = [.. Enumerable.Repeat((byte)0x77, 1024)];
        using var stream = new MemoryStream(image);
        var images = new Dictionary<uint, Stream> { [0x0D] = stream };
        EnqueueReadData(transport, imageId: 0x0D, offset: 0, length: 1024);
        EnqueueEndOfImage(transport, 0);
        EnqueueDoneResponse(transport, 1);
        EnqueueCommandReady(transport);
        var protocol = new SaharaProtocol(transport);

        await protocol.HandshakeAsync(images, TestContext.Current.CancellationToken);

        Assert.Equal(48, transport.Writes[0].Length);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(transport.Writes[0].AsSpan(16, 4)));
        Assert.Equal(4096u, protocol.MaxPacketLength);
        Assert.Equal(image, transport.Writes[1]);
    }
}