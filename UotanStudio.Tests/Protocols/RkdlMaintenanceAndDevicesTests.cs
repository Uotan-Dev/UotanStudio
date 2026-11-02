using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Bootrom;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using Xunit;

namespace UotanStudio.Tests.Protocols;

public sealed class RkdlMaintenanceAndDevicesTests
{
    [Fact]
    public void TestBadBlock_Cbw_MatchesTheOfficialWireLayout()
    {
        var cbw = new RkdlCbw(RkdlCommand.TestBadBlock, RkdlFrame.DirectionIn,
            RkdlConstants.TestBadBlockResultLength, RkdlFrame.CbwcbLengthLong,
            address: 0x01020304u, sectorLength: 512, flashCs: 2);

        var bytes = cbw.ToBytes();

        Assert.Equal((byte)RkdlCommand.TestBadBlock, bytes[15]);
        Assert.Equal(0, bytes[16]);
        Assert.Equal(0x01020304u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(17, 4)));
        Assert.Equal(512, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(22, 2)));
        Assert.Equal(2, bytes[13]);
        Assert.Equal(RkdlConstants.TestBadBlockResultLength, (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4)));
        Assert.Equal(RkdlFrame.DirectionIn, bytes[12]);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(255)]
    public void TestBadBlock_FlashChipOutsideOfficialRange_IsRefused(byte flashChip)
    {
        var fake = new CountingUsbSession(0x350B, 0x0201);
        using var session = new RkdlSession(new UsbDeviceTransport(fake));

        Assert.Throws<RkdlException>(() => session.TestBadBlock(flashChip, 0, 1));
        Assert.Equal(0, fake.WriteCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(513)]
    public void TestBadBlock_BlockCountOutsideOfficialRange_IsRefused(ushort blockCount)
    {
        var fake = new CountingUsbSession(0x350B, 0x0201);
        using var session = new RkdlSession(new UsbDeviceTransport(fake));

        Assert.Throws<RkdlException>(() => session.TestBadBlock(0, 0, blockCount));
        Assert.Equal(0, fake.WriteCalls);
    }

    [Theory]
    [InlineData(21)]
    [InlineData(0)]
    public void ReadSector_SectorCountOutsideOfficialBound_IsRefused(int sectorCount)
    {
        var fake = new CountingUsbSession(0x350B, 0x0201);
        using var session = new RkdlSession(new UsbDeviceTransport(fake));

        Assert.Throws<RkdlException>(() => session.ReadSector(0, sectorCount));
        Assert.Equal(0, fake.WriteCalls);
    }

    [Fact]
    public void ReadSector_Cbw_CarriesTheOfficial528ByteSectorTransfer()
    {
        var cbw = new RkdlCbw(RkdlCommand.ReadSector, RkdlFrame.DirectionIn,
            2 * RkdlConstants.SectorWithSpareSize, RkdlFrame.CbwcbLengthLong,
            address: 100, sectorLength: 2);

        var bytes = cbw.ToBytes();

        Assert.Equal((byte)RkdlCommand.ReadSector, bytes[15]);
        Assert.Equal(2 * RkdlConstants.SectorWithSpareSize, (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4)));
        Assert.Equal(100u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(17, 4)));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(22, 2)));
    }

    [Fact]
    public void WriteSdram_Cbw_CarriesTheVendorSubCodeAndPayloadLength()
    {
        var cbw = new RkdlCbw(RkdlCommand.WriteSdram, RkdlFrame.DirectionOut, 4096,
            RkdlFrame.CbwcbLengthLong, subCode: RkdlConstants.SdramVendorSubCode, sectorLength: 4096);

        var bytes = cbw.ToBytes();

        Assert.Equal((byte)RkdlCommand.WriteSdram, bytes[15]);
        Assert.Equal(RkdlConstants.SdramVendorSubCode, bytes[16]);
        Assert.Equal(4096, (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4)));
        Assert.Equal(4096, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(22, 2)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(RkdlConstants.TransferChunkSize + 1)]
    public void WriteSdram_PayloadOutsideOneCommand_IsRefused(int length)
    {
        var fake = new CountingUsbSession(0x350B, 0x0200);
        using var session = new RkdlSession(new UsbDeviceTransport(fake));

        Assert.Throws<RkdlException>(() => session.WriteSdram(new byte[length]));
        Assert.Equal(0, fake.WriteCalls);
    }

    [Fact]
    public void ExecuteSdram_Cbw_CarriesTheVendorSubCodeWithoutDataPhase()
    {
        var cbw = new RkdlCbw(RkdlCommand.ExecuteSdram, RkdlFrame.DirectionOut, 0,
            RkdlFrame.CbwcbLengthLong, subCode: RkdlConstants.SdramVendorSubCode);

        var bytes = cbw.ToBytes();

        Assert.Equal((byte)RkdlCommand.ExecuteSdram, bytes[15]);
        Assert.Equal(RkdlConstants.SdramVendorSubCode, bytes[16]);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4)));
        Assert.Equal(RkdlFrame.CbwcbLengthLong, bytes[14]);
    }

    [Fact]
    public void WriteNewEfuse_Cbw_AddressesTheEfuseByIndex()
    {
        var cbw = new RkdlCbw(RkdlCommand.WriteNewEfuse, RkdlFrame.DirectionOut, 8,
            RkdlFrame.CbwcbLengthLong, subCode: 1, address: 0x1234, sectorLength: 8);

        var bytes = cbw.ToBytes();

        Assert.Equal((byte)RkdlCommand.WriteNewEfuse, bytes[15]);
        Assert.Equal(1, bytes[16]);
        Assert.Equal(0x1234u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(17, 4)));
        Assert.Equal(8, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(22, 2)));
    }

    [Fact]
    public void ReadNewEfuse_Cbw_AddressesTheEfuseByIndex()
    {
        var cbw = new RkdlCbw(RkdlCommand.ReadNewEfuse, RkdlFrame.DirectionIn, 16,
            RkdlFrame.CbwcbLengthLong, address: 0x5678, sectorLength: 16);

        var bytes = cbw.ToBytes();

        Assert.Equal((byte)RkdlCommand.ReadNewEfuse, bytes[15]);
        Assert.Equal(0x5678u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(17, 4)));
        Assert.Equal(16, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(22, 2)));
    }

    [Theory]
    [InlineData("wipe")]
    [InlineData("")]
    [InlineData("WIPE ")]
    public void MaintenanceErase_WrongConfirmationPhrase_IsRefusedBeforeAnyWrite(string confirmationPhrase)
    {
        var fake = new CountingUsbSession(0x350B, 0x0201);
        using var session = new RkdlSession(new UsbDeviceTransport(fake));

        var cancellationToken = TestContext.Current.CancellationToken;
        Assert.Throws<UnauthorizedAccessException>(() => session.EraseSystemDiskBySubCode(confirmationPhrase, cancellationToken: cancellationToken));
        Assert.Throws<UnauthorizedAccessException>(() => session.LowerFormatBySubCode(confirmationPhrase, cancellationToken: cancellationToken));
        Assert.Throws<UnauthorizedAccessException>(() => session.EraseUserDataBySubCode(confirmationPhrase, cancellationToken: cancellationToken));
        Assert.Equal(0, fake.WriteCalls);
    }

    [Theory]
    [InlineData(RkdlTestUnitSubCode.EraseSystem)]
    [InlineData(RkdlTestUnitSubCode.LowerFormat)]
    [InlineData(RkdlTestUnitSubCode.EraseUserData)]
    public void MaintenanceErase_ConfirmedPhrase_SendsTestUnitReadyWithTheSubCode(byte subCode)
    {
        var fake = new CountingUsbSession(0x350B, 0x0201);
        using var session = new RkdlSession(new UsbDeviceTransport(fake));

        switch (subCode)
        {
            case RkdlTestUnitSubCode.EraseSystem:
                session.EraseSystemDiskBySubCode("WIPE", cancellationToken: TestContext.Current.CancellationToken);
                break;
            case RkdlTestUnitSubCode.LowerFormat:
                session.LowerFormatBySubCode("WIPE", cancellationToken: TestContext.Current.CancellationToken);
                break;
            default:
                session.EraseUserDataBySubCode("WIPE", cancellationToken: TestContext.Current.CancellationToken);
                break;
        }

        Assert.Equal(1, fake.WriteCalls);
        Assert.Equal((byte)RkdlCommand.TestUnitReady, fake.LastCbw[15]);
        Assert.Equal(subCode, fake.LastCbw[16]);
    }

    [Fact]
    public void MaintenanceErase_CompletesAfterTheDeviceReportsReady()
    {
        var fake = new CountingUsbSession(0x350B, 0x0201) { CswStatus = 1 };
        using var session = new RkdlSession(new UsbDeviceTransport(fake));

        var error = Assert.Throws<RkdlException>(() => session.EraseSystemDiskBySubCode("WIPE",
            timeoutMs: 2500, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(RkdlError.DeviceUnready, error.ErrorCode);
        Assert.Contains("did not complete", error.Message, StringComparison.Ordinal);
        Assert.True(fake.WriteCalls >= 2);
    }

    [Fact]
    public void GetUserSectorMap_SuccessfulCsw_ReturnsTrue()
    {
        var fake = new CountingUsbSession(0x350B, 0x0201);
        using var session = new RkdlSession(new UsbDeviceTransport(fake));

        Assert.True(session.GetUserSectorMap());
        Assert.Equal((byte)RkdlCommand.TestUnitReady, fake.LastCbw[15]);
        Assert.Equal(RkdlTestUnitSubCode.GetUserSector, fake.LastCbw[16]);
    }

    [Fact]
    public void ReadSector_OneSector_ReadsTheOfficial528BytePayload()
    {
        var fake = new CountingUsbSession(0x350B, 0x0201);
        using var session = new RkdlSession(new UsbDeviceTransport(fake));

        var sectors = session.ReadSector(7, 1);

        Assert.Equal(RkdlConstants.SectorWithSpareSize, sectors.Length);
        Assert.Equal((byte)RkdlCommand.ReadSector, fake.LastCbw[15]);
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32BigEndian(fake.LastCbw.AsSpan(17, 4)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(fake.LastCbw.AsSpan(22, 2)));
    }

    [Fact]
    public void TestBadBlock_ValidArguments_SendsTheOfficialRequest()
    {
        var fake = new CountingUsbSession(0x350B, 0x0201);
        using var session = new RkdlSession(new UsbDeviceTransport(fake));

        var result = session.TestBadBlock(3, 0x40, 512);

        Assert.Equal(RkdlConstants.TestBadBlockResultLength, result.Length);
        Assert.Equal((byte)RkdlCommand.TestBadBlock, fake.LastCbw[15]);
        Assert.Equal(3, fake.LastCbw[13]);
        Assert.Equal(0x40u, BinaryPrimitives.ReadUInt32BigEndian(fake.LastCbw.AsSpan(17, 4)));
        Assert.Equal(512, BinaryPrimitives.ReadUInt16BigEndian(fake.LastCbw.AsSpan(22, 2)));
    }

    [Fact]
    public void WriteSdram_OnePayload_SendsTheVendorSubCode()
    {
        var fake = new CountingUsbSession(0x350B, 0x0200);
        using var session = new RkdlSession(new UsbDeviceTransport(fake));

        session.WriteSdram(new byte[64]);

        Assert.Equal((byte)RkdlCommand.WriteSdram, fake.LastCbw[15]);
        Assert.Equal(RkdlConstants.SdramVendorSubCode, fake.LastCbw[16]);
        Assert.Equal(64, BinaryPrimitives.ReadUInt16BigEndian(fake.LastCbw.AsSpan(22, 2)));
    }

    [Fact]
    public void WriteSpiFlash_Payload_SendsAddressAndLength()
    {
        var fake = new CountingUsbSession(0x350B, 0x0201);
        using var session = new RkdlSession(new UsbDeviceTransport(fake));

        session.WriteSpiFlash(0x1000, new byte[32]);

        Assert.Equal((byte)RkdlCommand.WriteSpiFlash, fake.LastCbw[15]);
        Assert.Equal(0x1000u, BinaryPrimitives.ReadUInt32BigEndian(fake.LastCbw.AsSpan(17, 4)));
        Assert.Equal(32, BinaryPrimitives.ReadUInt16BigEndian(fake.LastCbw.AsSpan(22, 2)));
    }

    private sealed class CountingUsbSession(ushort productId, ushort? bcdUsb) : IUsbDeviceSession
    {
        private uint _lastCbwTag;

        public int WriteCalls { get; private set; }

        public int ControlTransferCalls { get; private set; }

        public byte[] LastCbw { get; private set; } = [];

        public byte CswStatus { get; set; }

        public int DefaultTimeoutMs => 1000;

        public UsbDeviceInfo DeviceInfo { get; } = new()
        {
            VendorId = 0x2207,
            ProductId = productId,
            BcdUsb = bcdUsb,
            SerialNumber = "10000030",
            DevicePath = "test",
        };

        public byte EndpointIn => 0x81;

        public byte EndpointOut => 0x01;

        public void Dispose()
        {
        }

        public byte[] Read(int length) => BuildReadBytes(length);

        public byte[] Read(int length, int timeoutMs) => BuildReadBytes(length);

        public int ReadInto(byte[] buffer, int offset, int length) => FillRead(buffer, offset, length);

        public int ReadInto(byte[] buffer, int offset, int length, int timeoutMs) => FillRead(buffer, offset, length);

        public int ReadInto(Span<byte> buffer, int timeoutMs)
        {
            if (buffer.Length == RkdlFrame.CswLength)
            {
                var scratch = new byte[buffer.Length];
                FillRead(scratch, 0, buffer.Length);
                scratch.CopyTo(buffer);
            }

            return buffer.Length;
        }

        private byte[] BuildReadBytes(int length)
        {
            var buffer = new byte[length];
            FillRead(buffer, 0, length);
            return buffer;
        }

        private int FillRead(byte[] buffer, int offset, int length)
        {
            if (length != RkdlFrame.CswLength)
            {
                return length;
            }

            buffer[offset + 0] = 0x55;
            buffer[offset + 1] = 0x53;
            buffer[offset + 2] = 0x42;
            buffer[offset + 3] = 0x53;
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 4, 4), _lastCbwTag);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 8, 4), 0);
            buffer[offset + 12] = CswStatus;
            return length;
        }

        public UsbReadResult ReadPacket(byte[] buffer, int offset, int length, int timeoutMs)
            => new(length, false, false);

        public long Write(byte[] data, int length) => Write(data, 0, length, DefaultTimeoutMs);

        public long Write(byte[] data, int length, int timeoutMs) => Write(data, 0, length, timeoutMs);

        public long Write(byte[] data, int offset, int length, int timeoutMs)
        {
            WriteCalls++;
            if (length == RkdlFrame.CbwLength)
            {
                LastCbw = data.AsSpan(offset, length).ToArray();
                _lastCbwTag = BinaryPrimitives.ReadUInt32LittleEndian(LastCbw.AsSpan(4, 4));
            }

            return length;
        }

        public void WriteZlp(int timeoutMs)
        {
        }

        public int ControlTransfer(UsbSetupPacket setupPacket, byte[]? buffer, int offset, int length, int timeoutMs)
        {
            ControlTransferCalls++;
            return 0;
        }

        public void SetInterfaceAltSetting(byte interfaceNumber, byte altSetting)
        {
        }

        public void SetConfiguration(byte configuration)
        {
        }

        public UsbReadResult ReadInterrupt(byte endpointAddress, byte[] buffer, int offset, int length, int timeoutMs)
            => new(0, false, false);

        public long WriteInterrupt(byte endpointAddress, byte[] data, int offset, int length, int timeoutMs) => length;

        public void Reset()
        {
        }

        public Task<byte[]> ReadAsync(int length, int timeoutMs, CancellationToken cancellationToken = default)
            => Task.FromResult(new byte[length]);

        public Task<int> ReadIntoAsync(byte[] buffer, int offset, int length, int timeoutMs,
            CancellationToken cancellationToken = default) => Task.FromResult(length);

        public Task<UsbReadResult> ReadPacketAsync(byte[] buffer, int offset, int length, int timeoutMs,
            CancellationToken cancellationToken = default) => Task.FromResult(new UsbReadResult(length, false, false));

        public Task<UsbReadResult> ReadPacketAsync(byte[] buffer, int offset, int length, int timeoutMs,
            IProgress<long>? progress, CancellationToken cancellationToken = default)
            => Task.FromResult(new UsbReadResult(length, false, false));

        public Task<long> WriteAsync(byte[] data, int length, int timeoutMs,
            CancellationToken cancellationToken = default) => WriteAsync(data, 0, length, timeoutMs, cancellationToken);

        public Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs,
            CancellationToken cancellationToken = default)
        {
            WriteCalls++;
            return Task.FromResult<long>(length);
        }

        public Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs, IProgress<long>? progress,
            CancellationToken cancellationToken = default) => WriteAsync(data, offset, length, timeoutMs, cancellationToken);

        public Task WriteZlpAsync(int timeoutMs, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<int> ControlTransferAsync(UsbSetupPacket setupPacket, byte[]? buffer, int offset, int length,
            int timeoutMs, CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task SetInterfaceAltSettingAsync(byte interfaceNumber, byte altSetting,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetConfigurationAsync(byte configuration, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<UsbReadResult> ReadInterruptAsync(byte endpointAddress, byte[] buffer, int offset, int length, int timeoutMs,
            CancellationToken cancellationToken = default) => Task.FromResult(new UsbReadResult(0, false, false));

        public Task<long> WriteInterruptAsync(byte endpointAddress, byte[] data, int offset, int length, int timeoutMs,
            CancellationToken cancellationToken = default) => Task.FromResult<long>(length);

        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}