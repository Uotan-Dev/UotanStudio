using System.Buffers.Binary;
using System.Text;
using UotanStudio.Comm.Protocols.Bootrom;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Usb.Abstractions;
using UotanStudio.Hash.RockchipCrc32;
using Xunit;

namespace UotanStudio.Tests.Protocols;

public sealed class RkdlModeClassificationTests
{
    private const ushort RockchipVid = 0x2207;

    [Theory]
    [InlineData(0x350B, 0x0200)]
    [InlineData(0x350B, 0x0210)]
    [InlineData(0x320A, 0x0200)]
    [InlineData(0x281A, 0x0110)]
    public void ClassifyMode_ClearLoaderBit_ReportsMaskrom(ushort productId, ushort bcdUsb)
        => Assert.Equal(RkdlDeviceMode.Maskrom, RkdlConstants.ClassifyMode(productId, bcdUsb));

    [Theory]
    [InlineData(0x350B, 0x0201)]
    [InlineData(0x350B, 0x0303)]
    [InlineData(0x320A, 0x0211)]
    public void ClassifyMode_SetLoaderBit_ReportsLoader(ushort productId, ushort bcdUsb)
        => Assert.Equal(RkdlDeviceMode.Loader, RkdlConstants.ClassifyMode(productId, bcdUsb));

    [Fact]
    public void ClassifyMode_MissingBcdUsb_ReportsUnknownInsteadOfGuessing()
    {
        Assert.Equal(RkdlDeviceMode.Unknown, RkdlConstants.ClassifyMode(0x350B, null));
        Assert.Equal(RkdlDeviceMode.Unknown, RkdlConstants.ClassifyMode(0x320A, null));
    }

    [Theory]
    [InlineData(0x0007)]
    [InlineData(0x00FF)]
    public void ClassifyMode_ProductOutsideBootromRange_ReportsUnknown(ushort productId)
        => Assert.Equal(RkdlDeviceMode.Unknown, RkdlConstants.ClassifyMode(productId, 0x0200));

    [Theory]
    [InlineData(0x350B)]
    [InlineData(0x320A)]
    [InlineData(0x281A)]
    public void IsBootromProduct_KnownProductId_ReturnsTrue(ushort productId)
        => Assert.True(RkdlConstants.IsBootromProduct(productId));

    [Fact]
    public void IsBootromProduct_NonBootromProductId_ReturnsFalse()
    {
        Assert.False(RkdlConstants.IsBootromProduct(0x0007));
        Assert.False(RkdlConstants.IsBootromProduct(0x00FF));
    }

    [Fact]
    public void Mode_SessionOnMaskromDevice_ReportsMaskromOnly()
    {
        var session = CreateSession(bcdUsb: 0x0200);

        Assert.Equal(RkdlDeviceMode.Maskrom, session.Mode);
        Assert.True(session.IsMaskromMode);
        Assert.False(session.IsRockusbMode);
        Assert.True(session.IsRockchipDevice());
    }

    [Fact]
    public void Mode_SessionOnLoaderDevice_ReportsLoaderOnly()
    {
        var session = CreateSession(bcdUsb: 0x0201);

        Assert.Equal(RkdlDeviceMode.Loader, session.Mode);
        Assert.True(session.IsRockusbMode);
        Assert.False(session.IsMaskromMode);
    }

    [Fact]
    public void Mode_SessionWithoutDescriptorValue_ClaimsNeitherStage()
    {
        var session = CreateSession(bcdUsb: null);

        Assert.Equal(RkdlDeviceMode.Unknown, session.Mode);
        Assert.False(session.IsMaskromMode);
        Assert.False(session.IsRockusbMode);
    }

    [Fact]
    public void ReadStorage_OnMaskromDevice_ThrowsInsteadOfWaitingForCsw()
    {
        var fake = new RecordingUsbSession(0x350B, 0x0200);
        using var session = new RkdlSession(new UsbDeviceTransport(fake));

        var error = Assert.Throws<RkdlException>(() => session.ReadStorageMask());

        Assert.Contains("mask ROM", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fake.WriteCalls);
    }

    [Fact]
    public void ReadCapability_OnMaskromDevice_SendsTheInformationCommand()
    {
        var fake = new RecordingUsbSession(0x350B, 0x0200);
        using var session = new RkdlSession(new UsbDeviceTransport(fake));

        Assert.ThrowsAny<RkdlException>(() => session.ReadCapability());

        Assert.Equal(1, fake.WriteCalls);
    }

    [Fact]
    public void ReadFlashId_OnMaskromDevice_SendsTheInformationCommand()
    {
        var fake = new RecordingUsbSession(0x350B, 0x0200);
        using var session = new RkdlSession(new UsbDeviceTransport(fake));

        Assert.ThrowsAny<RkdlException>(() => session.ReadFlashId());

        Assert.Equal(1, fake.WriteCalls);
    }

    [Fact]
    public void ReadStorage_WithoutDescriptorValue_SendsTheCommandLikeTheOfficialTool()
    {
        var fake = new RecordingUsbSession(0x350B, null);
        using var session = new RkdlSession(new UsbDeviceTransport(fake));

        Assert.Throws<RkdlException>(() => session.ReadStorageMask());
        Assert.Equal(1, fake.WriteCalls);
    }

    [Fact]
    public async Task DownloadBootLoaderAsync_WithoutDescriptorValue_AttemptsTheVendorRequest()
    {
        var fake = new RecordingUsbSession(0x350B, null);
        using var session = new RkdlSession(new UsbDeviceTransport(fake));

        var error = await Assert.ThrowsAsync<RkdlException>(() =>
            session.DownloadBootLoaderAsync(CreateLoaderImage(), TestContext.Current.CancellationToken));

        Assert.DoesNotContain("loader mode", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(fake.ControlTransferCalls > 0);
    }

    [Fact]
    public async Task DownloadBootLoaderAsync_OnLoaderDevice_RefusesBeforeAnyControlTransfer()
    {
        var fake = new RecordingUsbSession(0x350B, 0x0201);
        using var session = new RkdlSession(new UsbDeviceTransport(fake));

        var error = await Assert.ThrowsAsync<RkdlException>(() =>
            session.DownloadBootLoaderAsync(CreateLoaderImage(), TestContext.Current.CancellationToken));

        Assert.Contains("already reports loader mode", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fake.ControlTransferCalls);
    }

    [Fact]
    public void UpgradeLoader_OnLoaderDevice_RefusesBeforeAnyBulkWrite()
    {
        var fake = new RecordingUsbSession(0x350B, 0x0201);
        using var session = new RkdlSession(new UsbDeviceTransport(fake));

        var error = Assert.Throws<RkdlException>(() => session.UpgradeLoader(CreateLoaderImage()));

        Assert.Contains("loader mode", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fake.WriteCalls);
    }

    [Fact]
    public void UpgradeLoader_OnLoaderDevice_ChecksTheStageBeforeParsingTheImage()
    {
        var fake = new RecordingUsbSession(0x350B, 0x0201);
        using var session = new RkdlSession(new UsbDeviceTransport(fake));

        var error = Assert.Throws<RkdlException>(() => session.UpgradeLoader([1, 2, 3]));

        Assert.DoesNotContain("too small", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fake.WriteCalls);
    }

    [Fact]
    public void ResetDevice_DefaultSubCodeWithoutReadableCsw_ReportsSuccess()
    {
        var fake = new RecordingUsbSession(0x350B, 0x0201);
        using var session = new RkdlSession(new UsbDeviceTransport(fake));

        Assert.Equal(0, session.ResetDevice());
        Assert.Equal(1, fake.WriteCalls);
    }

    [Fact]
    public void ResetDevice_ExplicitSubCodeWithoutReadableCsw_StillFailsClosed()
    {
        var fake = new RecordingUsbSession(0x350B, 0x0201);
        using var session = new RkdlSession(new UsbDeviceTransport(fake));

        Assert.Throws<RkdlException>(() => session.ResetDevice(RkdlResetSubCode.ResetMaskRom));
    }

    [Fact]
    public async Task DeviceRequest_OnLoaderDevice_RejectsBeforeAnyControlTransfer()
    {
        var fake = new RecordingUsbSession(0x350B, 0x0201);
        using var session = new RkdlSession(new UsbDeviceTransport(fake));

        var error = await Assert.ThrowsAsync<RkdlException>(() => session.DownloadBootLoader471([1, 2, 3], TestContext.Current.CancellationToken));

        Assert.Contains("mask ROM only", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fake.ControlTransferCalls);
    }

    [Theory]
    [InlineData(0x301A, "RK3036")]
    [InlineData(0x310C, "RK3128")]
    [InlineData(0x320A, "RK3288")]
    [InlineData(0x320B, "RK3229")]
    [InlineData(0x320C, "RK3328")]
    [InlineData(0x330A, "RK3368")]
    [InlineData(0x330C, "RK3399")]
    [InlineData(0x350A, "RK3566")]
    [InlineData(0x41303233, "RK3288")]
    [InlineData(0x33353041, "RK3566")]
    [InlineData(0x33353838, "RK3588")]
    [InlineData(0x38383533, "RK3588")]
    [InlineData(0x42303533, "350B")]
    public void GetChipName_KnownOrTagChipId_ReturnsName(uint chipId, string expected)
        => Assert.Equal(expected, RkChipInfo.GetChipName(chipId));

    private static RkdlSession CreateSession(ushort? bcdUsb)
        => new(new UsbDeviceTransport(new RecordingUsbSession(0x350B, bcdUsb)));

    /// <summary>
    /// Builds a minimal synthetic RKBOOT image: one first-stage entry, one second-stage entry, padded to the
    /// 1024-byte parse minimum, with the trailing Rockchip CRC-32.
    /// <para>构建最小的合成 RKBOOT 镜像：一个一级条目与一个二级条目，填充到 1024 字节解析下限，
    /// 并附带末尾的瑞芯微 CRC-32。</para>
    /// </summary>
    internal static byte[] CreateLoaderImage()
    {
        const int headerSize = 102;
        const int entrySize = 57;
        const int count471 = 1;
        const int count472 = 1;
        byte[] payload471 = [1, 2, 3, 4];
        byte[] payload472 = [5, 6, 7];
        var tableBytes = (count471 + count472) * entrySize;
        var payloadBytes = payload471.Length + payload472.Length;
        var image = new byte[Math.Max(headerSize + tableBytes + payloadBytes + 4, 1024)];

        Encoding.ASCII.GetBytes("BOOT").CopyTo(image, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(4), headerSize);
        image[25] = count471;
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(26), headerSize);
        image[30] = entrySize;
        image[31] = count472;
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(32), headerSize + (count471 * entrySize));
        image[36] = entrySize;

        var dataOffset = headerSize + tableBytes;
        Span<byte> entry471 = image.AsSpan(headerSize, entrySize);
        entry471[0] = entrySize;
        BinaryPrimitives.WriteUInt32LittleEndian(entry471[1..], 1u);
        Encoding.ASCII.GetBytes("ddr").CopyTo(entry471[5..]);
        BinaryPrimitives.WriteUInt32LittleEndian(entry471[45..], (uint)dataOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(entry471[49..], (uint)payload471.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(entry471[53..], 1u);
        payload471.CopyTo(image, dataOffset);

        Span<byte> entry472 = image.AsSpan(headerSize + (count471 * entrySize), entrySize);
        entry472[0] = entrySize;
        BinaryPrimitives.WriteUInt32LittleEndian(entry472[1..], 2u);
        Encoding.ASCII.GetBytes("usbplug").CopyTo(entry472[5..]);
        BinaryPrimitives.WriteUInt32LittleEndian(entry472[45..], (uint)(dataOffset + payload471.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(entry472[49..], (uint)payload472.Length);
        payload472.CopyTo(image, dataOffset + payload471.Length);

        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(image.Length - 4),
            RockchipCrc32.Compute(image.AsSpan(0, image.Length - 4)));
        return image;
    }

    private sealed class RecordingUsbSession(ushort productId, ushort? bcdUsb) : IUsbDeviceSession
    {
        public int WriteCalls { get; private set; }

        public int ControlTransferCalls { get; private set; }

        public int DefaultTimeoutMs => 1000;

        public UsbDeviceInfo DeviceInfo { get; } = new()
        {
            VendorId = RockchipVid,
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

        public byte[] Read(int length) => new byte[length];

        public byte[] Read(int length, int timeoutMs) => new byte[length];

        public int ReadInto(byte[] buffer, int offset, int length) => length;

        public int ReadInto(byte[] buffer, int offset, int length, int timeoutMs) => length;

        public int ReadInto(Span<byte> buffer, int timeoutMs) => buffer.Length;

        public UsbReadResult ReadPacket(byte[] buffer, int offset, int length, int timeoutMs)
            => new(length, false, false);

        public long Write(byte[] data, int length) => Write(data, 0, length, DefaultTimeoutMs);

        public long Write(byte[] data, int length, int timeoutMs) => Write(data, 0, length, timeoutMs);

        public long Write(byte[] data, int offset, int length, int timeoutMs)
        {
            WriteCalls++;
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

        public Task<UsbReadResult> ReadInterruptAsync(byte endpointAddress, byte[] buffer, int offset, int length,
            int timeoutMs, CancellationToken cancellationToken = default)
            => Task.FromResult(new UsbReadResult(0, false, false));

        public Task<long> WriteInterruptAsync(byte endpointAddress, byte[] data, int offset, int length, int timeoutMs,
            CancellationToken cancellationToken = default) => Task.FromResult<long>(length);

        public Task ResetAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}