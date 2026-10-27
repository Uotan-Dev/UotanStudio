using System.Buffers.Binary;
using System.Text;
using UotanStudio.Comm.Protocols.Diag;
using UotanStudio.Comm.Transports.Abstractions;

using Xunit;

namespace UotanStudio.Tests.Protocols;

/// <summary>
/// Loopback transport replaying scripted replies for DIAG session tests.
/// <para>为 DIAG 会话测试回放脚本应答的环回传输。</para>
/// </summary>
public sealed class ScriptedDiagTransport : ICommTransport
{
    private readonly Queue<byte[]> _replies = new();
    public List<byte[]> Requests { get; } = [];
    public bool IsConnected => true;

    public void Connect()
    {
    }

    public void Disconnect()
    {
    }

    public void Reset()
    {
    }

    public int Read(byte[] buffer, int offset, int length, int timeoutMs) => throw new NotSupportedException();

    public Task<int> ReadAsync(byte[] buffer, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
    {
        if (_replies.Count == 0)
        {
            throw new TimeoutException();
        }

        var reply = _replies.Dequeue();
        var copy = Math.Min(reply.Length, length);
        reply.AsSpan(0, copy).CopyTo(buffer.AsSpan(offset));
        return Task.FromResult(copy);
    }

    public long Write(byte[] data, int offset, int length, int timeoutMs) => throw new NotSupportedException();

    public Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
    {
        Requests.Add(data[offset..(offset + length)]);
        return Task.FromResult((long)length);
    }

    public Stream GetStream() => throw new NotSupportedException();

    public void Dispose()
    {
    }

    public void EnqueueReply(byte[] payload)
    {
        _replies.Enqueue(UotanStudio.Comm.Protocols.Edl.Streaming.DiagHdlcCodec.Encode(payload, withCrc: true));
    }
}

public class DiagNvPacketTests
{
    [Fact]
    public void EncodeReadRequest_ItemId_PrependsIdAndPad()
    {
        var payload = DiagNvPacket.EncodeReadRequest(0x1177);
        Assert.Equal(2 + DiagConstants.MaxNvDataBytes, payload.Length);
        Assert.Equal(0x1177, BinaryPrimitives.ReadUInt16LittleEndian(payload));
    }

    [Fact]
    public void EncodeWriteRequest_DataZeroPads()
    {
        var payload = DiagNvPacket.EncodeWriteRequest(10, [1, 2, 3]);
        Assert.Equal(2 + DiagConstants.MaxNvDataBytes, payload.Length);
        Assert.Equal(10, BinaryPrimitives.ReadUInt16LittleEndian(payload));
        Assert.Equal(1, payload[2]);
        Assert.Equal(0, payload[5]);
    }

    [Fact]
    public void EncodeWriteRequest_OversizedData_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DiagNvPacket.EncodeWriteRequest(1, new byte[DiagConstants.MaxNvDataBytes + 1]));
    }

    [Fact]
    public void DecodeReadResponse_StatusIdData()
    {
        var response = new byte[] { 0x00, 0x00, 0x0A, 0x00, 0xAA, 0xBB };
        var (status, itemId, data) = DiagNvPacket.DecodeReadResponse(response);
        Assert.Equal(0, status);
        Assert.Equal(10, itemId);
        Assert.Equal([0xAA, 0xBB], data);
    }
}

public class DiagSubsysPacketTests
{
    [Fact]
    public void EncodeRequest_CarriesSubsystemOpcodeAndBody()
    {
        var request = DiagSubsysPacket.EncodeRequest(DiagConstants.Efs2SubsystemId, DiagConstants.Efs2GetFile, [9, 9]);
        Assert.Equal(DiagConstants.SubsysDispatchRequest, request[0]);
        Assert.Equal(DiagConstants.Efs2SubsystemId, BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(1)));
        Assert.Equal(DiagConstants.Efs2GetFile, BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(3)));
        Assert.Equal([9, 9], request[5..]);
    }

    [Fact]
    public void EncodeEfsString_LengthPrefixAndNul()
    {
        var encoded = DiagSubsysPacket.EncodeEfsString("/nv");
        Assert.Equal((ushort)4, BinaryPrimitives.ReadUInt16LittleEndian(encoded));
        Assert.Equal("/nv\0", Encoding.UTF8.GetString(encoded[2..]));
    }

    [Fact]
    public void DecodeResponse_MismatchedCommand_Throws()
    {
        Assert.Throws<InvalidDataException>(() => DiagSubsysPacket.DecodeResponse([0x00, 0x00, 0x00, 0x00, 0x00]));
    }
}

public class DiagSessionTests
{
    [Fact]
    public async Task ReadNvItemAsync_SuccessReply_ReturnsData()
    {
        var transport = new ScriptedDiagTransport();
        transport.EnqueueReply([DiagConstants.NvReadResponse, 0x00, 0x00, 0x0A, 0x00, 0x11, 0x22]);
        using var session = new DiagSession(transport);
        var item = await session.ReadNvItemAsync(10, TestContext.Current.CancellationToken);
        Assert.Equal((ushort)10, item.Id);
        Assert.Equal([0x11, 0x22], item.Data);
        Assert.Single(transport.Requests);
        Assert.Equal(DiagConstants.NvReadRequest, transport.Requests[0][0]);
    }

    [Fact]
    public async Task ReadNvItemAsync_ErrorStatus_Throws()
    {
        var transport = new ScriptedDiagTransport();
        transport.EnqueueReply([DiagConstants.NvReadResponse, 0x05, 0x00, 0x0A, 0x00]);
        using var session = new DiagSession(transport);
        await Assert.ThrowsAsync<DiagException>(() => session.ReadNvItemAsync(10, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WriteNvItemAsync_WithoutConfirm_Throws()
    {
        using var session = new DiagSession(new ScriptedDiagTransport());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            session.WriteNvItemAsync(new DiagNvItem(10, [1]), confirm: false, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SendSpcAsync_NonDigits_Throws()
    {
        using var session = new DiagSession(new ScriptedDiagTransport());
        await Assert.ThrowsAsync<ArgumentException>(() => session.SendSpcAsync("12x456", confirm: true, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BackupQcnAsync_RejectedItems_SkipsAndBuildsQcn()
    {
        var transport = new ScriptedDiagTransport();
        transport.EnqueueReply([DiagConstants.NvReadResponse, 0x00, 0x00, 0x0A, 0x00, 0x01, 0x02]);
        transport.EnqueueReply([DiagConstants.NvReadResponse, 0x09, 0x00, 0x0B, 0x00]);
        var path = Path.Combine(Path.GetTempPath(), $"uotan-diag-{Guid.NewGuid():N}.qcn");
        try
        {
            using var session = new DiagSession(transport);
            var refused = await session.BackupQcnAsync(path, [10, 11], cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal([11], refused);
            var parsed = await UotanStudio.Firmware.Radio.Qcn.QcnReader.ParseAsync(path, TestContext.Current.CancellationToken);
            Assert.Single(parsed.Items);
            Assert.Equal((ushort)10, parsed.Items[0].Id);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RestoreQcnAsync_WithoutConfirm_Throws()
    {
        using var session = new DiagSession(new ScriptedDiagTransport());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            session.RestoreQcnAsync("does-not-matter.qcn", confirm: false, cancellationToken: TestContext.Current.CancellationToken));
    }
}