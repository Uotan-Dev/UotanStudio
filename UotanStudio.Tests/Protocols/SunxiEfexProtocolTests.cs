using System.Buffers.Binary;
using System.Text;
using UotanStudio.Comm.Protocols.Sunxi;
using Xunit;

namespace UotanStudio.Tests.Protocols;

public sealed class SunxiEfexProtocolTests
{
    private const int MaximumTransfer = 64 * 1024;
    private const int UbootWorkModeOffset = 224;

    [Fact]
    public async Task FelSession_Write_SplitsAtSixtyFourKibibytes()
    {
        var transport = new FakeTransport();
        transport.QueueFelChunk();
        transport.QueueFelChunk();
        var session = new SunxiFelSession(transport);
        var payload = new byte[(MaximumTransfer * 1) + 4464];

        await session.WriteAsync(0x40000000, payload, TestContext.Current.CancellationToken);

        Assert.Equal(10, transport.Writes.Count);
        var firstHeader = transport.Writes[0];
        Assert.Equal(32, firstHeader.Length);
        Assert.Equal("AWUC", Encoding.ASCII.GetString(firstHeader, 0, 4));
        Assert.Equal(16u, BinaryPrimitives.ReadUInt32LittleEndian(firstHeader.AsSpan(8)));
        Assert.Equal(0x0C, firstHeader[15]);
        Assert.Equal(0x12, firstHeader[16]);

        var firstRequest = transport.Writes[1];
        Assert.Equal(16, firstRequest.Length);
        Assert.Equal(0x0101, BinaryPrimitives.ReadUInt16LittleEndian(firstRequest));
        Assert.Equal(0x40000000u, BinaryPrimitives.ReadUInt32LittleEndian(firstRequest.AsSpan(4)));
        Assert.Equal((uint)MaximumTransfer, BinaryPrimitives.ReadUInt32LittleEndian(firstRequest.AsSpan(8)));
        Assert.Equal((uint)MaximumTransfer, (uint)transport.Writes[3].Length);

        var secondRequest = transport.Writes[6];
        Assert.Equal(0x40010000u, BinaryPrimitives.ReadUInt32LittleEndian(secondRequest.AsSpan(4)));
        Assert.Equal(4464u, BinaryPrimitives.ReadUInt32LittleEndian(secondRequest.AsSpan(8)));
    }

    [Fact]
    public async Task FelSession_Execute_SendsOnlyTheRequestAndStatus()
    {
        var transport = new FakeTransport();
        transport.QueueResponse();
        transport.QueueStatus();
        transport.QueueResponse();
        var session = new SunxiFelSession(transport);

        await session.ExecuteAsync(0x00010000, TestContext.Current.CancellationToken);

        Assert.Equal(3, transport.Writes.Count);
        Assert.Equal(0x0102, BinaryPrimitives.ReadUInt16LittleEndian(transport.Writes[1]));
        Assert.Equal(0x00010000u, BinaryPrimitives.ReadUInt32LittleEndian(transport.Writes[1].AsSpan(4)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(transport.Writes[1].AsSpan(8)));
    }

    [Fact]
    public async Task FelSession_Open_SendsReadStageHeaderBeforeIdentity()
    {
        var transport = new FakeTransport();
        transport.QueueHandshake(SunxiDeviceMode.Fel, 0x00168000);
        var session = new SunxiFelSession(transport);

        await session.OpenAsync(TestContext.Current.CancellationToken);

        Assert.Equal(4, transport.Writes.Count);
        AssertReadStage(transport.Writes[2], 32);
        AssertReadStage(transport.Writes[3], 8);
    }

    [Fact]
    public async Task FelSession_Open_RejectsDeviceThatIsNotInFelMode()
    {
        var transport = new FakeTransport();
        transport.QueueHandshake(SunxiDeviceMode.Fes, 0x00168000);
        var session = new SunxiFelSession(transport);

        await Assert.ThrowsAsync<SunxiEfexException>(() =>
            session.OpenAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FelSession_Open_ReturnsIdentityAndChipFamily()
    {
        var transport = new FakeTransport();
        transport.QueueHandshake(SunxiDeviceMode.Fel, 0x00168000);
        var session = new SunxiFelSession(transport);

        var identity = await session.OpenAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0x00168000u, identity.Id);
        Assert.Equal(SunxiDeviceMode.Fel, identity.Mode);
        Assert.Equal("H2/H3", identity.ChipName);
    }

    [Fact]
    public async Task FelSession_Read_SendsReadStageForThePayload()
    {
        var transport = new FakeTransport();
        transport.QueueReadChunk(16);
        var session = new SunxiFelSession(transport);
        var buffer = new byte[16];

        await session.ReadAsync(0x2000, buffer, TestContext.Current.CancellationToken);

        Assert.Equal(4, transport.Writes.Count);
        Assert.Equal(0x0103, BinaryPrimitives.ReadUInt16LittleEndian(transport.Writes[1]));
        Assert.Equal(16u, BinaryPrimitives.ReadUInt32LittleEndian(transport.Writes[1].AsSpan(8)));
        AssertReadStage(transport.Writes[2], 16);
        AssertReadStage(transport.Writes[3], 8);
    }

    [Fact]
    public async Task FesSession_Download_AdvancesSectorAddressAndMarksLastChunk()
    {
        var transport = new FakeTransport();
        transport.QueueResponses(2);
        var session = new SunxiFesSession(transport);
        var payload = new byte[MaximumTransfer + 4464];

        await session.DownloadAsync(0, payload, SunxiFesDataTag.Flash, TestContext.Current.CancellationToken);

        Assert.Equal(4, transport.Writes.Count);
        var firstFrame = transport.Writes[0];
        Assert.Equal(20, firstFrame.Length);
        Assert.Equal(0x0206, BinaryPrimitives.ReadUInt16LittleEndian(firstFrame));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(firstFrame.AsSpan(4)));
        Assert.Equal((uint)MaximumTransfer, BinaryPrimitives.ReadUInt32LittleEndian(firstFrame.AsSpan(8)));
        Assert.Equal((uint)SunxiFesDataTag.Flash, BinaryPrimitives.ReadUInt32LittleEndian(firstFrame.AsSpan(12)));
        Assert.Equal("AWUC", Encoding.ASCII.GetString(firstFrame, 16, 4));

        var secondFrame = transport.Writes[2];
        Assert.Equal((uint)(MaximumTransfer / 512), BinaryPrimitives.ReadUInt32LittleEndian(secondFrame.AsSpan(4)));
        Assert.Equal(4464u, BinaryPrimitives.ReadUInt32LittleEndian(secondFrame.AsSpan(8)));
        Assert.Equal((uint)SunxiFesDataTag.Flash | SunxiEfexConstants.TransferFinishTag,
            BinaryPrimitives.ReadUInt32LittleEndian(secondFrame.AsSpan(12)));
    }

    [Fact]
    public async Task FesSession_Download_AdvancesByteAddressForPartitionTags()
    {
        var transport = new FakeTransport();
        transport.QueueResponses(2);
        var session = new SunxiFesSession(transport);
        var payload = new byte[1024];

        await session.DownloadAsync(0x2000, payload, SunxiFesDataTag.Preboot, TestContext.Current.CancellationToken);

        var frame = transport.Writes[0];
        Assert.Equal(2, transport.Writes.Count);
        Assert.Equal(0x2000u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(4)));
        Assert.Equal(1024u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(8)));
        Assert.Equal((uint)SunxiFesDataTag.Preboot | SunxiEfexConstants.TransferFinishTag,
            BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(12)));
    }

    [Fact]
    public async Task FesSession_Upload_SendsFesUpAndMarksLastChunk()
    {
        var transport = new FakeTransport();
        transport.QueueUploadChunk(MaximumTransfer);
        transport.QueueUploadChunk(8);
        var session = new SunxiFesSession(transport);
        var buffer = new byte[MaximumTransfer + 8];

        await session.UploadAsync(4, buffer, SunxiFesDataTag.Flash, TestContext.Current.CancellationToken);

        Assert.Equal(2, transport.Writes.Count);
        var firstFrame = transport.Writes[0];
        Assert.Equal(0x0207, BinaryPrimitives.ReadUInt16LittleEndian(firstFrame));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(firstFrame.AsSpan(4)));
        Assert.Equal((uint)SunxiFesDataTag.Flash, BinaryPrimitives.ReadUInt32LittleEndian(firstFrame.AsSpan(12)));
        var lastFrame = transport.Writes[1];
        Assert.Equal(4u + MaximumTransfer / 512u, BinaryPrimitives.ReadUInt32LittleEndian(lastFrame.AsSpan(4)));
        Assert.Equal((uint)SunxiFesDataTag.Flash | SunxiEfexConstants.TransferFinishTag,
            BinaryPrimitives.ReadUInt32LittleEndian(lastFrame.AsSpan(12)));
    }

    [Fact]
    public async Task FesSession_Erase_SendsEraseTagAndRequestWord()
    {
        var transport = new FakeTransport();
        transport.QueueResponse();
        var session = new SunxiFesSession(transport);

        await session.EraseAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, transport.Writes.Count);
        var frame = transport.Writes[0];
        Assert.Equal(0x0206, BinaryPrimitives.ReadUInt16LittleEndian(frame));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(4)));
        Assert.Equal(16u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(8)));
        Assert.Equal((uint)SunxiFesDataTag.Erase | SunxiEfexConstants.TransferFinishTag,
            BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(12)));
        Assert.Equal(0x12u, BinaryPrimitives.ReadUInt32LittleEndian(transport.Writes[1]));
        Assert.Equal(16, transport.Writes[1].Length);
    }

    [Fact]
    public async Task FesSession_QueryStorage_ReturnsFourByteValue()
    {
        var transport = new FakeTransport();
        transport.QueueValue(8);
        transport.QueueResponse();
        var session = new SunxiFesSession(transport);

        var storage = await session.QueryStorageAsync(TestContext.Current.CancellationToken);

        Assert.Equal(8u, storage);
        Assert.Equal(0x0209, BinaryPrimitives.ReadUInt16LittleEndian(transport.Writes[0]));
    }

    [Fact]
    public async Task FesSession_VerifyValue_ReportsFlagAndCrcs()
    {
        var transport = new FakeTransport();
        transport.QueueVerify(SunxiEfexConstants.CrcValidFlag, 0x1234, 0x5678);
        transport.QueueResponse();
        var session = new SunxiFesSession(transport);

        var result = await session.VerifyValueAsync(0x40, 4096, TestContext.Current.CancellationToken);

        Assert.True(result.IsCrcValid);
        Assert.False(result.IsValueMatch);
        Assert.Equal(0x1234, result.FesCrc);
        Assert.Equal(0x5678, result.MediaCrc);
        var frame = transport.Writes[0];
        Assert.Equal(0x020C, BinaryPrimitives.ReadUInt16LittleEndian(frame));
        Assert.Equal(0x40u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(4)));
        Assert.Equal(4096ul, BinaryPrimitives.ReadUInt64LittleEndian(frame.AsSpan(8)));
    }

    [Fact]
    public async Task FesSession_VerifyStatus_ReadsZeroMediaWordAsSuccess()
    {
        var transport = new FakeTransport();
        transport.QueueVerify(SunxiEfexConstants.CrcValidFlag, 0x1111, 0);
        transport.QueueResponse();
        var session = new SunxiFesSession(transport);

        var result = await session.VerifyStatusAsync(0x7F08, TestContext.Current.CancellationToken);

        Assert.True(result.IsStatusOk);
        var frame = transport.Writes[0];
        Assert.Equal(0x020D, BinaryPrimitives.ReadUInt16LittleEndian(frame));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(4)));
        Assert.Equal(0x7F08u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(12)));
    }

    [Fact]
    public void FesVerifyResult_WithoutTheValidFlag_NeverReportsSuccess()
    {
        var result = new SunxiFesVerifyResult(0x01, 0, 0);

        Assert.False(result.IsCrcValid);
        Assert.False(result.IsValueMatch);
        Assert.False(result.IsStatusOk);
    }

    [Fact]
    public async Task FesSession_SetFlash_SendsStorageTypeAndDirection()
    {
        var transport = new FakeTransport();
        transport.QueueResponse();
        transport.QueueResponse();
        var session = new SunxiFesSession(transport);

        await session.SetFlashAsync(2, on: true, TestContext.Current.CancellationToken);
        await session.SetFlashAsync(2, on: false, TestContext.Current.CancellationToken);

        Assert.Equal(0x020A, BinaryPrimitives.ReadUInt16LittleEndian(transport.Writes[0]));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(transport.Writes[0].AsSpan(4)));
        Assert.Equal(0x020B, BinaryPrimitives.ReadUInt16LittleEndian(transport.Writes[1]));
    }

    [Fact]
    public async Task FesSession_SetToolMode_CarriesBothModes()
    {
        var transport = new FakeTransport();
        transport.QueueResponse();
        var session = new SunxiFesSession(transport);

        await session.SetToolModeAsync(SunxiToolMode.Normal, SunxiToolMode.Reboot,
            TestContext.Current.CancellationToken);

        var frame = transport.Writes[0];
        Assert.Equal(0x020F, BinaryPrimitives.ReadUInt16LittleEndian(frame));
        Assert.Equal((uint)SunxiToolMode.Normal, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(4)));
        Assert.Equal((uint)SunxiToolMode.Reboot, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(8)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(12)));
    }

    [Fact]
    public async Task Session_NonZeroStatus_ThrowsWithThatStatus()
    {
        var transport = new FakeTransport();
        transport.QueueValue(0);
        transport.QueueResponse(status: 0x05);
        var session = new SunxiFesSession(transport);

        var exception = await Assert.ThrowsAsync<SunxiEfexException>(() =>
            session.QueryStorageAsync(TestContext.Current.CancellationToken));

        Assert.Equal(0x05, exception.Status);
    }

    [Fact]
    public void Frames_ReadResponse_RejectsForeignMagic()
    {
        var response = new byte[13];
        "AWUX"u8.CopyTo(response);

        Assert.Throws<InvalidDataException>(() => SunxiEfexFrames.ReadResponse(response));
    }

    [Fact]
    public void Frames_ReadIdentity_RejectsForeignMagic()
    {
        var identity = new byte[32];

        Assert.Throws<InvalidDataException>(() => SunxiEfexFrames.ReadIdentity(identity));
    }

    [Fact]
    public void Frames_WriteFesTransfer_RejectsPayloadLargerThanTwelveBytes()
    {
        var frame = new byte[20];

        Assert.Throws<ArgumentException>(() => SunxiEfexFrames.WriteFesTransfer(frame, SunxiEfexCommand.FesDown,
            new byte[13]));
    }

    [Theory]
    [InlineData(0x00168000u, "H2/H3")]
    [InlineData(0x00189000u, "A523/A527/MR527/T527")]
    [InlineData(0x00185900u, "D1/F133")]
    [InlineData(0x00188600u, "V851/V853")]
    public void ChipTable_ResolvesKnownSoCIds(uint id, string expected)
    {
        Assert.Equal(expected, SunxiChipTable.Resolve(id));
    }

    [Fact]
    public void ChipTable_UnknownIdResolvesToNull()
    {
        Assert.Null(SunxiChipTable.Resolve(0x00123456));
        Assert.True(SunxiChipTable.Count >= 26);
    }

    [Fact]
    public void BootImageHeader_ReadsEgonFields()
    {
        var image = BuildEgonImage(0x43000000, 0x40000000, 0x0000A000, "sun50iw9");

        var header = SunxiBootImageHeader.ReadEgonBoot(image);

        Assert.Equal(0x43000000u, header.ReturnAddress);
        Assert.Equal(0x40000000u, header.RunAddress);
        Assert.Equal(0x0000A000u, header.Length);
        Assert.Equal("sun50iw9", header.Platform);
    }

    [Fact]
    public void BootImageHeader_ReadsUbootFields()
    {
        var image = BuildUbootImage(0x4A000000, 0x00080000, "sun50iw9");

        var header = SunxiBootImageHeader.ReadUboot(image);

        Assert.Equal(0x4A000000u, header.RunAddress);
        Assert.Equal(0x00080000u, header.Length);
        Assert.Equal("sun50iw9", header.Platform);
    }

    [Fact]
    public void BootImageHeader_RejectsAnImageWithTheOtherMagic()
    {
        var image = BuildUbootImage(0x4A000000, 0x100, "sun50iw9");

        Assert.Throws<InvalidDataException>(() => SunxiBootImageHeader.ReadEgonBoot(image));
    }

    [Fact]
    public async Task Handoff_InitialiseDram_ClearsParametersRunsAndReadsStatus()
    {
        var transport = new FakeTransport();
        transport.QueueFelChunk();
        transport.QueueFelChunk();
        transport.QueueResponse();
        transport.QueueStatus();
        transport.QueueResponse();
        transport.QueueResponse();
        transport.QueuePayload(new byte[SunxiFesHandoff.DramParameterLength]);
        transport.QueueResponse();
        transport.QueueStatus();
        transport.QueueResponse();
        var session = new SunxiFelSession(transport);
        var handoff = new SunxiFesHandoff(_ => throw new NotSupportedException(), TimeSpan.Zero, TimeSpan.Zero);

        var status = await handoff.InitialiseDramAsync(session,
            BuildEgonImage(0x43000000, 0x40000000, 0x100, "sun50iw9"), TestContext.Current.CancellationToken);

        Assert.False(status.InitialisationFailed);
        Assert.False(status.Updated);
        Assert.Equal(136, SunxiFesHandoff.DramParameterLength);
        Assert.Equal(0x43000000u, BinaryPrimitives.ReadUInt32LittleEndian(transport.Writes[1].AsSpan(4)));
        Assert.Equal(136u, BinaryPrimitives.ReadUInt32LittleEndian(transport.Writes[1].AsSpan(8)));
        Assert.Equal(0x40000000u, BinaryPrimitives.ReadUInt32LittleEndian(transport.Writes[6].AsSpan(4)));
    }

    [Fact]
    public async Task Handoff_InitialiseDram_ReportsTheLoadersFailureFlag()
    {
        var transport = new FakeTransport();
        transport.QueueFelChunk();
        transport.QueueFelChunk();
        transport.QueueResponse();
        transport.QueueStatus();
        transport.QueueResponse();
        transport.QueueResponse();
        var parameters = new byte[SunxiFesHandoff.DramParameterLength];
        BinaryPrimitives.WriteUInt32LittleEndian(parameters, 1);
        transport.QueuePayload(parameters);
        transport.QueueResponse();
        transport.QueueStatus();
        transport.QueueResponse();
        var session = new SunxiFelSession(transport);
        var handoff = new SunxiFesHandoff(_ => throw new NotSupportedException(), TimeSpan.Zero, TimeSpan.Zero);

        await Assert.ThrowsAsync<SunxiEfexException>(() => handoff.InitialiseDramAsync(session,
            BuildEgonImage(0x43000000, 0x40000000, 0x100, "sun50iw9"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Handoff_StartService_SetsWorkModeAndRetriesUntilHandshakeAnswers()
    {
        var transport = new FakeTransport();
        transport.QueueFelChunk();
        transport.QueueResponse();
        transport.QueueStatus();
        transport.QueueResponse();
        var session = new SunxiFelSession(transport);
        var attempts = 0;
        var service = new SunxiFesSession(new FakeTransport());
        var handoff = new SunxiFesHandoff(_ =>
        {
            attempts++;
            return attempts < 3 ? throw new IOException("not ready") : Task.FromResult(service);
        }, TimeSpan.Zero, TimeSpan.Zero);

        var result = await handoff.StartServiceAsync(session, BuildUbootImage(0x4A000000, 0x200, "sun50iw9"), 3,
            TestContext.Current.CancellationToken);

        Assert.Same(service, result);
        Assert.Equal(3, attempts);
        Assert.Equal(SunxiFesHandoff.UsbProductWorkMode,
            BinaryPrimitives.ReadInt32LittleEndian(transport.Writes[3].AsSpan(UbootWorkModeOffset)));
        Assert.Equal(0x4A000000u, BinaryPrimitives.ReadUInt32LittleEndian(transport.Writes[1].AsSpan(4)));
    }

    [Fact]
    public async Task Handoff_StartService_ToleratesAFailedExecutionStatus()
    {
        var transport = new FakeTransport();
        transport.QueueFelChunk();
        transport.QueueResponse(status: 0x04);
        var session = new SunxiFelSession(transport);
        var service = new SunxiFesSession(new FakeTransport());
        var handoff = new SunxiFesHandoff(_ => Task.FromResult(service), TimeSpan.Zero, TimeSpan.Zero);

        var result = await handoff.StartServiceAsync(session, BuildUbootImage(0x4A000000, 0x200, "sun50iw9"), 1,
            TestContext.Current.CancellationToken);

        Assert.Same(service, result);
    }

    [Fact]
    public async Task Handoff_StartService_FailsWhenEveryAttemptStaysSilent()
    {
        var transport = new FakeTransport();
        transport.QueueFelChunk();
        transport.QueueResponse();
        transport.QueueStatus();
        transport.QueueResponse();
        var session = new SunxiFelSession(transport);
        var handoff = new SunxiFesHandoff(_ => throw new TimeoutException("no device"), TimeSpan.Zero,
            TimeSpan.Zero);

        await Assert.ThrowsAsync<TimeoutException>(() => handoff.StartServiceAsync(session,
            BuildUbootImage(0x4A000000, 0x200, "sun50iw9"), 2, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeviceSession_ExposesFelOnlyWhenTheDeviceReportsFel()
    {
        var transport = new FakeTransport();
        transport.QueueHandshake(SunxiDeviceMode.Fel, 0x00168000);

        var session = await SunxiDeviceSession.OpenAsync(transport, TestContext.Current.CancellationToken);

        Assert.True(session.IsFel);
        Assert.False(session.IsFes);
        Assert.Equal("H2/H3", session.Identity.ChipName);
        Assert.Throws<InvalidOperationException>(() => session.Fes);
    }

    [Fact]
    public async Task DeviceSession_ExposesFesOnlyWhenTheDeviceReportsFes()
    {
        var transport = new FakeTransport();
        transport.QueueHandshake(SunxiDeviceMode.Fes, 0x00189000);

        var session = await SunxiDeviceSession.OpenAsync(transport, TestContext.Current.CancellationToken);

        Assert.True(session.IsFes);
        Assert.Throws<InvalidOperationException>(() => session.Fel);
    }

    private static void AssertReadStage(byte[] header, int length)
    {
        Assert.Equal(32, header.Length);
        Assert.Equal("AWUC", Encoding.ASCII.GetString(header, 0, 4));
        Assert.Equal((uint)length, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)));
        Assert.Equal(0x11, header[16]);
    }

    private static byte[] BuildEgonImage(uint returnAddress, uint runAddress, uint length, string platform)
    {
        var image = new byte[64];
        "eGON.BT0"u8.CopyTo(image.AsSpan(4));
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16), length);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(28), returnAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(32), runAddress);
        Encoding.ASCII.GetBytes(platform).CopyTo(image.AsSpan(40));
        return image;
    }

    private static byte[] BuildUbootImage(uint runAddress, uint length, string platform)
    {
        var image = new byte[512];
        "u-boot\0\0"u8.CopyTo(image.AsSpan(4));
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20), length);
        Encoding.ASCII.GetBytes(platform).CopyTo(image.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(44), runAddress);
        return image;
    }

    private sealed class FakeTransport : ISunxiEfexTransport
    {
        private readonly Queue<byte[]> _reads = new();

        public List<byte[]> Writes { get; } = [];

        public void QueueHandshake(SunxiDeviceMode mode, uint id)
        {
            QueueResponse();
            QueueIdentity(mode, id);
            QueueResponse();
            QueueStatus();
            QueueResponse();
        }

        public void QueueResponses(int count)
        {
            for (var index = 0; index < count; index++)
            {
                QueueResponse();
            }
        }

        public void QueueFelChunk()
        {
            QueueResponse();
            QueueResponse();
            QueueStatus();
            QueueResponse();
        }

        public void QueueReadChunk(int payloadLength)
        {
            QueueResponse();
            QueuePayload(new byte[payloadLength]);
            QueueResponse();
            QueueStatus();
            QueueResponse();
        }

        public void QueueUploadChunk(int payloadLength)
        {
            QueuePayload(new byte[payloadLength]);
            QueueResponse();
        }

        public void QueueResponse(byte status = 0)
        {
            var response = new byte[13];
            "AWUS"u8.CopyTo(response);
            response[12] = status;
            _reads.Enqueue(response);
        }

        public void QueueStatus()
        {
            _reads.Enqueue(new byte[8]);
        }

        public void QueuePayload(byte[] payload)
        {
            _reads.Enqueue(payload);
        }

        public void QueueIdentity(SunxiDeviceMode mode, uint id)
        {
            var identity = new byte[32];
            SunxiEfexConstants.IdentityMagic.CopyTo(identity);
            BinaryPrimitives.WriteUInt32LittleEndian(identity.AsSpan(8), id);
            BinaryPrimitives.WriteUInt32LittleEndian(identity.AsSpan(12), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(identity.AsSpan(16), (ushort)mode);
            _reads.Enqueue(identity);
        }

        public void QueueValue(uint value)
        {
            var payload = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(payload, value);
            _reads.Enqueue(payload);
        }

        public void QueueVerify(uint flag, int fesCrc, int mediaCrc)
        {
            var payload = new byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(payload, flag);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), fesCrc);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(8), mediaCrc);
            _reads.Enqueue(payload);
        }

        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken,
            int? timeoutMilliseconds = null)
        {
            Writes.Add(data.ToArray());
            return Task.CompletedTask;
        }

        public Task<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken,
            int? timeoutMilliseconds = null)
        {
            Assert.NotEmpty(_reads);
            var next = _reads.Dequeue();
            Assert.Equal(buffer.Length, next.Length);
            next.CopyTo(buffer);
            return Task.FromResult(next.Length);
        }
    }
}