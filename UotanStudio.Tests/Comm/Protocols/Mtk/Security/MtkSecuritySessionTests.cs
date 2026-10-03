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
    private const string SecCfgOpenHeaderHex = "4D4D4D4D040000003C00000003000000010000000000000045454545";
    private const string SecCfgOpenHashHex = "E0FAB6F6572E45F101F63D837AB70CBD91BF782B4BC6D4DAF5A2E2BD431FD904";
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
    public async Task QueryXFlashSecurityControlCodeAsync_OpaqueReply_ReturnsBytesUnchanged()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageOneSessionAsync(transport);
        var reply = Convert.FromHexString("DEADBEEF0011");
        var before = transport.Writes.Count;
        EnqueueXFlashValue(transport, 0);
        EnqueueXFlashValue(transport, 0);
        EnqueueXFlashFrame(transport, reply);
        EnqueueXFlashValue(transport, 0);

        var received = await session.QueryXFlashSecurityControlCodeAsync(MtkXFlashRawControlCode.HardwareIdentifier,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(reply, received);
        Assert.Equal("00010009+00040014", SecurityWordText(transport, before));
        Assert.Equal(MtkSessionState.XFlashStageOneReady, session.State);
    }

    [Fact]
    public async Task QueryXFlashSecurityControlCodeAsync_ReplyBeyondCeiling_FaultsWithoutBufferingPayload()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageOneSessionAsync(transport);
        EnqueueXFlashValue(transport, 0);
        EnqueueXFlashValue(transport, 0);
        EnqueueXFlashFrame(transport, new byte[8]);

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.QueryXFlashSecurityControlCodeAsync(
            MtkXFlashRawControlCode.ReplayProtectedAreaStatus, 4, TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task QueryXFlashSecurityControlCodeAsync_UndefinedCodeOrCeiling_RejectsBeforeIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageOneSessionAsync(transport);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.QueryXFlashSecurityControlCodeAsync(
            (MtkXFlashRawControlCode)0x049999, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.QueryXFlashSecurityControlCodeAsync(
            MtkXFlashRawControlCode.HardwareIdentifier, 0, TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XFlashStageOneReady, session.State);
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
    public async Task ApplyXFlashSecCfgAsync_WriteNotConfirmed_RefusesAfterFullComputationAndKeepsStage()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateGptSessionAsync(transport);
        EnqueueGpt(transport, SecCfgDisk());
        EnqueueStorageRead(transport, SecCfgImage(SecCfgClosedHeaderHex, SecCfgClosedHashHex));
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => session.ApplyXFlashSecCfgAsync(
            MtkSecCfgLockIntent.Unlock, 8, confirmWrite: false, TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
        Assert.NotNull(session.XFlashStorage);
        Assert.DoesNotContain("00010004", SecurityWordText(transport, before), StringComparison.Ordinal);
        var refused = transport.Writes.Count;
        EnqueueGpt(transport, SecCfgDisk());
        EnqueueStorageRead(transport, SecCfgImage(SecCfgClosedHeaderHex, SecCfgClosedHashHex));
        EnqueueTransferStart(transport, read: false);
        EnqueueXFlashValue(transport, 0);
        EnqueueXFlashValue(transport, 0);

        var result = await session.ApplyXFlashSecCfgAsync(MtkSecCfgLockIntent.Unlock, 8, confirmWrite: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(SecCfgImageLength, result.ByteLength);
        Assert.Contains("00010004", SecurityWordText(transport, refused), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyXFlashSecCfgAsync_Confirmed_WritesPaddedImageAtTheLocatedPartitionRange()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateGptSessionAsync(transport);
        EnqueueGpt(transport, SecCfgDisk());
        EnqueueStorageRead(transport, SecCfgImage(SecCfgClosedHeaderHex, SecCfgClosedHashHex));
        EnqueueTransferStart(transport, read: false);
        EnqueueXFlashValue(transport, 0);
        EnqueueXFlashValue(transport, 0);
        var before = transport.Writes.Count;

        var result = await session.ApplyXFlashSecCfgAsync(MtkSecCfgLockIntent.Unlock, 8, confirmWrite: true,
            TestContext.Current.CancellationToken);

        Assert.Equal("seccfg", result.PartitionName);
        Assert.Equal(SecCfgPartitionOffset, result.ByteOffset);
        Assert.Equal(SecCfgImageLength, result.ByteLength);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(SecCfgOpenImage())), result.ImageSha256Hex);
        var header = transport.Writes.Skip(before).Single(bytes => bytes.Length == 56 &&
            BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(bytes, 4, 4)) == 8 &&
            BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(bytes, 8, 8)) ==
                (ulong)SecCfgPartitionOffset &&
            BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(bytes, 16, 8)) == SecCfgImageLength);
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(header, 4, 4)));
        Assert.Equal((ulong)SecCfgPartitionOffset,
            BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(header, 8, 8)));
        Assert.Equal((ulong)SecCfgImageLength,
            BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(header, 16, 8)));
        var writePhase = IndexOfSecurityWord(transport, 0x010004, before);
        Assert.True(writePhase >= before);
        Assert.Equal(SecCfgOpenImage(),
            transport.Writes.Skip(writePhase).Single(bytes => bytes.Length == SecCfgImageLength));
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Fact]
    public async Task ApplyXFlashSecCfgAsync_VersionThreeLayout_RefusesRewriteAndKeepsStage()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateGptSessionAsync(transport);
        EnqueueGpt(transport, SecCfgDisk());
        EnqueueStorageRead(transport, SecCfgVersion3Image());
        var before = transport.Writes.Count;

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => session.ApplyXFlashSecCfgAsync(
            MtkSecCfgLockIntent.Unlock, 8, confirmWrite: true, TestContext.Current.CancellationToken));

        Assert.Contains("version 3", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("00010004", SecurityWordText(transport, before), StringComparison.Ordinal);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Fact]
    public async Task ApplyXFlashSecCfgAsync_AlreadyAppliedState_RefusesWithoutWriteAndKeepsStage()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateGptSessionAsync(transport);
        EnqueueGpt(transport, SecCfgDisk());
        EnqueueStorageRead(transport, SecCfgImage(SecCfgOpenHeaderHex, SecCfgOpenHashHex));
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ApplyXFlashSecCfgAsync(
            MtkSecCfgLockIntent.Unlock, 8, confirmWrite: true, TestContext.Current.CancellationToken));

        Assert.DoesNotContain("00010004", SecurityWordText(transport, before), StringComparison.Ordinal);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4 * 1024 * 1024 + 512)]
    public async Task WriteXFlashSecCfgAsync_LengthOutsideCeiling_RejectsBeforeAnyAllocationOrIo(int length)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateGptSessionAsync(transport);
        using var source = new MemoryStream(new byte[8 * 1024 * 1024]);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.WriteXFlashSecCfgAsync(
            source, length, 8, confirmWrite: true, TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Fact]
    public async Task WriteXFlashSecCfgAsync_TruncatedSourceAndUndocumentedHeader_RejectBeforeIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateGptSessionAsync(transport);
        var before = transport.Writes.Count;
        using var truncated = new MemoryStream(new byte[1024]);

        await Assert.ThrowsAsync<ArgumentException>(() => session.WriteXFlashSecCfgAsync(
            truncated, SecCfgPartitionLength, 8, confirmWrite: true, TestContext.Current.CancellationToken));
        using var unknown = new MemoryStream(new byte[SecCfgPartitionLength]);

        await Assert.ThrowsAsync<ArgumentException>(() => session.WriteXFlashSecCfgAsync(
            unknown, SecCfgPartitionLength, 8, confirmWrite: true, TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Fact]
    public async Task WriteXFlashSecCfgAsync_LayoutMismatchingPartition_RefusesWithoutWriteAndKeepsStage()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateGptSessionAsync(transport);
        var before = transport.Writes.Count;
        EnqueueGpt(transport, SecCfgDisk());
        EnqueueStorageRead(transport, SecCfgImage(SecCfgClosedHeaderHex, SecCfgClosedHashHex));
        using var supplied = new MemoryStream(SecCfgVersion3Image());

        await Assert.ThrowsAsync<ArgumentException>(() => session.WriteXFlashSecCfgAsync(
            supplied, SecCfgPartitionLength, 8, confirmWrite: true, TestContext.Current.CancellationToken));

        Assert.DoesNotContain("00010004", SecurityWordText(transport, before), StringComparison.Ordinal);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Fact]
    public async Task ProgramXFlashOemKeyAsync_SoftwareClass_WritesEightLittleEndianRegisterFrames()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageTwoSessionAsync(transport);
        var before = transport.Writes.Count;
        for (var slot = 0; slot < 8; slot++)
        {
            EnqueueXFlashValue(transport, 0);
            EnqueueXFlashValue(transport, 0);
            EnqueueXFlashValue(transport, 0);
        }

        var receipt = await session.ProgramXFlashOemKeyAsync(
            new MtkOemKeyRequest(MtkOemKeyKind.SoftwareOneTimeProgramKey, KeyHex, 0x00100000), confirmWrite: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(MtkOemKeyKind.SoftwareOneTimeProgramKey, receipt.Kind);
        Assert.Equal(8, receipt.SubmittedUnits);
        Assert.Null(receipt.EchoHex);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Convert.FromHexString(KeyHex))), receipt.KeySha256Hex);
        var words = SecurityWordText(transport, before);
        Assert.StartsWith("00010009+000F0004+00100060+03020100+00010009+000F0004+00100064+07060504",
            words, StringComparison.Ordinal);
        Assert.EndsWith("+00010009+000F0004+0010007C+1F1E1D1C", words, StringComparison.Ordinal);
        Assert.Equal(32, words.Split('+').Length);
    }

    [Fact]
    public async Task ProgramXFlashOemKeyAsync_ReplayClass_EchoesReceiptAndMismatchFaultsWithoutClaimingSuccess()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageTwoSessionAsync(transport);
        EnqueueXFlashValue(transport, 0);
        EnqueueXFlashValue(transport, 0);
        EnqueueXFlashFrame(transport, Convert.FromHexString(KeyHex));
        EnqueueXFlashValue(transport, 0);
        var before = transport.Writes.Count;

        var receipt = await session.ProgramXFlashOemKeyAsync(
            new MtkOemKeyRequest(MtkOemKeyKind.ReplayProtectedAreaKey, KeyHex, null), confirmWrite: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, receipt.SubmittedUnits);
        Assert.Equal(KeyHex, receipt.EchoHex);
        Assert.Equal("00010009+000F000B", SecurityWordText(transport, before));
        Assert.Equal(MtkSessionState.XFlashStageTwoStarted, session.State);
        EnqueueXFlashValue(transport, 0);
        EnqueueXFlashValue(transport, 0);
        EnqueueXFlashFrame(transport, new byte[32]);
        EnqueueXFlashValue(transport, 0);

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.ProgramXFlashOemKeyAsync(
            new MtkOemKeyRequest(MtkOemKeyKind.ReplayProtectedAreaKey, KeyHex, null), confirmWrite: true,
            TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task ProgramXFlashOemKeyAsync_WriteNotConfirmedOrBadKey_SendsNoBytesAndKeepsStage()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageTwoSessionAsync(transport);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => session.ProgramXFlashOemKeyAsync(
            new MtkOemKeyRequest(MtkOemKeyKind.SoftwareOneTimeProgramKey, KeyHex, 0x00100000), confirmWrite: false,
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => session.ProgramXFlashOemKeyAsync(
            new MtkOemKeyRequest(MtkOemKeyKind.SoftwareOneTimeProgramKey, "00", 0x00100000), confirmWrite: true,
            TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XFlashStageTwoStarted, session.State);
    }

    [Fact]
    public async Task ProgramXFlashOemKeyAsync_StageOneOnly_RefusesBeforeAnyExchangeAndKeepsStage()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateStageOneSessionAsync(transport);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ProgramXFlashOemKeyAsync(
            new MtkOemKeyRequest(MtkOemKeyKind.SoftwareOneTimeProgramKey, KeyHex, 0x00100000), confirmWrite: true,
            TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XFlashStageOneReady, session.State);
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
    public async Task ProgramLegacyOemKeyAsync_SoftwareClass_WritesBigEndianRegisterFrames()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyGptSessionAsync(transport);
        var before = transport.Writes.Count;
        transport.Enqueue([0x5A, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A, 0x5A]);

        var receipt = await session.ProgramLegacyOemKeyAsync(
            new MtkOemKeyRequest(MtkOemKeyKind.SoftwareOneTimeProgramKey, KeyHex, 0x00100000), confirmWrite: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(8, receipt.SubmittedUnits);
        Assert.Null(receipt.EchoHex);
        Assert.Equal(8, transport.Writes.Count - before);
        Assert.Contains(new byte[] { 0x7B, 0x00, 0x10, 0x00, 0x60, 0x03, 0x02, 0x01, 0x00 }, transport.Writes.Skip(before));
        Assert.Contains(new byte[] { 0x7B, 0x00, 0x10, 0x00, 0x7C, 0x1F, 0x1E, 0x1D, 0x1C }, transport.Writes.Skip(before));
    }

    [Fact]
    public async Task ProgramLegacyOemKeyAsync_ReplayClassOrMissingOptIn_SendsNoBytes()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyGptSessionAsync(transport);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<NotSupportedException>(() => session.ProgramLegacyOemKeyAsync(
            new MtkOemKeyRequest(MtkOemKeyKind.ReplayProtectedAreaKey, KeyHex, null), confirmWrite: true,
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => session.ProgramLegacyOemKeyAsync(
            new MtkOemKeyRequest(MtkOemKeyKind.SoftwareOneTimeProgramKey, KeyHex, 0x00100000), confirmWrite: false,
            TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
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

    [Fact]
    public async Task ApplyLegacySecCfgAsync_WriteNotConfirmed_RefusesAfterComputationAndKeepsStage()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyGptSessionAsync(transport);
        EnqueueLegacyGptReads(transport, SecCfgDisk());
        EnqueueLegacyRead(transport, SecCfgPartitionImage(SecCfgClosedHeaderHex, SecCfgClosedHashHex),
            SecCfgPartitionOffset, SecCfgPartitionLength);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => session.ApplyLegacySecCfgAsync(
            MtkSecCfgLockIntent.Unlock, 8, confirmWrite: false, TestContext.Current.CancellationToken));

        Assert.DoesNotContain(transport.Writes.Skip(before), bytes => bytes.Length == 23 && bytes[0] == 0x62);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
        Assert.NotNull(session.LegacyStorage);
    }

    [Fact]
    public async Task ApplyLegacySecCfgAsync_Confirmed_WritesHeaderWithDocumentedCommandAndRange()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyGptSessionAsync(transport);
        EnqueueLegacyGptReads(transport, SecCfgDisk());
        EnqueueLegacyRead(transport, SecCfgPartitionImage(SecCfgClosedHeaderHex, SecCfgClosedHashHex),
            SecCfgPartitionOffset, SecCfgPartitionLength);
        transport.Enqueue([0x5A, 0x69]);
        var before = transport.Writes.Count;

        var result = await session.ApplyLegacySecCfgAsync(MtkSecCfgLockIntent.Unlock, 8, confirmWrite: true,
            TestContext.Current.CancellationToken);

        Assert.Equal("seccfg", result.PartitionName);
        Assert.Equal(SecCfgImageLength, result.ByteLength);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(SecCfgOpenImage())), result.ImageSha256Hex);
        var header = transport.Writes.Skip(before).Single(bytes => bytes.Length == 23 && bytes[0] == 0x62);
        Assert.Equal(0x62, (int)header[0]);
        Assert.Equal(8, (int)header[2]);
        Assert.Equal(SecCfgPartitionOffset, (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(3)));
        Assert.Equal(SecCfgImageLength, (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(11)));
        Assert.Equal(SecCfgOpenImage(), transport.Writes.Skip(before).Single(bytes => bytes.Length == SecCfgImageLength && bytes[0] == 0x4D));
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Fact]
    public async Task ApplyLegacySecCfgAsync_RejectedWriteHeader_FaultsWithoutRetry()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyGptSessionAsync(transport);
        EnqueueLegacyGptReads(transport, SecCfgDisk());
        EnqueueLegacyRead(transport, SecCfgPartitionImage(SecCfgClosedHeaderHex, SecCfgClosedHashHex),
            SecCfgPartitionOffset, SecCfgPartitionLength);
        transport.Enqueue([0xA5]);

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.ApplyLegacySecCfgAsync(
            MtkSecCfgLockIntent.Unlock, 8, confirmWrite: true, TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.LegacyStorage);
        var refused = transport.Writes.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ApplyLegacySecCfgAsync(
            MtkSecCfgLockIntent.Unlock, 8, confirmWrite: true, TestContext.Current.CancellationToken));
        Assert.Equal(refused, transport.Writes.Count);
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

    private static byte[] SecCfgOpenImage()
    {
        var image = new byte[SecCfgImageLength];
        Convert.FromHexString(SecCfgOpenHeaderHex).CopyTo(image, 0);
        Convert.FromHexString(SecCfgOpenHashHex).CopyTo(image, 0x1C);
        return image;
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