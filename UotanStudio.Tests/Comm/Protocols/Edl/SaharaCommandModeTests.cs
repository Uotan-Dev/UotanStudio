using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl;

/// <summary>
/// Sahara command-mode entry and the exec diagnostics built on it, driven by a scripted transport.
/// Covers the command-mode HELLO reply, COMMAND_READY consumption and cmd_exec frame shapes.
/// <para>Sahara 命令模式入口与构建在其上的 exec 诊断，由脚本化传输驱动。覆盖命令模式 HELLO 应答、
/// COMMAND_READY 消费与各类 cmd_exec 帧形状。</para>
/// </summary>
public sealed class SaharaCommandModeTests
{
    [Fact]
    public async Task EnterCommandModeAsync_CommandModeHello_AnswersInCommandModeAndConsumesCommandReady()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(BuildHello(SaharaMode.Command));
        transport.Enqueue(BuildCommandReady());
        var sahara = new SaharaProtocol(transport);

        var mode = await sahara.EnterCommandModeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SaharaMode.Command, mode);
        Assert.True(sahara.IsCommandModeReady);
        var helloResponse = Assert.Single(transport.Writes);
        Assert.Equal(48, helloResponse.Length);
        Assert.Equal((uint)SaharaCommand.HelloResponse, BinaryPrimitives.ReadUInt32LittleEndian(helloResponse));
        Assert.Equal(48u, BinaryPrimitives.ReadUInt32LittleEndian(helloResponse.AsSpan(4)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(helloResponse.AsSpan(8)));
        Assert.Equal(EdlConstants.SaharaHostCompatibleVersion,
            BinaryPrimitives.ReadUInt32LittleEndian(helloResponse.AsSpan(12)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(helloResponse.AsSpan(16)));
        Assert.Equal((uint)SaharaMode.Command, BinaryPrimitives.ReadUInt32LittleEndian(helloResponse.AsSpan(20)));
    }

    [Fact]
    public async Task EnterCommandModeAsync_SecondCallOnNegotiatedLink_SendsNoFurtherPackets()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(BuildHello(SaharaMode.Command));
        transport.Enqueue(BuildCommandReady());
        var sahara = new SaharaProtocol(transport);
        _ = await sahara.EnterCommandModeAsync(TestContext.Current.CancellationToken);

        var mode = await sahara.EnterCommandModeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SaharaMode.Command, mode);
        Assert.Single(transport.Writes);
    }

    [Theory]
    [InlineData(SaharaMode.ImageTxPending)]
    [InlineData(SaharaMode.ImageTxComplete)]
    [InlineData(SaharaMode.MemoryDebug)]
    public async Task EnterCommandModeAsync_HelloInAnotherMode_ThrowsBeforeAnswering(SaharaMode mode)
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(BuildHello(mode));
        var sahara = new SaharaProtocol(transport);

        var exception = await Assert.ThrowsAsync<SaharaException>(() =>
            sahara.EnterCommandModeAsync(TestContext.Current.CancellationToken));

        Assert.Contains("command-mode HELLO", exception.Message, StringComparison.Ordinal);
        Assert.False(sahara.IsCommandModeReady);
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task EnterCommandModeAsync_FirehoseXmlFirstBytes_ThrowsBeforeAnswering()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(BuildFirehoseXmlHello());
        var sahara = new SaharaProtocol(transport);

        var exception = await Assert.ThrowsAsync<SaharaException>(() =>
            sahara.EnterCommandModeAsync(TestContext.Current.CancellationToken));

        Assert.Contains("Firehose XML", exception.Message, StringComparison.Ordinal);
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task EnterCommandModeAsync_TargetSendsEndOfImageInsteadOfCommandReady_Throws()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(BuildHello(SaharaMode.Command));
        transport.Enqueue(BuildPacket((uint)SaharaCommand.EndOfImage, 16));
        var sahara = new SaharaProtocol(transport);

        var exception = await Assert.ThrowsAsync<SaharaException>(() =>
            sahara.EnterCommandModeAsync(TestContext.Current.CancellationToken));

        Assert.Contains("CommandReady", exception.Message, StringComparison.Ordinal);
        Assert.False(sahara.IsCommandModeReady);
        Assert.Single(transport.Writes);
    }

    [Fact]
    public async Task EnterCommandModeAsync_CancelledToken_SendsNoPackets()
    {
        using var transport = new ScriptedCommTransport();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var sahara = new SaharaProtocol(transport);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            sahara.EnterCommandModeAsync(cancellation.Token));

        Assert.Empty(transport.Writes);
        Assert.Equal(0, transport.ReadCount);
    }

    [Fact]
    public async Task NopAsync_NegotiatedCommandModeLink_SendsTheReferenceExecFrames()
    {
        using var transport = new ScriptedCommTransport
        {
            OnWrite = RouteCommandModeDevice(EdlConstants.SaharaExecCmdNop, [])
        };
        transport.Enqueue(BuildHello(SaharaMode.Command));
        var sahara = new SaharaProtocol(transport);
        _ = await sahara.EnterCommandModeAsync(TestContext.Current.CancellationToken);

        await sahara.NopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, transport.Writes.Count);
        AssertExecRequest(transport.Writes[1], SaharaCommand.Execute, EdlConstants.SaharaExecCmdNop);
        AssertExecRequest(transport.Writes[2], SaharaCommand.ExecuteData, EdlConstants.SaharaExecCmdNop);
        Assert.True(sahara.IsCommandModeReady);
    }

    [Fact]
    public async Task ReadDebugDataAsync_NegotiatedCommandModeLink_ReturnsPayloadAfterExecuteData()
    {
        var payload = BuildPattern(256, seed: 41);
        using var transport = new ScriptedCommTransport
        {
            OnWrite = RouteCommandModeDevice(EdlConstants.SaharaExecCmdReadDebugData, payload)
        };
        transport.Enqueue(BuildHello(SaharaMode.Command));
        var sahara = new SaharaProtocol(transport);
        _ = await sahara.EnterCommandModeAsync(TestContext.Current.CancellationToken);

        var debugData = await sahara.ReadDebugDataAsync(TestContext.Current.CancellationToken);

        Assert.Equal(payload, debugData);
        Assert.Equal(3, transport.Writes.Count);
        AssertExecRequest(transport.Writes[1], SaharaCommand.Execute, EdlConstants.SaharaExecCmdReadDebugData);
        AssertExecRequest(transport.Writes[2], SaharaCommand.ExecuteData, EdlConstants.SaharaExecCmdReadDebugData);
    }

    [Fact]
    public async Task GetTrainingDataAsync_NegotiatedCommandModeLink_ReturnsPayloadAfterExecuteData()
    {
        var payload = BuildPattern(1024, seed: 43);
        using var transport = new ScriptedCommTransport
        {
            OnWrite = RouteCommandModeDevice(EdlConstants.SaharaExecCmdGetTrainingData, payload)
        };
        transport.Enqueue(BuildHello(SaharaMode.Command));
        var sahara = new SaharaProtocol(transport);
        _ = await sahara.EnterCommandModeAsync(TestContext.Current.CancellationToken);

        var trainingData = await sahara.GetTrainingDataAsync(TestContext.Current.CancellationToken);

        Assert.Equal(payload, trainingData);
        Assert.Equal(3, transport.Writes.Count);
        AssertExecRequest(transport.Writes[1], SaharaCommand.Execute, EdlConstants.SaharaExecCmdGetTrainingData);
        AssertExecRequest(transport.Writes[2], SaharaCommand.ExecuteData, EdlConstants.SaharaExecCmdGetTrainingData);
    }

    [Fact]
    public async Task GetCommandIdListAsync_NegotiatedCommandModeLink_ReturnsRawLittleEndianWords()
    {
        var payload = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00 };
        using var transport = new ScriptedCommTransport
        {
            OnWrite = RouteCommandModeDevice(EdlConstants.SaharaExecCmdGetCommandIdList, payload)
        };
        transport.Enqueue(BuildHello(SaharaMode.Command));
        var sahara = new SaharaProtocol(transport);
        _ = await sahara.EnterCommandModeAsync(TestContext.Current.CancellationToken);

        var list = await sahara.GetCommandIdListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(payload, list);
        Assert.Equal(3, transport.Writes.Count);
        AssertExecRequest(transport.Writes[1], SaharaCommand.Execute, EdlConstants.SaharaExecCmdGetCommandIdList);
    }

    [Fact]
    public async Task SwitchToDmssDloadAsync_TargetAcceptsSwitch_ClearsCommandModeReadiness()
    {
        using var transport = new ScriptedCommTransport
        {
            OnWrite = RouteCommandModeDevice(EdlConstants.SaharaExecCmdSwitchToDmssDload, [])
        };
        transport.Enqueue(BuildHello(SaharaMode.Command));
        var sahara = new SaharaProtocol(transport);
        _ = await sahara.EnterCommandModeAsync(TestContext.Current.CancellationToken);

        var payload = await sahara.SwitchToDmssDloadAsync(TestContext.Current.CancellationToken);

        Assert.Empty(payload);
        Assert.False(sahara.IsCommandModeReady);
        Assert.Equal(3, transport.Writes.Count);
        AssertExecRequest(transport.Writes[1], SaharaCommand.Execute, EdlConstants.SaharaExecCmdSwitchToDmssDload);
        AssertExecRequest(transport.Writes[2], SaharaCommand.ExecuteData, EdlConstants.SaharaExecCmdSwitchToDmssDload);
    }

    [Fact]
    public async Task SwitchToStreamDloadAsync_TargetAcceptsSwitch_ClearsCommandModeReadiness()
    {
        using var transport = new ScriptedCommTransport
        {
            OnWrite = RouteCommandModeDevice(EdlConstants.SaharaExecCmdSwitchToStreamDload, [])
        };
        transport.Enqueue(BuildHello(SaharaMode.Command));
        var sahara = new SaharaProtocol(transport);
        _ = await sahara.EnterCommandModeAsync(TestContext.Current.CancellationToken);

        var payload = await sahara.SwitchToStreamDloadAsync(TestContext.Current.CancellationToken);

        Assert.Empty(payload);
        Assert.False(sahara.IsCommandModeReady);
        AssertExecRequest(transport.Writes[1], SaharaCommand.Execute, EdlConstants.SaharaExecCmdSwitchToStreamDload);
    }

    [Fact]
    public async Task NopAsync_TargetRejectsExecRequest_ThrowsWithoutSendingExecuteData()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(BuildHello(SaharaMode.Command));
        transport.Enqueue(BuildCommandReady());
        transport.Enqueue(BuildPacket((uint)SaharaCommand.EndOfImage, 16));
        var sahara = new SaharaProtocol(transport);
        _ = await sahara.EnterCommandModeAsync(TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<SaharaException>(() =>
            sahara.NopAsync(TestContext.Current.CancellationToken));

        Assert.Contains("ExecuteResponse", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, transport.Writes.Count);
    }

    [Fact]
    public async Task ReadDebugDataAsync_TargetReportsPayloadAboveOneMebibyte_ThrowsBeforeBuffering()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(BuildHello(SaharaMode.Command));
        transport.Enqueue(BuildCommandReady());
        transport.Enqueue(BuildExecuteResponse(EdlConstants.SaharaExecCmdReadDebugData, 1024 * 1024 + 4));
        var sahara = new SaharaProtocol(transport);
        _ = await sahara.EnterCommandModeAsync(TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<SaharaException>(() =>
            sahara.ReadDebugDataAsync(TestContext.Current.CancellationToken));

        Assert.Contains("exec response command or length is invalid", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, transport.Writes.Count);
    }

    [Fact]
    public async Task GetCommandIdListAsync_TargetEchoesAnotherCommand_ThrowsBeforeReadingPayload()
    {
        using var transport = new ScriptedCommTransport();
        transport.Enqueue(BuildHello(SaharaMode.Command));
        transport.Enqueue(BuildCommandReady());
        transport.Enqueue(BuildExecuteResponse(EdlConstants.SaharaExecCmdReadDebugData, 8));
        var sahara = new SaharaProtocol(transport);
        _ = await sahara.EnterCommandModeAsync(TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<SaharaException>(() =>
            sahara.GetCommandIdListAsync(TestContext.Current.CancellationToken));

        Assert.Contains("exec response command or length is invalid", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, transport.Writes.Count);
    }

    private static Func<byte[], IEnumerable<byte[]>> RouteCommandModeDevice(uint execCommand, byte[] payload)
        => write =>
        {
            if (write.Length == 48
                && BinaryPrimitives.ReadUInt32LittleEndian(write) == (uint)SaharaCommand.HelloResponse)
            {
                return [BuildCommandReady()];
            }

            if (write.Length == 12)
            {
                var command = BinaryPrimitives.ReadUInt32LittleEndian(write);
                if (command == (uint)SaharaCommand.Execute)
                {
                    Assert.Equal(execCommand, BinaryPrimitives.ReadUInt32LittleEndian(write.AsSpan(8)));
                    return [BuildExecuteResponse(execCommand, (uint)payload.Length)];
                }

                Assert.Equal((uint)SaharaCommand.ExecuteData, command);
                Assert.Equal(execCommand, BinaryPrimitives.ReadUInt32LittleEndian(write.AsSpan(8)));
                return payload.Length == 0 ? [] : [payload];
            }

            return [];
        };

    private static void AssertExecRequest(byte[] packet, SaharaCommand command, uint execCommand)
    {
        Assert.Equal(12, packet.Length);
        Assert.Equal((uint)command, BinaryPrimitives.ReadUInt32LittleEndian(packet));
        Assert.Equal(12u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)));
        Assert.Equal(execCommand, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8)));
    }

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

    private static byte[] BuildFirehoseXmlHello()
    {
        var bytes = new byte[48];
        "<?xml version=\"1.0\" encoding=\"UTF-8\" ?>"u8.CopyTo(bytes);
        return bytes;
    }

    private static byte[] BuildCommandReady() => BuildPacket((uint)SaharaCommand.CommandReady, 8);

    private static byte[] BuildPacket(uint command, uint length)
    {
        var bytes = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, command);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), length);
        return bytes;
    }

    private static byte[] BuildExecuteResponse(uint echoedCommand, uint payloadLength)
    {
        var bytes = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)SaharaCommand.ExecuteResponse);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), echoedCommand);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), payloadLength);
        return bytes;
    }

    private static byte[] BuildPattern(int length, byte seed)
    {
        var bytes = new byte[length];
        for (var index = 0; index < length; index++)
        {
            bytes[index] = (byte)((index * 31 + seed) % 251);
        }

        return bytes;
    }
}