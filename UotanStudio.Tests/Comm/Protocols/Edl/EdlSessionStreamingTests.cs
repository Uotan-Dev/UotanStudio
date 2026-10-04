using System.Buffers.Binary;
using System.Text;
using UotanStudio.Comm.Protocols.Common.Evidence;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.Streaming;
using UotanStudio.Tests.Comm.Protocols.Edl.Streaming;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

public sealed class EdlSessionStreamingTests
{
    private const uint SwitchToStreamDloadCommand = 0x05;

    [Fact]
    public async Task InitializeAsync_CommandModeHelloWithStreamDloadAllowed_ExposesStreamingBackendAndLeavesFirehoseUnconfigured()
    {
        using var transport = new ScriptedCommTransport { OnWrite = RouteDeviceReply };
        transport.Enqueue(BuildCommandModeHello());
        using var session = new EdlSession(transport);

        await session.InitializeAsync((Stream?)null,
            new EdlInitializationOptions { AllowStreamDload = true },
            TestContext.Current.CancellationToken);

        Assert.Equal(EdlProtocolState.Streaming, session.ProtocolState);
        Assert.Equal(EdlDownloadBackend.StreamDload, session.Backend);
        Assert.NotNull(session.Streaming);
        Assert.NotNull(session.Streaming!.Device);
        Assert.False(session.IsConfigured);
        Assert.Null(session.Firehose!.Capabilities);
        var written = Encoding.UTF8.GetString(transport.Writes.SelectMany(static write => write).ToArray());
        Assert.DoesNotContain("<configure", written, StringComparison.Ordinal);
        Assert.Contains(session.InitializationEvidence, static evidence =>
            evidence.Phase == "stream-dload-selected" && evidence.Outcome == CommandOutcomeKind.Completed);
    }

    [Fact]
    public async Task InitializeAsync_CommandModeHelloWithoutStreamDloadAllowed_ThrowsProgrammerImageError()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(BuildCommandModeHello());
        using var session = new EdlSession(transport);

        var error = await Assert.ThrowsAsync<SaharaException>(() => session.InitializeAsync(
            (Stream?)null,
            new EdlInitializationOptions { AllowStreamDload = false },
            TestContext.Current.CancellationToken));

        Assert.Contains("no programmer image", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(EdlProtocolState.Unknown, session.ProtocolState);
        Assert.Equal(EdlDownloadBackend.None, session.Backend);
        Assert.Null(session.Streaming);
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task InitializeAsync_ImageTxPendingHelloWithStreamDloadAllowed_StillThrowsProgrammerImageError()
    {
        using var transport = new ScriptedCommTransport { OnWrite = RouteDeviceReply };
        transport.Enqueue(BuildImageTxPendingHello());
        using var session = new EdlSession(transport);

        var error = await Assert.ThrowsAsync<SaharaException>(() => session.InitializeAsync(
            (Stream?)null,
            new EdlInitializationOptions { AllowStreamDload = true },
            TestContext.Current.CancellationToken));

        Assert.Contains("no programmer image", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(EdlDownloadBackend.None, session.Backend);
        Assert.Null(session.Streaming);
    }

    private static IEnumerable<byte[]> RouteDeviceReply(byte[] write)
    {
        if (write.Length == 12)
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(write) == (uint)SaharaCommand.Execute
                ? [BuildExecuteResponse(SwitchToStreamDloadCommand)]
                : [];
        }

        if (write.Length == 48 && BinaryPrimitives.ReadUInt32LittleEndian(write) == (uint)SaharaCommand.HelloResponse)
        {
            return [BuildCommandReady()];
        }

        if (DiagHdlcCodec.TryDecode(write, withCrc: true, verifyCrc: true, out var payload)
            && payload is { Length: > 0 }
            && payload[0] == StreamingConstants.DiagHelloCommand)
        {
            return [StreamingTestDiag.Reply(StreamingTestDiag.HelloResponse())];
        }

        return [];
    }

    private static byte[] BuildCommandModeHello() => BuildHello(SaharaMode.Command);

    private static byte[] BuildImageTxPendingHello() => BuildHello(SaharaMode.ImageTxPending);

    private static byte[] BuildHello(SaharaMode mode)
    {
        var bytes = new byte[48];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)SaharaCommand.Hello);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 48);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), (uint)mode);
        return bytes;
    }

    private static byte[] BuildCommandReady()
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)SaharaCommand.CommandReady);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 8);
        return bytes;
    }

    private static byte[] BuildExecuteResponse(uint echoedCommand)
    {
        var bytes = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)SaharaCommand.ExecuteResponse);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), echoedCommand);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 0);
        return bytes;
    }
}