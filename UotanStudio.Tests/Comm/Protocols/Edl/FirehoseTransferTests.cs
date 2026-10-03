using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

public class FirehoseTransferTests
{
    private const string Ready = "<data><response value=\"ACK\" rawmode=\"true\"/></data>";
    private const string Complete = "<data><response value=\"ACK\" rawmode=\"false\"/></data>";
    private const string Nak = "<data><response value=\"NAK\" rawmode=\"false\"/></data>";

    [Theory]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(4096)]
    public async Task ReadToStreamAsync_FragmentedXmlAndBinary_PreservesEveryByte(int fragmentSize)
    {
        using var transport = new ScriptedCommTransport();
        using var protocol = CreateProtocol(transport);
        var payload = new byte[8192];
        new Random(37).NextBytes(payload);
        Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><data><log value=\"image content\"/></data>").CopyTo(payload, 0);
        payload[^1] = (byte)'<';
        byte[] wire = [.. Encoding.UTF8.GetBytes(Ready), .. payload, .. Encoding.UTF8.GetBytes(Complete + Nak)];
        for (var offset = 0; offset < wire.Length; offset += fragmentSize)
        {
            transport.Enqueue(wire.AsSpan(offset, Math.Min(fragmentSize, wire.Length - offset)).ToArray());
        }

        using var destination = new MemoryStream();
        var response = await protocol.ReadToStreamAsync(destination, 0, 16, cancellationToken: TestContext.Current.CancellationToken);
        var next = await protocol.NopAsync(TestContext.Current.CancellationToken);

        Assert.True(response.IsAck);
        Assert.Equal(payload, destination.ToArray());
        Assert.True(next.IsNak);
    }

    [Theory]
    [InlineData(Nak)]
    [InlineData(Ready)]
    public async Task ReadToStreamAsync_InvalidFinalResponse_FaultsSession(string finalResponse)
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(Ready);
        transport.Enqueue(new byte[512]);
        transport.Enqueue(finalResponse);
        using var protocol = CreateProtocol(transport);
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<EdlException>(() =>
            protocol.ReadToStreamAsync(destination, 0, 1, cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(protocol.RequiresReconnect);
        await Assert.ThrowsAsync<EdlException>(() => protocol.NopAsync(TestContext.Current.CancellationToken));
        Assert.Single(transport.Writes);
    }

    [Fact]
    public async Task ProgramAsync_DeviceRequiresReadyRead_DoesNotWritePayloadPrematurely()
    {
        using var transport = new ScriptedCommTransport();
        var payload = new byte[1024];
        payload.AsSpan().Fill(0x51);
        transport.OnWrite = bytes =>
        {
            if (Encoding.UTF8.GetString(bytes).Contains("<program ", StringComparison.Ordinal))
            {
                return [Encoding.UTF8.GetBytes(Ready)];
            }

            Assert.Equal(payload, bytes);
            Assert.True(transport.ReadCount > 0);
            return [Encoding.UTF8.GetBytes(Complete)];
        };
        using var protocol = CreateProtocol(transport);
        using var source = new FragmentedSource(payload);
        var response = await protocol.ProgramAsync(source, 0, 2, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(response.IsAck);
        Assert.Equal(payload, transport.Writes[1]);
    }

    [Fact]
    public async Task ProgramAsync_PayloadWriteTimesOut_DoesNotReplayAndRejectsNextCommand()
    {
        using var transport = new ScriptedCommTransport();
        transport.OnWrite = bytes =>
        {
            if (Encoding.UTF8.GetString(bytes).Contains("<program ", StringComparison.Ordinal))
            {
                return [Encoding.UTF8.GetBytes(Ready)];
            }

            throw new EdlException(EdlErrorCode.TransportTimeout);
        };
        using var protocol = CreateProtocol(transport);
        using var source = new MemoryStream(new byte[512]);

        await Assert.ThrowsAsync<EdlException>(() =>
            protocol.ProgramAsync(source, 0, 1, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<EdlException>(() => protocol.NopAsync(TestContext.Current.CancellationToken));

        Assert.True(protocol.RequiresReconnect);
        Assert.Equal(2, transport.Writes.Count);
    }

    [Theory]
    [InlineData(Complete)]
    public async Task ProgramAsync_EntryNotAcknowledgedInRawMode_DoesNotConsumeSource(string response)
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(response);
        using var protocol = CreateProtocol(transport);
        using var source = new MemoryStream(new byte[512]);

        await Assert.ThrowsAsync<EdlException>(() =>
            protocol.ProgramAsync(source, 0, 1, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, source.Position);
        Assert.Single(transport.Writes);
    }

    [Fact]
    public async Task ProgramAsync_SourceTooShort_RejectsBeforeDeviceIo()
    {
        using var transport = new ScriptedCommTransport();
        using var protocol = CreateProtocol(transport);
        using var source = new MemoryStream(0);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            protocol.ProgramAsync(source, 0, 1, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(transport.Writes);
        Assert.False(protocol.RequiresReconnect);
    }

    [Fact]
    public async Task ProgramAsync_SourceShortByPartialSector_SendsZeroFilledFinalSector()
    {
        using var transport = new ScriptedCommTransport();
        using var protocol = CreateProtocol(transport);
        transport.OnWrite = _ =>
        [
            Encoding.UTF8.GetBytes(transport.Writes.Count switch
            {
                1 => "<data><response value='ACK' rawmode='true'/></data>",
                _ => Complete
            })
        ];
        byte[] data = [.. Enumerable.Repeat((byte)0xC3, 511)];
        using var source = new MemoryStream(data);

        var response = await protocol.ProgramAsync(source, 0, 1, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(response.IsAck);
        byte[] expectedPayload = [.. data, 0];
        Assert.Equal(expectedPayload, transport.Writes[1]);
    }

    [Fact]
    public async Task ProgramAsync_PreCancelled_DoesNotWriteOrFaultSession()
    {
        using var transport = new ScriptedCommTransport();
        using var protocol = CreateProtocol(transport);
        using var source = new MemoryStream(new byte[512]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            protocol.ProgramAsync(source, 0, 1, cancellationToken: cancellation.Token));

        Assert.Empty(transport.Writes);
        Assert.False(protocol.RequiresReconnect);
    }

    [Fact]
    public async Task ProgramAsync_CancelledAfterRawEntry_RequiresReconnect()
    {
        using var transport = new ScriptedCommTransport();
        using var protocol = CreateProtocol(transport);
        using var source = new MemoryStream(new byte[512]);
        using var cancellation = new CancellationTokenSource();
        transport.OnWrite = _ =>
        {
            cancellation.Cancel();
            return [Encoding.UTF8.GetBytes(Ready)];
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            protocol.ProgramAsync(source, 0, 1, cancellationToken: cancellation.Token));

        Assert.True(protocol.RequiresReconnect);
        Assert.Single(transport.Writes);
    }

    [Fact]
    public async Task ProgramAsync_RangeOverflows_RejectsBeforeDeviceIo()
    {
        using var transport = new ScriptedCommTransport();
        using var protocol = CreateProtocol(transport);
        using var source = new MemoryStream();
        await Assert.ThrowsAsync<OverflowException>(() =>
            protocol.ProgramAsync(source, 0, long.MaxValue, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task NopAsync_LargeFragmentedLog_PreservesFollowingResponse()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue("<data><log value=\"" + new string('x', 12000) + "\"/></data>" + Complete);
        using var protocol = CreateProtocol(transport);
        var logs = new List<string>();
        protocol.TargetLogReceived += (_, args) => logs.Add(args.Value);

        var response = await protocol.NopAsync(TestContext.Current.CancellationToken);

        Assert.True(response.IsAck);
        Assert.Equal(12000, Assert.Single(logs).Length);
    }

    [Fact]
    public async Task NopAsync_OversizedUnterminatedXml_RejectsBoundedly()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue("<data><log value=\"" + new string('x', 65536));
        using var protocol = CreateProtocol(transport);

        await Assert.ThrowsAsync<FirehoseException>(() => protocol.NopAsync(TestContext.Current.CancellationToken));
        Assert.True(protocol.RequiresReconnect);
    }

    [Fact]
    public async Task ConfigureAsync_Timeout_DoesNotRetryIntoDelayedAck()
    {
        using var transport = new ScriptedCommTransport();
        using var protocol = CreateProtocol(transport);
        protocol.Configuration.ConfigureFirstAttemptTimeoutMs = 5;
        protocol.Configuration.ConfigureRetryIntervalMs = 0;

        var error = await Assert.ThrowsAsync<EdlException>(() => protocol.ConfigureAsync(
            maxRetries: 3, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(EdlErrorCode.ConnectionLost, error.ErrorCode);
        Assert.True(protocol.RequiresReconnect);
        Assert.Single(transport.Writes);
    }

    [Fact]
    public async Task ConfigureAsync_TransportTimeout_FailsInsideFirstAttemptWithoutRetryWrite()
    {
        using var transport = new ScriptedCommTransport();
        using var protocol = CreateProtocol(transport);
        protocol.Configuration.ConfigureFirstAttemptTimeoutMs = 5;
        protocol.Configuration.ConfigureRetryIntervalMs = 0;

        var error = await Assert.ThrowsAsync<EdlException>(() => protocol.ConfigureAsync(
            maxRetries: 30, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(EdlErrorCode.ConnectionLost, error.ErrorCode);
        Assert.Equal("Configure timed out; reconnect rather than reusing a potentially delayed acknowledgement.", error.Message);
        var inner = Assert.IsType<EdlException>(error.InnerException);
        Assert.Equal(EdlErrorCode.TransportTimeout, inner.ErrorCode);
        Assert.Single(transport.Writes);
    }

    [Theory]
    [InlineData("<!DOCTYPE data [<!ENTITY e 'ACK'>]><data><response value='&e;'/></data>")]
    [InlineData("<data><response value='ACK'/><response value='NAK'/></data>")]
    [InlineData("<data><response value='ACK'></data>")]
    public void ParseDataBlock_UntrustedXml_RejectsInvalidOrAmbiguousResponse(string xml)
    {
        Assert.Throws<FirehoseException>(() => FirehoseResponseParser.ParseDataBlock(xml));
    }

    private static FirehoseProtocol CreateProtocol(ScriptedCommTransport transport) => new(transport,
        new FirehoseConfiguration { SectorSize = 512, AutoDetectSectorSize = false, ResponseTimeoutSeconds = 1 }, 50);

    private sealed class FragmentedSource(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(17, buffer.Length)], cancellationToken);
    }
}