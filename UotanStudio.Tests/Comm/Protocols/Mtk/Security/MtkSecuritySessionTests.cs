using System.Buffers.Binary;
using System.Security.Cryptography;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Protocols.Mtk.Security;
using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.Tests.Firmware;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed partial class MtkSessionTests
{
    private const string SecCfgClosedHeaderHex = "4D4D4D4D040000003C00000001000000000000000000000045454545";
    private const string SecCfgClosedHashHex = "BE83A6C7969908D7A61D11E93A3984A17A970C722F6318C3371DA3A52F038F44";
    private const string KeyHex = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";
    private const long SecCfgPartitionOffset = 20480;
    private const int SecCfgPartitionLength = 2048;
    private const int SecCfgImageLength = 0x200;

    [Fact]
    public async Task GetXFlashChipIdAsync_TenByteReply_SendsDocumentedControlPairAndDecodesLittleEndianWords()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageOneSessionAsync(transport);
        var before = transport.Writes.Count;
        EnqueueXFlashValue(transport, 0);
        EnqueueXFlashValue(transport, 0);
        EnqueueXFlashFrame(transport, Convert.FromHexString("89670100020003000400"));
        EnqueueXFlashValue(transport, 0);

        var identity = await session.GetXFlashChipIdAsync(TestContext.Current.CancellationToken);

        Assert.Equal((ushort)0x6789, identity.HardwareCode);
        Assert.Equal((ushort)0x0004, identity.ChipEvolution);
        Assert.Equal("89670100020003000400", identity.ReplyHex);
        Assert.Equal("00010009+0004000D", SecurityWordText(transport, before));
        Assert.Equal(MtkSessionState.XFlashStageOneReady, session.State);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("oversized")]
    public async Task GetXFlashChipIdAsync_ReplyOutsideDocumentedLayout_FaultsWithoutRetry(string shape)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageOneSessionAsync(transport);
        EnqueueXFlashValue(transport, 0);
        EnqueueXFlashValue(transport, 0);
        EnqueueXFlashFrame(transport, shape == "short" ? [0x01, 0x02] : new byte[12]);

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.GetXFlashChipIdAsync(
            TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.XFlashStorage);
        var refused = transport.Writes.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.GetXFlashChipIdAsync(
            TestContext.Current.CancellationToken));
        Assert.Equal(refused, transport.Writes.Count);
    }


    [Fact]
    public async Task ReadXFlashEfuseCellsAsync_MixedTable_ReadsOnlyAddressedCellsAndKeepsReportedValue()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageTwoSessionAsync(transport);
        var before = transport.Writes.Count;
        EnqueueXFlashValue(transport, 0);
        EnqueueXFlashValue(transport, 0);
        EnqueueXFlashFrame(transport, Convert.FromHexString("DDEEFF00"));
        EnqueueXFlashValue(transport, 0);

        var reading = await session.ReadXFlashEfuseCellsAsync(0x1208, 0x00100000, 10, 2,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, reading.Cells.Count);
        Assert.Equal(1, reading.ReadCellCount);
        Assert.True(reading.Cells[0].Cell.IsReportedWithoutRead);
        Assert.Equal("0A000000", reading.Cells[0].BytesHex);
        Assert.Equal(0x08000008u, reading.Cells[1].Cell.Address);
        Assert.Equal("DDEEFF00", reading.Cells[1].BytesHex);
        Assert.Equal("00010009+000F0002+08000008", SecurityWordText(transport, before));
    }

    [Fact]
    public async Task ReadXFlashEfuseCellsAsync_UnknownChipOrBadRange_RejectsBeforeIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageTwoSessionAsync(transport);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<NotSupportedException>(() => session.ReadXFlashEfuseCellsAsync(
            0x0001, 0x00100000, 0, 1, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.ReadXFlashEfuseCellsAsync(
            0x6570, 0x00100000, 32, 1, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.ReadXFlashEfuseCellsAsync(
            0x6570, 0x00100000, 0, MtkEfuseAccess.MaximumRequestedCells + 1, TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XFlashStageTwoStarted, session.State);
    }

    [Fact]
    public async Task ReadXFlashEfuseCellsAsync_StageOneOnly_IsRefusedBecauseCustomCodesNeedDaTwo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageOneSessionAsync(transport);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ReadXFlashEfuseCellsAsync(
            0x6570, 0x00100000, 0, 1, TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XFlashStageOneReady, session.State);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("oversized")]
    public async Task ReadXFlashEfuseCellsAsync_MalformedRegisterReply_FaultsWithoutRetry(string shape)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageTwoSessionAsync(transport);
        EnqueueXFlashValue(transport, 0);
        EnqueueXFlashValue(transport, 0);
        EnqueueXFlashFrame(transport, shape == "short" ? [0x01, 0x02, 0x03] : new byte[5]);

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.ReadXFlashEfuseCellsAsync(
            0x6570, 0x00100000, 0, 1, TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
        var refused = transport.Writes.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ReadXFlashEfuseCellsAsync(
            0x6570, 0x00100000, 0, 1, TestContext.Current.CancellationToken));
        Assert.Equal(refused, transport.Writes.Count);
    }

    [Fact]
    public async Task ReadXFlashBootPublicKeyAsync_EightDoublewords_AssemblesLittleEndianWindow()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageTwoSessionAsync(transport);
        var before = transport.Writes.Count;
        for (var slot = 0; slot < 8; slot++)
        {
            EnqueueXFlashValue(transport, 0);
            EnqueueXFlashValue(transport, 0);
            EnqueueXFlashFrame(transport, [0x10, 0x11, 0x12, 0x13]);
            EnqueueXFlashValue(transport, 0);
        }

        var key = await session.ReadXFlashBootPublicKeyAsync(0x00100000, TestContext.Current.CancellationToken);

        Assert.Equal(0x20, key.Length);
        Assert.Equal([0x10, 0x11, 0x12, 0x13, 0x10, 0x11, 0x12, 0x13], key[..8]);
        Assert.StartsWith("00010009+000F0002+00100090", SecurityWordText(transport, before), StringComparison.Ordinal);
        Assert.Equal(48, transport.Writes.Count - before);
    }

    [Fact]
    public async Task ReadXFlashSecCfgAsync_SoftwareProtectedPartitionImage_DecodesWithoutAnyWrite()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateGptSessionAsync(transport);
        EnqueueGpt(transport, SecCfgDisk());
        EnqueueStorageRead(transport, SecCfgImage(SecCfgClosedHeaderHex, SecCfgClosedHashHex));
        var before = transport.Writes.Count;

        var view = await session.ReadXFlashSecCfgAsync(8, TestContext.Current.CancellationToken);

        Assert.Equal(MtkSecCfgFormat.Version4, view.Format);
        Assert.Equal("seccfg", view.PartitionName);
        Assert.Equal(SecCfgPartitionOffset, view.ByteOffset);
        Assert.Equal(SecCfgPartitionLength, view.ByteLength);
        Assert.NotNull(view.Version4);
        Assert.Equal(MtkSecCfgHashKind.Software, view.Version4!.HashKind);
        Assert.True(view.Version4.IsClosed);
        Assert.Null(view.Version3);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(
            SecCfgImage(SecCfgClosedHeaderHex, SecCfgClosedHashHex))), view.ImageSha256Hex);
        Assert.DoesNotContain("00010004", SecurityWordText(transport, before), StringComparison.Ordinal);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Fact]
    public async Task ReadXFlashSecCfgAsync_UndocumentedHeader_FaultsWithoutReinterpreting()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateGptSessionAsync(transport);
        EnqueueGpt(transport, SecCfgDisk());
        EnqueueStorageRead(transport, new byte[SecCfgPartitionLength]);

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.ReadXFlashSecCfgAsync(8,
            TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
    }


    [Fact]
    public async Task ReadLegacyEfuseCellsAsync_BigEndianRegisterValue_ReEmitsLittleEndianBytes()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyGptSessionAsync(transport);
        var before = transport.Writes.Count;
        transport.Enqueue([0x00, 0xEE, 0xFF, 0xDD, 0x5A]);

        var reading = await session.ReadLegacyEfuseCellsAsync(0x1208, 0x00100000, 10, 2,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, reading.ReadCellCount);
        Assert.Equal("0A000000", reading.Cells[0].BytesHex);
        Assert.Equal("DDFFEE00", reading.Cells[1].BytesHex);
        Assert.Equal(0x00EEFFDDu, reading.Cells[1].Value);
        Assert.Contains(new byte[] { 0x7A, 0x08, 0x00, 0x00, 0x08 }, transport.Writes.Skip(before));
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Fact]
    public async Task ReadLegacyEfuseCellsAsync_NegativeAcknowledge_FaultsWithoutRetry()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyGptSessionAsync(transport);
        transport.Enqueue([0x00, 0x00, 0x00, 0x20, 0xA5]);

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.ReadLegacyEfuseCellsAsync(
            0x6570, 0x00100000, 0, 1, TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.LegacyStorage);
        var refused = transport.Writes.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ReadLegacyEfuseCellsAsync(
            0x6570, 0x00100000, 0, 1, TestContext.Current.CancellationToken));
        Assert.Equal(refused, transport.Writes.Count);
    }

    [Fact]
    public async Task ReadLegacyBootPublicKeyAsync_EightRegisters_PreservesLittleEndianOrder()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyGptSessionAsync(transport);
        var before = transport.Writes.Count;
        for (var slot = 0; slot < 8; slot++)
        {
            transport.Enqueue([0x00, 0x00, 0x00, (byte)(0x10 + slot), 0x5A]);
        }

        var key = await session.ReadLegacyBootPublicKeyAsync(0x00100000, TestContext.Current.CancellationToken);

        Assert.Equal(0x20, key.Length);
        Assert.Equal([0x10, 0x00, 0x00, 0x00, 0x11, 0x00, 0x00, 0x00], key[..8]);
        Assert.Contains(new byte[] { 0x7A, 0x00, 0x10, 0x00, 0x90 }, transport.Writes.Skip(before));
    }


    [Fact]
    public async Task ReadLegacySecCfgAsync_SoftwareProtectedImage_DecodesWithoutAnyWriteCommand()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyGptSessionAsync(transport);
        EnqueueLegacyGptReads(transport, SecCfgDisk());
        EnqueueLegacyRead(transport, SecCfgPartitionImage(SecCfgClosedHeaderHex, SecCfgClosedHashHex),
            SecCfgPartitionOffset, SecCfgPartitionLength);
        var before = transport.Writes.Count;

        var view = await session.ReadLegacySecCfgAsync(8, TestContext.Current.CancellationToken);

        Assert.Equal(MtkSecCfgFormat.Version4, view.Format);
        Assert.Equal("seccfg", view.PartitionName);
        Assert.Equal(SecCfgPartitionOffset, view.ByteOffset);
        Assert.NotNull(view.Version4);
        Assert.True(view.Version4!.IsClosed);
        Assert.DoesNotContain(transport.Writes.Skip(before), bytes => bytes.Length == 23 && bytes[0] == 0x62);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Fact]
    public async Task ReadLegacySecCfgAsync_UndocumentedHeader_FaultsWithoutReinterpreting()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyGptSessionAsync(transport);
        EnqueueLegacyGptReads(transport, SecCfgDisk());
        EnqueueLegacyRead(transport, new byte[SecCfgPartitionOffset + SecCfgPartitionLength],
            SecCfgPartitionOffset, SecCfgPartitionLength);

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.ReadLegacySecCfgAsync(8,
            TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
    }


    private static int IndexOfSecurityWord(ScriptedCommTransport transport, uint word, int after)
    {
        for (var index = after; index < transport.Writes.Count; index++)
        {
            if (transport.Writes[index].Length == 4
                && BinaryPrimitives.ReadUInt32LittleEndian(transport.Writes[index]) == word)
            {
                return index;
            }
        }

        return -1;
    }

    private static string SecurityWordText(ScriptedCommTransport transport, int before)
        => string.Join('+', transport.Writes.Skip(before).Where(bytes => bytes.Length == 4)
            .Select(bytes => BinaryPrimitives.ReadUInt32LittleEndian(bytes).ToString("X8")));

    private static byte[] SecCfgDisk()
    {
        var disk = GptReaderTests.CreateDisk(512);
        disk.AsSpan(1024 + 56, 72).Clear();
        for (var index = 0; index < "seccfg".Length; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(disk.AsSpan(1024 + 56 + index * 2), "seccfg"[index]);
        }

        GptReaderTests.FixArrays(disk);
        return disk;
    }

    private static byte[] SecCfgImage(string headerHex, string hashHex)
    {
        var image = new byte[SecCfgPartitionLength];
        Convert.FromHexString(headerHex).CopyTo(image, 0);
        Convert.FromHexString(hashHex).CopyTo(image, 0x1C);
        return image;
    }

    private static byte[] SecCfgVersion3Image()
    {
        var image = new byte[SecCfgPartitionLength];
        "AND_SECCFG_v\0\0\0\0"u8.CopyTo(image);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x10), MtkSecCfg.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x14), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x18), 0x130);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x20), MtkSecCfg.Version3ClosedEncryptionLength);
        BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x26), 0x200);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x28), 7);
        for (var index = 0; index < 0x100; index++)
        {
            image[0x2C + index] = (byte)index;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x12C), MtkSecCfg.EndFlag);
        return image;
    }

    private static byte[] SecCfgPartitionImage(string headerHex, string hashHex)
    {
        var partition = new byte[SecCfgPartitionOffset + SecCfgPartitionLength];
        SecCfgImage(headerHex, hashHex).CopyTo(partition, SecCfgPartitionOffset);
        return partition;
    }
}