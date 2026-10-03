using System.Buffers.Binary;
using System.Security.Cryptography;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Protocols.Mtk.Exploit;
using UotanStudio.Comm.Protocols.Mtk.Security;
using UotanStudio.Services.Flash;
using UotanStudio.Tests.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.Protocols.Mtk.Rpmb;
using UotanStudio.Tests.Comm.TestDoubles;
using UotanStudio.Tests.Firmware;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class MtkSecCfgServiceTests
{
    private const uint SecCfgRegion = 8;
    private const int SecCfgPartitionLength = 2048;
    private const long SecCfgPartitionOffset = 20480;
    private const ushort SoftwareTableHardwareCode = 0x6570;
    private const uint EfuseBase = 0x00100000;
    private const uint SejBase = 0x10000000;
    private const ushort StageTwoHardwareCode = 0x0766;
    private const uint RecordedSejBase = 0x1000A000;
    private const uint TypedSejBase = 0x11223344;
    private const string KeyHex = "000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F";

    [Fact]
    public async Task ReadReportAsync_SoftwareProtectedVersionFourImage_DecodesClosedLockState()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkSessionTests.CreateGptSessionAsync(transport);
        EnqueueGptWithSecCfg(transport, SecCfgImage(lockState: 1, criticalLockState: 0));
        var progress = new RecordingProgress();
        var before = transport.Writes.Count;

        var outcome = await MtkSecCfgService.ReadReportAsync(session, MtkDaProtocol.XFlash, SecCfgRegion, progress,
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Status.Success);
        Assert.Null(outcome.Status.VendorStatusCode);
        Assert.Equal(MtkDaProtocol.XFlash, outcome.Family);
        var view = Assert.IsAssignableFrom<MtkSecCfgView>(outcome.View);
        Assert.Equal(MtkSecCfgFormat.Version4, view.Format);
        Assert.Equal("seccfg", view.PartitionName);
        Assert.Equal(SecCfgPartitionOffset, view.ByteOffset);
        Assert.Equal(SecCfgPartitionLength, view.ByteLength);
        Assert.NotNull(view.Version4);
        Assert.Equal(MtkSecCfgHashKind.Software, view.Version4!.HashKind);
        Assert.True(view.Version4.IsClosed);
        Assert.Equal(MtkSecCfgLockState.Default, view.Version4.LockState);
        Assert.Contains("seccfg", outcome.Status.Summary, StringComparison.Ordinal);
        Assert.Equal(100, progress.Values[^1].Percent);
        Assert.True(transport.Writes.Count > before);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Fact]
    public async Task ReadReportAsync_ImageWithoutDocumentedHeader_ReportsFailureWithoutAVendorCode()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkSessionTests.CreateGptSessionAsync(transport);
        EnqueueGptWithSecCfg(transport, new byte[SecCfgPartitionLength]);

        var outcome = await MtkSecCfgService.ReadReportAsync(session, MtkDaProtocol.XFlash, SecCfgRegion,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(outcome.Status.Success);
        Assert.Null(outcome.Status.VendorStatusCode);
        Assert.Null(outcome.View);
        Assert.Contains("documented", outcome.Status.Summary, StringComparison.Ordinal);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task ReadReportAsync_XmlFamily_IsRefusedBeforeAnyByteIsSent()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXmlControlSessionAsync(transport);
        var before = transport.Writes.Count;

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => MtkSecCfgService.ReadReportAsync(
            session, MtkDaProtocol.Xml, SecCfgRegion, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("XML", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XmlControlReady, session.State);
    }

    [Fact]
    public async Task ReadReportAsync_SessionWithoutStorageStage_IsRefusedBeforeAnyByteIsSent()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateStageOneSessionAsync(transport);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => MtkSecCfgService.ReadReportAsync(
            session, MtkDaProtocol.XFlash, SecCfgRegion, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XFlashStageOneReady, session.State);
    }

    [Fact]
    public async Task ReadReportAsync_LegacyRegionBeyondOneByte_IsRefusedBeforeAnyByteIsSent()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateLegacyRpmbSessionAsync(transport);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => MtkSecCfgService.ReadReportAsync(
            session, MtkDaProtocol.Legacy, 256, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Fact]
    public async Task ReadReportAsync_InFlightCancellation_PropagatesInsteadOfReportingFailure()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkSessionTests.CreateGptSessionAsync(transport);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        MtkRpmbTestHarness.EnqueueXFlashValue(transport, 0);
        MtkRpmbTestHarness.EnqueueXFlashValue(transport, 0);
        MtkRpmbTestHarness.EnqueueXFlashValue(transport, 0);
        foreach (var chunk in Chunk(SecCfgDisk()[512..1024]))
        {
            MtkRpmbTestHarness.EnqueueXFlashFrame(transport, chunk);
        }

        MtkRpmbTestHarness.EnqueueXFlashValue(transport, 0);
        transport.OnWrite = bytes =>
        {
            if (bytes.Length == 56)
            {
                cancellation.Cancel();
            }

            return [];
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MtkSecCfgService.ReadReportAsync(
            session, MtkDaProtocol.XFlash, SecCfgRegion, cancellationToken: cancellation.Token));

        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task ApplyLockStateAsync_WriteNotConfirmed_IsRefusedBeforeAnyByteIsSent()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkSessionTests.CreateGptSessionAsync(transport);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => MtkSecCfgService.ApplyLockStateAsync(
            session, MtkDaProtocol.XFlash, MtkSecCfgLockIntent.Unlock, SecCfgRegion, confirmed: false,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
        Assert.NotNull(session.XFlashStorage);
    }

    [Fact]
    public async Task ApplyLockStateAsync_XmlFamily_IsRefusedAheadOfTheConfirmationCheck()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXmlControlSessionAsync(transport);
        var before = transport.Writes.Count;

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => MtkSecCfgService.ApplyLockStateAsync(
            session, MtkDaProtocol.Xml, MtkSecCfgLockIntent.Unlock, SecCfgRegion, confirmed: false,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("XML", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, transport.Writes.Count);
    }

    [Fact]
    public async Task ApplyLockStateAsync_TargetAcknowledgesWrite_ReturnsReceiptForPaddedImage()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkSessionTests.CreateGptSessionAsync(transport);
        EnqueueGptWithSecCfg(transport, SecCfgImage(lockState: 1, criticalLockState: 0));
        EnqueueXFlashWriteStart(transport);
        MtkRpmbTestHarness.EnqueueXFlashValue(transport, 0);
        MtkRpmbTestHarness.EnqueueXFlashValue(transport, 0);
        var progress = new RecordingProgress();

        var outcome = await MtkSecCfgService.ApplyLockStateAsync(session, MtkDaProtocol.XFlash,
            MtkSecCfgLockIntent.Unlock, SecCfgRegion, confirmed: true, progress,
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Status.Success);
        Assert.Null(outcome.Status.VendorStatusCode);
        var receipt = Assert.IsAssignableFrom<MtkSecCfgWriteResult>(outcome.Receipt);
        Assert.Equal("seccfg", receipt.PartitionName);
        Assert.Equal(SecCfgPartitionOffset, receipt.ByteOffset);
        Assert.Equal(MtkSecCfg.ImageAlignment, receipt.ByteLength);
        Assert.Equal(MtkSecCfgFormat.Version4, receipt.Format);
        Assert.Contains("read", outcome.Status.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(100, progress.Values[^1].Percent);
        Assert.Equal(MtkSessionState.XFlashStorageReady, session.State);
    }

    [Fact]
    public async Task ApplyLockStateAsync_TargetRejectsWritePacket_FailsWithVendorStatusCodeAndNoReceipt()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkSessionTests.CreateGptSessionAsync(transport);
        EnqueueGptWithSecCfg(transport, SecCfgImage(lockState: 1, criticalLockState: 0));
        EnqueueXFlashWriteStart(transport);
        MtkRpmbTestHarness.EnqueueXFlashFrame(transport, [1, 0, 0, 0xC0]);

        var outcome = await MtkSecCfgService.ApplyLockStateAsync(session, MtkDaProtocol.XFlash,
            MtkSecCfgLockIntent.Unlock, SecCfgRegion, confirmed: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(outcome.Status.Success);
        Assert.Null(outcome.Receipt);
        Assert.Equal(0xC0000001u, outcome.Status.VendorStatusCode);
        Assert.True(outcome.Status.IsVendorRejection);
        Assert.Equal("0xC0000001", outcome.Status.VendorStatusDescription);
        Assert.Contains("Unlock", outcome.Status.Summary, StringComparison.Ordinal);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task ReadEfuseRangeAsync_XFlashAddressedCell_ReadsOneRegisterPerCell()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateStageTwoSessionAsync(transport);
        var before = transport.Writes.Count;
        MtkRpmbTestHarness.EnqueueXFlashValue(transport, 0);
        MtkRpmbTestHarness.EnqueueXFlashValue(transport, 0);
        MtkRpmbTestHarness.EnqueueXFlashFrame(transport, [0x11, 0x22, 0x33, 0x44]);
        MtkRpmbTestHarness.EnqueueXFlashValue(transport, 0);

        var outcome = await MtkSecCfgService.ReadEfuseRangeAsync(session, MtkDaProtocol.XFlash,
            SoftwareTableHardwareCode, EfuseBase, 0, 1, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(outcome.Status.Success);
        var reading = Assert.IsAssignableFrom<MtkEfuseReading>(outcome.Reading);
        Assert.Equal(EfuseBase, reading.EfuseBase);
        Assert.Single(reading.Cells);
        Assert.Equal(1, reading.ReadCellCount);
        Assert.Equal(0x44332211u, reading.Cells[0].Value);
        Assert.Equal(checked(EfuseBase + 0x20u), reading.Cells[0].Cell.Address);
        Assert.Equal(6, transport.Writes.Count - before);
    }

    [Theory]
    [InlineData(0, "empty")]
    [InlineData(300, "oversized")]
    [InlineData(5, "past-end")]
    public async Task ReadEfuseRangeAsync_RangeOutsideDocumentedBounds_IsRefusedBeforeAnyByteIsSent(int count,
        string shape)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateStageTwoSessionAsync(transport);
        var before = transport.Writes.Count;
        var startIndex = shape == "past-end" ? 30 : 0;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => MtkSecCfgService.ReadEfuseRangeAsync(
            session, MtkDaProtocol.XFlash, SoftwareTableHardwareCode, EfuseBase, startIndex, count,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.XFlashStageTwoStarted, session.State);
    }

    [Fact]
    public async Task ReadEfuseRangeAsync_HardwareCodeWithoutTranscribedTable_IsRefusedBeforeAnyByteIsSent()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateStageTwoSessionAsync(transport);
        var before = transport.Writes.Count;

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => MtkSecCfgService.ReadEfuseRangeAsync(
            session, MtkDaProtocol.XFlash, 0x9999, EfuseBase, 0, 1,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("0x9999", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, transport.Writes.Count);
    }

    [Fact]
    public async Task ReadEfuseRangeAsync_XmlFamily_IsRefusedBeforeAnyByteIsSent()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXmlControlSessionAsync(transport);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<NotSupportedException>(() => MtkSecCfgService.ReadEfuseRangeAsync(
            session, MtkDaProtocol.Xml, SoftwareTableHardwareCode, EfuseBase, 0, 1,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
    }

    [Fact]
    public async Task ReadEfuseFieldAsync_BootPublicKeyWindow_AssemblesDocumentedDoublewords()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateStageTwoSessionAsync(transport);
        for (var slot = 0; slot < MtkEfuseAccess.PublicKeyLength / MtkEfuseAccess.CellLength; slot++)
        {
            MtkRpmbTestHarness.EnqueueXFlashValue(transport, 0);
            MtkRpmbTestHarness.EnqueueXFlashValue(transport, 0);
            MtkRpmbTestHarness.EnqueueXFlashFrame(transport, [0x10, 0x11, 0x12, 0x13]);
            MtkRpmbTestHarness.EnqueueXFlashValue(transport, 0);
        }

        var outcome = await MtkSecCfgService.ReadEfuseFieldAsync(session, MtkDaProtocol.XFlash,
            MtkSecEfuseField.BootPublicKey, EfuseBase, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(outcome.Status.Success);
        var bytes = Assert.IsAssignableFrom<byte[]>(outcome.Bytes);
        Assert.Equal(MtkEfuseAccess.PublicKeyLength, bytes.Length);
        Assert.Equal([0x10, 0x11, 0x12, 0x13], bytes[..4]);
        Assert.Contains("0x00100090", outcome.Status.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadEfuseFieldAsync_WindowBeyondAddressSpace_IsRefusedBeforeAnyByteIsSent()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateStageTwoSessionAsync(transport);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => MtkSecCfgService.ReadEfuseFieldAsync(
            session, MtkDaProtocol.XFlash, MtkSecEfuseField.BootPublicKey, uint.MaxValue - 0x10,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
    }

    [Fact]
    public async Task PrepareOemKeyRequest_UnconfirmedRequest_IsRefusedWithoutDeviceIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateStageTwoSessionAsync(transport);
        var before = transport.Writes.Count;

        Assert.Throws<UnauthorizedAccessException>(() => MtkSecCfgService.PrepareOemKeyRequest(session,
            MtkDaProtocol.XFlash, SoftwareKeyRequest(), confirmed: false,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
    }

    [Fact]
    public async Task PrepareOemKeyRequest_ConfirmedSoftwareKey_DescribesWindowWithoutSubmitting()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateStageTwoSessionAsync(transport);
        var before = transport.Writes.Count;
        var progress = new RecordingProgress();

        var outcome = MtkSecCfgService.PrepareOemKeyRequest(session, MtkDaProtocol.XFlash, SoftwareKeyRequest(),
            confirmed: true, progress, TestContext.Current.CancellationToken);

        Assert.True(outcome.Status.Success);
        Assert.Equal(before, transport.Writes.Count);
        var plan = Assert.IsAssignableFrom<MtkOemKeyPlan>(outcome.Plan);
        Assert.Equal(MtkDaProtocol.XFlash, plan.Family);
        Assert.Equal(MtkOemKeyKind.SoftwareOneTimeProgramKey, plan.Kind);
        Assert.Equal(8, plan.SubmittedUnits);
        Assert.Equal(8, plan.RegisterAddresses.Count);
        Assert.Equal(SejBase + MtkOemKeyRegisterWindow.Offset, plan.RegisterAddresses[0]);
        Assert.Equal(SejBase + 0x7C, plan.RegisterAddresses[7]);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Convert.FromHexString(KeyHex))), plan.KeySha256Hex);
        Assert.DoesNotContain(KeyHex, outcome.Status.Summary, StringComparison.Ordinal);
        Assert.Equal(100, progress.Values[^1].Percent);
    }

    [Fact]
    public async Task PrepareOemKeyRequest_SoftwareKeyWithoutTypedBase_UsesTheSejBaseTheChipTableRecords()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateStageTwoSessionAsync(transport);
        var before = transport.Writes.Count;

        Assert.Equal(StageTwoHardwareCode, session.DeviceInfo!.HardwareCode);
        var outcome = MtkSecCfgService.PrepareOemKeyRequest(session, MtkDaProtocol.XFlash,
            new MtkOemKeyRequest(MtkOemKeyKind.SoftwareOneTimeProgramKey, KeyHex, null),
            confirmed: true, progress: null, TestContext.Current.CancellationToken);

        Assert.True(outcome.Status.Success);
        Assert.Equal(before, transport.Writes.Count);
        var plan = Assert.IsAssignableFrom<MtkOemKeyPlan>(outcome.Plan);
        Assert.Equal(RecordedSejBase + MtkOemKeyRegisterWindow.Offset, plan.RegisterAddresses[0]);
        Assert.Equal(RecordedSejBase + 0x7C, plan.RegisterAddresses[7]);
    }

    [Fact]
    public async Task PrepareOemKeyRequest_SoftwareKeyWithTypedBase_KeepsTheTypedBaseOverTheChipTable()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateStageTwoSessionAsync(transport);

        var outcome = MtkSecCfgService.PrepareOemKeyRequest(session, MtkDaProtocol.XFlash,
            new MtkOemKeyRequest(MtkOemKeyKind.SoftwareOneTimeProgramKey, KeyHex, TypedSejBase),
            confirmed: true, progress: null, TestContext.Current.CancellationToken);

        var plan = Assert.IsAssignableFrom<MtkOemKeyPlan>(outcome.Plan);
        Assert.Equal(TypedSejBase + MtkOemKeyRegisterWindow.Offset, plan.RegisterAddresses[0]);
        Assert.Equal(TypedSejBase + 0x7C, plan.RegisterAddresses[7]);
        Assert.NotEqual(RecordedSejBase + MtkOemKeyRegisterWindow.Offset, plan.RegisterAddresses[0]);
    }

    [Fact]
    public async Task ProgramOemKeyAsync_SoftwareKeyWithoutTypedBase_SubmitsTheRecordedChipWindow()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateStageTwoSessionAsync(transport);
        var before = transport.Writes.Count;
        for (var slot = 0; slot < 8; slot++)
        {
            MtkRpmbTestHarness.EnqueueXFlashValue(transport, 0);
            MtkRpmbTestHarness.EnqueueXFlashValue(transport, 0);
            MtkRpmbTestHarness.EnqueueXFlashValue(transport, 0);
        }

        var outcome = await MtkSecCfgService.ProgramOemKeyAsync(session, MtkDaProtocol.XFlash,
            new MtkOemKeyRequest(MtkOemKeyKind.SoftwareOneTimeProgramKey, KeyHex, null), confirmed: true,
            progress: null, TestContext.Current.CancellationToken);

        Assert.True(outcome.Status.Success);
        var receipt = Assert.IsAssignableFrom<MtkOemKeyReceipt>(outcome.Receipt);
        Assert.Equal(8, receipt.SubmittedUnits);
        Assert.True(transport.Writes.Count > before);
    }

    [Fact]
    public void ResolveOemKeySejBase_TypedBaseAlongsideRecordedChip_KeepsTheTypedBase()
    {
        var profile = MtkBromChipTable.Find(StageTwoHardwareCode);

        Assert.NotNull(profile);
        Assert.Equal(RecordedSejBase, profile.SejBaseAddress);
        var resolved = MtkSecCfgService.ResolveOemKeySejBase(
            new MtkOemKeyRequest(MtkOemKeyKind.SoftwareOneTimeProgramKey, KeyHex, TypedSejBase), profile);

        Assert.Equal(TypedSejBase, resolved.SejBaseAddress);
    }

    [Fact]
    public void ResolveOemKeySejBase_SoftwareKeyWithoutAnyBase_LeavesTheRequestRefusedByDecodeKey()
    {
        var profile = MtkBromChipTable.Find(0x0571);

        Assert.NotNull(profile);
        Assert.True(profile.SejBaseAddress is null,
            $"mtkclient/config/brom_config.py:328 comments sej_base out for 0x0571, so nothing may be filled in " +
            $"from 0x{profile.SejBaseAddress:X8}.");
        var request = new MtkOemKeyRequest(MtkOemKeyKind.SoftwareOneTimeProgramKey, KeyHex, null);
        var resolved = MtkSecCfgService.ResolveOemKeySejBase(request, profile);

        Assert.Same(request, resolved);
        var error = Assert.Throws<ArgumentException>(() => resolved.DecodeKey());
        Assert.Equal(nameof(MtkOemKeyRequest.SejBaseAddress), error.ParamName);
        Assert.Contains("requires the SEJ peripheral base address", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveOemKeySejBase_ReplayProtectedClass_LeavesTheAddressEmpty()
    {
        var profile = MtkBromChipTable.Find(StageTwoHardwareCode);

        Assert.NotNull(profile);
        var request = new MtkOemKeyRequest(MtkOemKeyKind.ReplayProtectedAreaKey, KeyHex, null);
        var resolved = MtkSecCfgService.ResolveOemKeySejBase(request, profile);

        Assert.Same(request, resolved);
        Assert.Null(resolved.SejBaseAddress);
        Assert.Equal(32, resolved.DecodeKey().Length);
    }

    [Fact]
    public async Task PrepareOemKeyRequest_LegacyReplayProtectedClass_IsRefusedBeforeAnyByteIsSent()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateLegacyRpmbSessionAsync(transport);
        var before = transport.Writes.Count;

        var error = Assert.Throws<NotSupportedException>(() => MtkSecCfgService.PrepareOemKeyRequest(session,
            MtkDaProtocol.Legacy, new MtkOemKeyRequest(MtkOemKeyKind.ReplayProtectedAreaKey, KeyHex, null),
            confirmed: true, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("Legacy", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Fact]
    public async Task PrepareOemKeyRequest_KeyPayloadOutsideDocumentedLength_IsRefusedBeforeAnyByteIsSent()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateStageTwoSessionAsync(transport);
        var before = transport.Writes.Count;

        Assert.Throws<ArgumentException>(() => MtkSecCfgService.PrepareOemKeyRequest(session,
            MtkDaProtocol.XFlash,
            new MtkOemKeyRequest(MtkOemKeyKind.SoftwareOneTimeProgramKey, "00112233", SejBase),
            confirmed: true, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
    }

    [Fact]
    public async Task PrepareOemKeyRequest_CancelledToken_ThrowsWithoutProducingAPlan()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateStageTwoSessionAsync(transport);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => MtkSecCfgService.PrepareOemKeyRequest(session,
            MtkDaProtocol.XFlash, SoftwareKeyRequest(), confirmed: true, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task ProgramOemKeyAsync_SoftwareClass_SubmitsRegisterFramesAndReportsOnlyTheDigest()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateStageTwoSessionAsync(transport);
        var before = transport.Writes.Count;
        for (var slot = 0; slot < 8; slot++)
        {
            MtkRpmbTestHarness.EnqueueXFlashValue(transport, 0);
            MtkRpmbTestHarness.EnqueueXFlashValue(transport, 0);
            MtkRpmbTestHarness.EnqueueXFlashValue(transport, 0);
        }

        var outcome = await MtkSecCfgService.ProgramOemKeyAsync(session, MtkDaProtocol.XFlash,
            SoftwareKeyRequest(), confirmed: true, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(outcome.Status.Success);
        Assert.NotNull(outcome.Receipt);
        Assert.Equal(MtkDaProtocol.XFlash, outcome.Family);
        Assert.Equal(8, outcome.Receipt.SubmittedUnits);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Convert.FromHexString(KeyHex))),
            outcome.Receipt.KeySha256Hex);
        Assert.True(transport.Writes.Count > before);
        Assert.Contains("SoftwareOneTimeProgramKey", outcome.Status.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(KeyHex, outcome.Status.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProgramOemKeyAsync_UnconfirmedRequest_IsRefusedWithoutDeviceIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateStageTwoSessionAsync(transport);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => MtkSecCfgService.ProgramOemKeyAsync(session,
            MtkDaProtocol.XFlash, SoftwareKeyRequest(), confirmed: false,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
    }

    [Fact]
    public async Task ProgramOemKeyAsync_XmlFamily_IsRefusedBeforeDeviceIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateStageTwoSessionAsync(transport);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<NotSupportedException>(() => MtkSecCfgService.ProgramOemKeyAsync(session,
            MtkDaProtocol.Xml, SoftwareKeyRequest(), confirmed: true,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
    }

    [Fact]
    public async Task ProgramOemKeyAsync_LegacyReplayProtectedClass_IsRefusedBeforeDeviceIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateStageTwoSessionAsync(transport);
        var before = transport.Writes.Count;
        var request = new MtkOemKeyRequest(MtkOemKeyKind.ReplayProtectedAreaKey, KeyHex, null);

        await Assert.ThrowsAsync<NotSupportedException>(() => MtkSecCfgService.ProgramOemKeyAsync(session,
            MtkDaProtocol.Legacy, request, confirmed: true,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
    }

    [Fact]
    public async Task ProgramOemKeyAsync_CancelledToken_ThrowsWithoutSubmitting()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateStageTwoSessionAsync(transport);
        var before = transport.Writes.Count;
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MtkSecCfgService.ProgramOemKeyAsync(
            session, MtkDaProtocol.XFlash, SoftwareKeyRequest(), confirmed: true,
            cancellationToken: cancellation.Token));

        Assert.Equal(before, transport.Writes.Count);
    }

    private static MtkOemKeyRequest SoftwareKeyRequest() =>
        new(MtkOemKeyKind.SoftwareOneTimeProgramKey, KeyHex, SejBase);

    private static void EnqueueGptWithSecCfg(ScriptedCommTransport transport, byte[] image)
    {
        var disk = SecCfgDisk();
        MtkSessionTests.EnqueueGpt(transport, disk);
        EnqueueStorageRead(transport, image);
    }

    private static void EnqueueStorageRead(ScriptedCommTransport transport, byte[] bytes)
    {
        MtkRpmbTestHarness.EnqueueXFlashValue(transport, 0);
        MtkRpmbTestHarness.EnqueueXFlashValue(transport, 0);
        MtkRpmbTestHarness.EnqueueXFlashValue(transport, 0);
        foreach (var chunk in Chunk(bytes))
        {
            MtkRpmbTestHarness.EnqueueXFlashFrame(transport, chunk);
        }

        MtkRpmbTestHarness.EnqueueXFlashValue(transport, 0);
    }

    private static void EnqueueXFlashWriteStart(ScriptedCommTransport transport)
    {
        MtkRpmbTestHarness.EnqueueXFlashValue(transport, 0);
        MtkRpmbTestHarness.EnqueueXFlashValue(transport, 0);
    }

    private static IEnumerable<byte[]> Chunk(byte[] bytes)
    {
        for (var offset = 0; offset < bytes.Length; offset += 512)
        {
            yield return bytes.AsSpan(offset, Math.Min(512, bytes.Length - offset)).ToArray();
        }
    }

    private static byte[] SecCfgDisk()
    {
        var disk = GptReaderTests.CreateDisk(512);
        var name = disk.AsSpan(1024 + 56, 72);
        name.Clear();
        const string partitionName = "seccfg";
        for (var index = 0; index < partitionName.Length; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(name.Slice(index * 2), partitionName[index]);
        }

        GptReaderTests.FixArrays(disk);
        return disk;
    }

    private static byte[] SecCfgImage(uint lockState, uint criticalLockState)
    {
        var header = new byte[MtkSecCfg.Version4HeaderLength];
        BinaryPrimitives.WriteUInt32LittleEndian(header, MtkSecCfg.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), MtkSecCfg.Version4AgreedSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), lockState);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), criticalLockState);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), MtkSecCfg.EndFlag);
        var image = new byte[SecCfgPartitionLength];
        header.CopyTo(image, 0);
        MtkSecCfg.SoftwareTransform(SHA256.HashData(header), encrypt: true).CopyTo(image.AsSpan(0x1C));
        return image;
    }

    private sealed class RecordingProgress : IProgress<FlashProgress>
    {
        internal List<FlashProgress> Values { get; } = [];

        public void Report(FlashProgress value) => Values.Add(value);
    }
}