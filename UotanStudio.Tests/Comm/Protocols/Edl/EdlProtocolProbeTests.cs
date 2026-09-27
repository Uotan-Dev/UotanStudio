using System.Buffers.Binary;
using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

public sealed class EdlProtocolProbeTests
{
    internal static byte[] Hello()
    {
        var bytes = new byte[48];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 48);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 4096);
        return bytes;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(48)]
    public async Task ProbeProtocolAsync_FragmentedHello_PreservesHandshakeWithoutWrites(int chunkSize)
    {
        var transport = new ScriptedCommTransport();
        var hello = Hello();
        foreach (var chunk in hello.Chunk(chunkSize))
        {
            transport.Enqueue(chunk);
        }
        using var session = new EdlSession(transport);

        Assert.Equal(EdlProtocolState.Sahara, await session.ProbeProtocolAsync(TestContext.Current.CancellationToken));
        Assert.Empty(transport.Writes);
        var replay = new byte[48];
        Assert.Equal(48, await session.Transport.ReadAsync(replay, 0, replay.Length, 100,
            TestContext.Current.CancellationToken));
        Assert.Equal(hello, replay);
        Assert.False(session.IsConfigured);
    }

    [Theory]
    [InlineData("<data><response value=\"ACK\" /></data>", EdlProtocolState.Firehose)]
    [InlineData("<data><response value=\"NAK\" /></data>", EdlProtocolState.Firehose)]
    [InlineData("<data><response value=\"ACK\" rawmode=\"true\" /></data>", EdlProtocolState.Unknown)]
    [InlineData("<data><log value=\"Firehose\" /></data>", EdlProtocolState.Unknown)]
    [InlineData("<data><response value=\"ACK\"", EdlProtocolState.Unknown)]
    [InlineData("<data><response value=\"INVALID\" /></data>", EdlProtocolState.Unknown)]
    [InlineData("<!DOCTYPE data [<!ENTITY x 'ACK'>]><data><response value=\"&x;\"/></data>", EdlProtocolState.Unknown)]
    public async Task ProbeProtocolAsync_NopResponse_RequiresCompleteVerifiedResponse(string response, EdlProtocolState expected)
    {
        var transport = new ScriptedCommTransport { OnWrite = _ => [Encoding.UTF8.GetBytes(response)] };
        using var session = new EdlSession(transport);

        Assert.Equal(expected, await session.ProbeProtocolAsync(TestContext.Current.CancellationToken));
        Assert.Contains("<nop ", Encoding.UTF8.GetString(Assert.Single(transport.Writes)));
        Assert.False(session.IsConfigured);
    }

    [Fact]
    public async Task ProbeProtocolAsync_InvalidHello_DoesNotGuessProtocolOrWrite()
    {
        var transport = new ScriptedCommTransport();
        var hello = Hello();
        BinaryPrimitives.WriteUInt32LittleEndian(hello.AsSpan(4), uint.MaxValue);
        transport.Enqueue(hello);
        using var session = new EdlSession(transport);
        Assert.Equal(EdlProtocolState.Unknown, await session.ProbeProtocolAsync(TestContext.Current.CancellationToken));
        Assert.Empty(transport.Writes);
    }

    [Theory]
    [InlineData((uint)SaharaCommand.ExecuteResponse, 16, EdlProtocolState.Sahara)]
    [InlineData((uint)SaharaCommand.Execute, 16, EdlProtocolState.Unknown)]
    [InlineData((uint)SaharaCommand.Hello, 8, EdlProtocolState.Unknown)]
    public void Classify_PacketHeader_RequiresDeviceCommandAndExactLength(uint command, int length, EdlProtocolState expected)
    {
        var bytes = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, command);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)length);
        Assert.Equal(expected, EdlProbeTransport.Classify(bytes));
    }

    [Fact]
    public async Task ProbeProtocolAsync_IncompleteHello_ResumesWithoutLosingPrefix()
    {
        var transport = new ScriptedCommTransport();
        var hello = Hello();
        transport.Enqueue(hello[..16]);
        using var session = new EdlSession(transport);
        Assert.Equal(EdlProtocolState.Unknown, await session.ProbeProtocolAsync(TestContext.Current.CancellationToken));
        transport.Enqueue(hello[16..]);
        Assert.Equal(EdlProtocolState.Sahara, await session.ProbeProtocolAsync(TestContext.Current.CancellationToken));
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task ProbeProtocolAsync_SilentDevice_RemainsUnknownAfterSingleNop()
    {
        var transport = new ScriptedCommTransport();
        using var session = new EdlSession(transport);
        Assert.Equal(EdlProtocolState.Unknown, await session.ProbeProtocolAsync(TestContext.Current.CancellationToken));
        Assert.Single(transport.Writes);
    }

    [Fact]
    public async Task ProbeProtocolAsync_PassiveFirehoseLogs_RequestsResponseBeforeClassification()
    {
        var transport = new ScriptedCommTransport
        {
            OnWrite = _ => [Encoding.UTF8.GetBytes("<data><response value=\"ACK\" /></data>")]
        };
        transport.Enqueue("<data><log value=\"ready\" /></data>");
        using var session = new EdlSession(transport);
        Assert.Equal(EdlProtocolState.Firehose, await session.ProbeProtocolAsync(TestContext.Current.CancellationToken));
        Assert.Single(transport.Writes);
    }

    [Fact]
    public async Task ProbeProtocolAsync_Canceled_DoesNotReadOrWrite()
    {
        var transport = new ScriptedCommTransport();
        using var session = new EdlSession(transport);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await lifetime.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ProbeProtocolAsync(lifetime.Token));
        Assert.Equal(0, transport.ReadCount);
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task InitializeAsync_AfterFirehoseProbe_DiscardsProbeAckAndConfiguresDirectly()
    {
        var transport = new ScriptedCommTransport
        {
            OnWrite = bytes => [Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Contains("<nop ")
                ? "<data><response value=\"ACK\" /></data>"
                : "<data><response value=\"ACK\" MaxPayloadSizeToTargetInBytes=\"4096\" /></data>")]
        };
        using var session = new EdlSession(transport, new FirehoseConfiguration { AutoDetectSectorSize = false, SectorSize = 512 });
        Assert.Equal(EdlProtocolState.Firehose, await session.ProbeProtocolAsync(TestContext.Current.CancellationToken));

        await session.InitializeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(session.IsConfigured);
        Assert.Null(session.Sahara);
        Assert.Equal(2, transport.Writes.Count);
        Assert.Contains("<configure ", Encoding.UTF8.GetString(transport.Writes[1]));
    }

    [Fact]
    public async Task InitializeAsync_AfterSaharaProbe_ReplaysHelloAndTransitionsToFirehose()
    {
        var transport = new ScriptedCommTransport
        {
            OnWrite = bytes => Encoding.UTF8.GetString(bytes).Contains("<configure ")
                ? [Encoding.UTF8.GetBytes("<data><response value=\"ACK\" /></data>")] : []
        };
        transport.Enqueue(Hello());
        var end = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(end, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(4), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(8), (uint)SaharaImageId.Firehose);
        transport.Enqueue(end);
        var done = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(done, 6);
        BinaryPrimitives.WriteUInt32LittleEndian(done.AsSpan(4), 12);
        BinaryPrimitives.WriteUInt32LittleEndian(done.AsSpan(8), 1);
        transport.Enqueue(done);
        using var session = new EdlSession(transport, new FirehoseConfiguration { AutoDetectSectorSize = false, SectorSize = 512 });
        List<EdlProtocolState> changes = [];
        session.ProtocolStateChanged += (_, state) => changes.Add(state);
        Assert.Equal(EdlProtocolState.Sahara, await session.ProbeProtocolAsync(TestContext.Current.CancellationToken));
        using var programmer = new MemoryStream([1, 2, 3, 4]);

        await session.InitializeAsync(programmer, TestContext.Current.CancellationToken);

        Assert.True(session.IsConfigured);
        Assert.Equal([EdlProtocolState.Sahara, EdlProtocolState.Firehose], changes);
        Assert.Equal(3, transport.Writes.Count);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(transport.Writes[0]));
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(transport.Writes[1]));
    }

    [Fact]
    public async Task ProbeProtocolAsync_OversizedUnrecognizedInput_StopsAtBound()
    {
        var transport = new ScriptedCommTransport();
        transport.Enqueue(new byte[128 * 1024]);
        using var session = new EdlSession(transport);
        Assert.Equal(EdlProtocolState.Unknown, await session.ProbeProtocolAsync(TestContext.Current.CancellationToken));
        Assert.InRange(transport.ReadCount, 1, 1366);
        Assert.Empty(transport.Writes);
    }
}