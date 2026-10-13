using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Bootrom;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Bootrom;

/// <summary>
/// RKDL WRITE_LBA / READ_LBA / WRITE_GPT tests driven by a simulated rockusb device. Short source
/// reads must not desynchronize the sector counter or write zero padding mid-image.
/// <para>由模拟 rockusb 设备驱动的 RKDL WRITE_LBA / READ_LBA / WRITE_GPT 测试。源流短读不得使扇区
/// 计数失步，也不得在镜像中段写入补零。</para>
/// </summary>
public class RkdlTransferTests
{
    private const int SectorSize = 512;

    private const byte OpcodeWriteLba = 0x15;

    private const byte OpcodeReadLba = 0x14;

    private const byte OpcodeEraseLba = 0x25;

    private const byte OpcodeEraseNormal = 0x06;

    private const byte OpcodeReadFlashInfo = 0x1A;

    [Fact]
    public void WriteLba_ShortReadingSourceStream_DeliversExactImageBytesInOneChunk()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var transport = new FakeRkdlDeviceTransport { FlashInfoSectorCount = 0x8000 };
        using var session = new RkdlSession(transport, timeoutMs: 5000);
        var image = BuildPatternedImage(4096);
        using var source = new ShortReadStream(image, maximumRead: 511);

        var written = session.WriteLBA(0x6000, source, image.Length, null, cancellationToken);

        Assert.Equal(image.Length, written);
        Assert.True(source.ReadCalls > image.Length / 511, $"expected repeated short reads, got {source.ReadCalls}");
        Assert.Equal(image, Assert.Single(transport.OutPayloads));

        var cbw = Assert.Single(transport.Cbws, c => c.Opcode == OpcodeWriteLba);
        Assert.Equal(0x6000u, cbw.Address);
        Assert.Equal(8, cbw.SectorLength);
        Assert.Equal((uint)image.Length, cbw.TransferLength);
    }

    [Fact]
    public void WriteLba_ShortReadsAcrossMultipleChunks_NeverZeroPadsAndNeverRepeatsSourceBytes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var transport = new FakeRkdlDeviceTransport();
        using var session = new RkdlSession(transport, timeoutMs: 5000);
        var chunkSize = RkdlConstants.TransferChunkSize;
        var image = BuildPatternedImage(chunkSize + 3000);
        using var source = new ShortReadStream(image, maximumRead: 511);

        var written = session.WriteLBA(0x10, source, image.Length, null, cancellationToken);

        Assert.Equal(image.Length, written);
        Assert.Equal(2, transport.OutPayloads.Count);
        Assert.Equal(chunkSize, transport.OutPayloads[0].Length);
        Assert.Equal(3072, transport.OutPayloads[1].Length);
        Assert.Equal(image[..chunkSize], transport.OutPayloads[0]);

        var expectedFinalChunk = new byte[3072];
        Array.Copy(image, chunkSize, expectedFinalChunk, 0, 3000);
        Assert.Equal(expectedFinalChunk, transport.OutPayloads[1]);

        var writes = transport.Cbws.Where(c => c.Opcode == OpcodeWriteLba).ToArray();
        Assert.Equal(2, writes.Length);
        Assert.Equal(0x10u, writes[0].Address);
        Assert.Equal(128, writes[0].SectorLength);
        Assert.Equal(0x10u + 128u, writes[1].Address);
        Assert.Equal(6, writes[1].SectorLength);
    }

    [Fact]
    public void WriteLba_FullyAlignedSourceStream_BehavesAsOneSectorMultipleChunk()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var transport = new FakeRkdlDeviceTransport { FlashInfoSectorCount = 0x8000 };
        using var session = new RkdlSession(transport, timeoutMs: 5000);
        var image = BuildPatternedImage(4096);
        using var source = new MemoryStream(image, writable: false);
        var progress = new SyncProgress<long>();

        var written = session.WriteLBA(0x6000, source, image.Length, progress, cancellationToken);

        Assert.Equal(image.Length, written);
        Assert.Equal([4096L], progress.Reports);
        Assert.Equal(image, Assert.Single(transport.OutPayloads));
        Assert.Equal(8, Assert.Single(transport.Cbws, c => c.Opcode == OpcodeWriteLba).SectorLength);
    }

    [Fact]
    public void WriteLba_TrailingPartialSector_ZeroPadsOnlyTheFinalChunk()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var transport = new FakeRkdlDeviceTransport();
        using var session = new RkdlSession(transport, timeoutMs: 5000);
        var image = BuildPatternedImage(SectorSize + 100);
        using var source = new MemoryStream(image, writable: false);

        var written = session.WriteLBA(0, source, image.Length, null, cancellationToken);

        Assert.Equal(image.Length, written);
        var payload = Assert.Single(transport.OutPayloads);
        Assert.Equal(2 * SectorSize, payload.Length);
        Assert.Equal(image, payload[..image.Length]);
        Assert.All(payload[image.Length..], b => Assert.Equal(0, b));
        Assert.Equal(2, Assert.Single(transport.Cbws, c => c.Opcode == OpcodeWriteLba).SectorLength);
    }

    [Fact]
    public void WriteLba_SourceEndsBeforeRequestedLength_ThrowsBeforeAnyDeviceWrite()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var transport = new FakeRkdlDeviceTransport();
        using var session = new RkdlSession(transport, timeoutMs: 5000);
        var image = BuildPatternedImage(2048);
        using var source = new MemoryStream(image, writable: false);

        var exception = Assert.Throws<EndOfStreamException>(
            () => session.WriteLBA(0, source, 4096, null, cancellationToken));

        Assert.Contains("2048/4096", exception.Message);
        Assert.DoesNotContain(transport.Cbws, c => c.Opcode == OpcodeWriteLba);
        Assert.Empty(transport.OutPayloads);
    }

    [Fact]
    public void WriteLba_NegativeLengthOrAddress_ThrowsWithoutTouchingTheDevice()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var transport = new FakeRkdlDeviceTransport();
        using var session = new RkdlSession(transport, timeoutMs: 5000);
        using var source = new MemoryStream(BuildPatternedImage(SectorSize), writable: false);

        Assert.Throws<RkdlException>(() => session.WriteLBA(-1, source, SectorSize, null, cancellationToken));
        Assert.Throws<RkdlException>(() => session.WriteLBA((long)uint.MaxValue + 1, source, SectorSize, null, cancellationToken));
        Assert.Throws<ArgumentNullException>(() => session.WriteLBA(0, null!, SectorSize, null, cancellationToken));
        Assert.Empty(transport.Cbws);
    }

    [Fact]
    public void WriteLba_FailingCswOnSecondChunk_ThrowsAndStopsTransferring()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var transport = new FakeRkdlDeviceTransport
        {
            CswStatusResolver = cbw => cbw.Opcode != OpcodeWriteLba || cbw.Address == 0x10 ? (byte)0 : (byte)1
        };
        using var session = new RkdlSession(transport, timeoutMs: 5000);
        var image = BuildPatternedImage(RkdlConstants.TransferChunkSize + SectorSize);
        using var source = new MemoryStream(image, writable: false);

        var exception = Assert.Throws<RkdlException>(
            () => session.WriteLBA(0x10, source, image.Length, null, cancellationToken));

        Assert.Equal(RkdlError.WriteFailed, exception.ErrorCode);
        Assert.Equal(2, transport.Cbws.Count(c => c.Opcode == OpcodeWriteLba));
        Assert.Equal(2, transport.OutPayloads.Count);
    }

    [Fact]
    public void WriteLba_DeadTransport_ThrowsTransferFailed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var transport = new FakeRkdlDeviceTransport { FailWrites = true };
        using var session = new RkdlSession(transport, timeoutMs: 50);
        using var source = new MemoryStream(BuildPatternedImage(SectorSize), writable: false);

        var exception = Assert.Throws<RkdlException>(
            () => session.WriteLBA(0, source, SectorSize, null, cancellationToken));

        Assert.Equal(RkdlError.TransferFailed, exception.ErrorCode);
    }

    [Fact]
    public void WriteLba_CancellationRequested_ThrowsBeforeSendingData()
    {
        var transport = new FakeRkdlDeviceTransport();
        using var session = new RkdlSession(transport, timeoutMs: 5000);
        using var source = new MemoryStream(BuildPatternedImage(RkdlConstants.TransferChunkSize), writable: false);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => session.WriteLBA(0, source, RkdlConstants.TransferChunkSize, null, cancelled.Token));
        Assert.Empty(transport.OutPayloads);
    }

    [Fact]
    public void ReadLba_FlashSliceLargerThanOneRead_FillsBufferAndTrimsToRequestedLength()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var flash = BuildPatternedImage(8192);
        var transport = new FakeRkdlDeviceTransport { FlashContents = flash };
        using var session = new RkdlSession(transport, timeoutMs: 5000);
        using var output = new MemoryStream();

        var read = session.ReadLBA(2, output, 1000, null, cancellationToken);

        Assert.Equal(1000, read);
        Assert.Equal(flash.AsSpan(2 * SectorSize, 1000).ToArray(), output.ToArray());
        var cbw = Assert.Single(transport.Cbws, c => c.Opcode == OpcodeReadLba);
        Assert.Equal(2u, cbw.Address);
        Assert.Equal(2, cbw.SectorLength);
        Assert.Equal((uint)(2 * SectorSize), cbw.TransferLength);
    }

    [Fact]
    public void ReadLba_SilentDevice_ThrowsReadFailed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var transport = new FakeRkdlDeviceTransport { GoSilentAfterCbwCount = 0 };
        using var session = new RkdlSession(transport, timeoutMs: 50);
        using var output = new MemoryStream();

        var exception = Assert.Throws<RkdlException>(
            () => session.ReadLBA(0, output, SectorSize, null, cancellationToken));

        Assert.Equal(RkdlError.ReadFailed, exception.ErrorCode);
    }

    [Fact]
    public void WriteGpt_ValidSectorCount_WritesMasterAtLbaZeroAndBackupAtTotalMinus33()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var transport = new FakeRkdlDeviceTransport { FlashInfoSectorCount = 0x2000 };
        using var session = new RkdlSession(transport, timeoutMs: 5000);
        var gpt = BuildGptImage();

        session.WriteGPT(gpt, null, cancellationToken);

        var writes = transport.Cbws.Where(c => c.Opcode == OpcodeWriteLba).ToArray();
        Assert.Equal(2, writes.Length);
        Assert.Equal(0u, writes[0].Address);
        Assert.Equal(34, writes[0].SectorLength);
        Assert.Equal(0x2000u - 33u, writes[1].Address);
        Assert.Equal(33, writes[1].SectorLength);
        Assert.Equal(34 * SectorSize, transport.OutPayloads[0].Length);
        Assert.Equal(33 * SectorSize, transport.OutPayloads[1].Length);
    }

    [Fact]
    public void WriteGpt_SectorCountEqualToBackupSectors_ThrowsWithoutAnyWriteLba()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var transport = new FakeRkdlDeviceTransport { FlashInfoSectorCount = 33 };
        using var session = new RkdlSession(transport, timeoutMs: 5000);
        var gpt = BuildGptImage();

        var exception = Assert.Throws<RkdlException>(() => session.WriteGPT(gpt, null, cancellationToken));

        Assert.Equal(RkdlError.FlashInfoFailed, exception.ErrorCode);
        Assert.DoesNotContain(transport.Cbws, c => c.Opcode == OpcodeWriteLba);
        Assert.Empty(transport.OutPayloads);
    }

    [Fact]
    public void WriteGpt_ZeroSectorCount_ThrowsAndReportsTheUntruncatedValue()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var transport = new FakeRkdlDeviceTransport { FlashInfoSectorCount = 0 };
        using var session = new RkdlSession(transport, timeoutMs: 5000);
        var gpt = BuildGptImage();

        var exception = Assert.Throws<RkdlException>(() => session.WriteGPT(gpt, null, cancellationToken));

        Assert.Equal(RkdlError.FlashInfoFailed, exception.ErrorCode);
        Assert.Contains("sector count: 0 ", exception.Message);
        Assert.DoesNotContain(transport.Cbws, c => c.Opcode == OpcodeWriteLba);
    }

    [Fact]
    public void WriteGpt_MaximumSectorCount_PlacesBackupAtLastUsableLba()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var transport = new FakeRkdlDeviceTransport { FlashInfoSectorCount = uint.MaxValue };
        using var session = new RkdlSession(transport, timeoutMs: 5000);
        var gpt = BuildGptImage();

        session.WriteGPT(gpt, null, cancellationToken);

        var writes = transport.Cbws.Where(c => c.Opcode == OpcodeWriteLba).ToArray();
        Assert.Equal(2, writes.Length);
        Assert.Equal(uint.MaxValue - 33u, writes[1].Address);
    }

    [Fact]
    public void WriteGpt_TruncatedFlashInfoPayload_ThrowsReadFailed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var transport = new FakeRkdlDeviceTransport { FlashInfoPayload = [0x00, 0x20, 0x00] };
        using var session = new RkdlSession(transport, timeoutMs: 5000);
        var gpt = BuildGptImage();

        var exception = Assert.Throws<RkdlException>(() => session.WriteGPT(gpt, null, cancellationToken));

        Assert.Equal(RkdlError.ReadFailed, exception.ErrorCode);
        Assert.DoesNotContain(transport.Cbws, c => c.Opcode == OpcodeWriteLba);
    }

    [Fact]
    public void WriteGpt_ImageSmallerThanPrimaryTable_ThrowsInvalidParameter()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var transport = new FakeRkdlDeviceTransport();
        using var session = new RkdlSession(transport, timeoutMs: 5000);

        var exception = Assert.Throws<RkdlException>(
            () => session.WriteGPT(new byte[33 * SectorSize], null, cancellationToken));

        Assert.Equal(RkdlError.InvalidParameter, exception.ErrorCode);
        Assert.Empty(transport.Cbws);
    }

    [Fact]
    public void UpdateDiskSize_SectorCountNotAboveBackupSectors_ThrowsArgumentOutOfRange()
    {
        var master = BuildGptImage();
        var backup = new byte[33 * SectorSize];

        Assert.Throws<ArgumentOutOfRangeException>(() => RkdlGpt.UpdateDiskSize(master, backup, 33));
    }

    [Fact]
    public void ReadFlashInfo_DeviceSectorCount_IsReportedAsBytesAndSectors()
    {
        var transport = new FakeRkdlDeviceTransport { FlashInfoSectorCount = 0x1000 };
        using var session = new RkdlSession(transport, timeoutMs: 5000);

        var info = session.ReadFlashInfo();

        Assert.Equal(0x1000 * (long)SectorSize, info.Size);
        Assert.Equal(SectorSize, info.SectorSize);
        Assert.Equal(0x1000, info.SectorCount);
        Assert.Equal(0x1A, Assert.Single(transport.Cbws).Opcode);
    }

    [Fact]
    public void ReadChipInfo_AsciiTagPayload_IsDecodedFromLittleEndianFields()
    {
        var transport = new FakeRkdlDeviceTransport
        {
            ChipInfoPayload = [0x41, 0x30, 0x32, 0x33, 0x34, 0x31, 0x30, 0x32, 0x33, 0x38, 0x31, 0x33, 0x56, 0x32, 0x30, 0x30]
        };
        using var session = new RkdlSession(transport, timeoutMs: 5000);

        var chip = session.ReadChipInfo();

        Assert.Equal("A023", chip.Tag);
        Assert.Equal(0x33323041u, chip.ChipId);
        Assert.Equal("RK32", chip.ChipName);
        Assert.Equal(0x32303134u, chip.Revision);
    }

    [Fact]
    public void ReadCapability_EightBytePayload_IsReturnedVerbatim()
    {
        var transport = new FakeRkdlDeviceTransport
        {
            CapabilityPayload = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08]
        };
        using var session = new RkdlSession(transport, timeoutMs: 5000);

        var capability = session.ReadCapability();

        Assert.Equal([0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08], capability);
        Assert.Equal(0xAA, Assert.Single(transport.Cbws).Opcode);
    }

    [Fact]
    public void EraseLba_BlocksAboveUShortRange_ThrowsWithoutDeviceTraffic()
    {
        var transport = new FakeRkdlDeviceTransport();
        using var session = new RkdlSession(transport, timeoutMs: 5000);

        Assert.Throws<RkdlException>(() => session.EraseLBA(0, ushort.MaxValue + 1));
        Assert.Empty(transport.Cbws);
    }

    [Fact]
    public void EraseBlocks_MoreThanOneCommandWorth_SplitsIntoSixteenBlockChunks()
    {
        var transport = new FakeRkdlDeviceTransport();
        using var session = new RkdlSession(transport, timeoutMs: 5000);

        session.EraseBlocks(8, 40);

        var erases = transport.Cbws.Where(c => c.Opcode == OpcodeEraseNormal).ToArray();
        Assert.Equal(3, erases.Length);
        Assert.Equal([8u, 24u, 40u], erases.Select(c => c.Address).ToArray());
        Assert.Equal([16, 16, 8], erases.Select(c => c.SectorLength).ToArray());
        Assert.Equal(1, transport.Cbws.Count(c => c.Opcode == OpcodeReadFlashInfo));
    }

    [Fact]
    public void TestDeviceReady_ResidueProgress_DecodesCurrentFromHighHalfAndTotalFromLowHalf()
    {
        var transport = new FakeRkdlDeviceTransport { CswDataResidue = 0x0300_1000 };
        using var session = new RkdlSession(transport, timeoutMs: 5000);

        var ready = session.TestDeviceReady(out var current, out var total);

        Assert.True(ready);
        Assert.Equal((ushort)3, current);
        Assert.Equal((ushort)16, total);
        Assert.Equal(0x00, Assert.Single(transport.Cbws).Opcode);
    }

    [Fact]
    public void WriteLba_RangeBeyondDeviceGeometry_ThrowsWithoutWriteLbaCommand()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var transport = new FakeRkdlDeviceTransport();
        using var session = new RkdlSession(transport, timeoutMs: 5000);
        using var source = new MemoryStream(BuildPatternedImage(8192), writable: false);

        var exception = Assert.Throws<RkdlException>(
            () => session.WriteLBA(0x1FF8, source, 8192, null, cancellationToken));

        Assert.Equal(RkdlError.InvalidParameter, exception.ErrorCode);
        Assert.DoesNotContain(transport.Cbws, c => c.Opcode == OpcodeWriteLba);
        Assert.Empty(transport.OutPayloads);
    }

    [Fact]
    public void ReadLba_RangeBeyondDeviceGeometry_ThrowsWithoutReadLbaCommand()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var transport = new FakeRkdlDeviceTransport { FlashContents = BuildPatternedImage(8192) };
        using var session = new RkdlSession(transport, timeoutMs: 5000);
        using var output = new MemoryStream();

        var exception = Assert.Throws<RkdlException>(
            () => session.ReadLBA(0x1FF8, output, 8192, null, cancellationToken));

        Assert.Equal(RkdlError.InvalidParameter, exception.ErrorCode);
        Assert.DoesNotContain(transport.Cbws, c => c.Opcode == OpcodeReadLba);
        Assert.Empty(output.ToArray());
    }

    [Fact]
    public void EraseLba_RangeBeyondDeviceGeometry_ThrowsWithoutEraseLbaCommand()
    {
        var transport = new FakeRkdlDeviceTransport();
        using var session = new RkdlSession(transport, timeoutMs: 5000);

        var exception = Assert.Throws<RkdlException>(() => session.EraseLBA(0x1FF0, 32));

        Assert.Equal(RkdlError.InvalidParameter, exception.ErrorCode);
        Assert.DoesNotContain(transport.Cbws, c => c.Opcode == OpcodeEraseLba);
    }

    [Fact]
    public void WriteLba_UnusableFlashGeometry_FailsClosedWithFlashInfoFailed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var transport = new FakeRkdlDeviceTransport { FlashInfoSectorCount = 0 };
        using var session = new RkdlSession(transport, timeoutMs: 5000);
        using var source = new MemoryStream(BuildPatternedImage(SectorSize), writable: false);

        var exception = Assert.Throws<RkdlException>(
            () => session.WriteLBA(0, source, SectorSize, null, cancellationToken));

        Assert.Equal(RkdlError.FlashInfoFailed, exception.ErrorCode);
        Assert.DoesNotContain(transport.Cbws, c => c.Opcode == OpcodeWriteLba);
    }

    [Fact]
    public void ReadLba_UnusableFlashGeometry_FailsClosedWithFlashInfoFailed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var transport = new FakeRkdlDeviceTransport { FlashInfoSectorCount = 0 };
        using var session = new RkdlSession(transport, timeoutMs: 5000);
        using var output = new MemoryStream();

        var exception = Assert.Throws<RkdlException>(
            () => session.ReadLBA(0, output, SectorSize, null, cancellationToken));

        Assert.Equal(RkdlError.FlashInfoFailed, exception.ErrorCode);
        Assert.DoesNotContain(transport.Cbws, c => c.Opcode == OpcodeReadLba);
    }

    [Fact]
    public void EraseLba_UnusableFlashGeometry_FailsClosedWithFlashInfoFailed()
    {
        var transport = new FakeRkdlDeviceTransport { FlashInfoSectorCount = 0 };
        using var session = new RkdlSession(transport, timeoutMs: 5000);

        var exception = Assert.Throws<RkdlException>(() => session.EraseLBA(0, 1));

        Assert.Equal(RkdlError.FlashInfoFailed, exception.ErrorCode);
        Assert.DoesNotContain(transport.Cbws, c => c.Opcode == OpcodeEraseLba);
    }

    [Fact]
    public void WriteLba_PartialTrailingSectorPastLastUsableSector_RoundsUpAndThrows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var transport = new FakeRkdlDeviceTransport();
        using var session = new RkdlSession(transport, timeoutMs: 5000);
        using var source = new MemoryStream(BuildPatternedImage(7 * SectorSize + 1), writable: false);

        var exception = Assert.Throws<RkdlException>(
            () => session.WriteLBA(0x1FF9, source, 7 * SectorSize + 1, null, cancellationToken));

        Assert.Equal(RkdlError.InvalidParameter, exception.ErrorCode);
        Assert.DoesNotContain(transport.Cbws, c => c.Opcode == OpcodeWriteLba);
    }

    [Fact]
    public void WriteLba_LengthEndingExactlyAtLastUsableSector_IsAccepted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var transport = new FakeRkdlDeviceTransport();
        using var session = new RkdlSession(transport, timeoutMs: 5000);
        var image = BuildPatternedImage(7 * SectorSize);
        using var source = new MemoryStream(image, writable: false);

        var written = session.WriteLBA(0x1FF9, source, image.Length, null, cancellationToken);

        Assert.Equal(image.Length, written);
        var cbw = Assert.Single(transport.Cbws, c => c.Opcode == OpcodeWriteLba);
        Assert.Equal(0x1FF9u, cbw.Address);
        Assert.Equal(7, cbw.SectorLength);
    }

    [Fact]
    public void ReadLba_PartialTrailingByteRoundsUpPastGeometry_ThrowsWithoutReadLbaCommand()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var transport = new FakeRkdlDeviceTransport { FlashContents = BuildPatternedImage(8192) };
        using var session = new RkdlSession(transport, timeoutMs: 5000);
        using var output = new MemoryStream();

        var exception = Assert.Throws<RkdlException>(
            () => session.ReadLBA(0x1FF9, output, 7 * SectorSize + 1, null, cancellationToken));

        Assert.Equal(RkdlError.InvalidParameter, exception.ErrorCode);
        Assert.DoesNotContain(transport.Cbws, c => c.Opcode == OpcodeReadLba);
    }

    [Fact]
    public void WriteLba_ConsecutiveTransfers_QueryFlashGeometryOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var transport = new FakeRkdlDeviceTransport();
        using var session = new RkdlSession(transport, timeoutMs: 5000);
        using var first = new MemoryStream(BuildPatternedImage(SectorSize), writable: false);
        using var second = new MemoryStream(BuildPatternedImage(SectorSize), writable: false);

        session.WriteLBA(0, first, SectorSize, null, cancellationToken);
        session.WriteLBA(1, second, SectorSize, null, cancellationToken);

        Assert.Equal(1, transport.Cbws.Count(c => c.Opcode == OpcodeReadFlashInfo));
        Assert.Equal(2, transport.Cbws.Count(c => c.Opcode == OpcodeWriteLba));
    }

    private static byte[] BuildPatternedImage(int length)
    {
        var image = new byte[length];
        for (var i = 0; i < image.Length; i++)
        {
            image[i] = (byte)((i * 31) + (i / 251));
        }

        return image;
    }

    private static byte[] BuildGptImage()
    {
        var gpt = new byte[34 * SectorSize];
        BinaryPrimitives.WriteUInt32LittleEndian(gpt.AsSpan(SectorSize + 12), 92);
        BinaryPrimitives.WriteUInt32LittleEndian(gpt.AsSpan(SectorSize + 80), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(gpt.AsSpan(SectorSize + 32), 0x1000);
        BinaryPrimitives.WriteUInt64LittleEndian(gpt.AsSpan(SectorSize + 48), 0x1000 - 34);
        return gpt;
    }

    private sealed class ShortReadStream(byte[] bytes, int maximumRead) : MemoryStream(bytes, writable: false)
    {
        private int _calls;

        public int ReadCalls => _calls;

        public override int Read(byte[] buffer, int offset, int count)
        {
            _calls++;
            return base.Read(buffer, offset, Math.Min(count, 1 + (_calls % maximumRead)));
        }
    }
}