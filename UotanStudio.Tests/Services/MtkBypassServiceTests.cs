using UotanStudio.Comm.Protocols.Common.Evidence;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Protocols.Mtk.Exploit;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Flash;
using UotanStudio.Tests.Comm.Protocols.Mtk.Exploit;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class MtkBypassServiceTests
{
    private const int IdentityWriteCount = 7;
    private const ushort MultiFamilyChip = 0x0766;
    private const ushort SolePreloaderChip = 0x0571;
    private const ushort UndocumentedChip = 0x0001;
    private const uint AuthenticatedConfiguration = 0x17;
    private const int PatchedPayloadLength = 1336;
    private const int PerChipBlobLength = 612;

    [Fact]
    public async Task RunAsync_PermissionFlagsOff_RefusesWithoutEndpointIo()
    {
        using var pipe = new ScriptedCommTransport();
        EnqueueIdentity(pipe, MultiFamilyChip, AuthenticatedConfiguration);
        using var device = new BypassUsbSession(pipe);
        using var session = new MtkSession(new UsbDeviceTransport(device));
        await session.InitializeAsync(TestContext.Current.CancellationToken);
        var request = new MtkBypassRequest { Payload = MtkExploitPayloadSource.StageOnePatcher };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => MtkBypassService.RunAsync(session,
            request, null, TestContext.Current.CancellationToken));

        Assert.Contains("until the caller grants it explicitly", error.Message);
        Assert.Equal(IdentityWriteCount, pipe.Writes.Count);
        Assert.Empty(device.ControlTransfers);
        Assert.Null(session.LastBypassResult);
        Assert.Equal(MtkSessionState.Bootloader, session.State);
    }

    [Fact]
    public async Task RunAsync_MasterSwitchWithoutUnsafeWriteFlag_RefusesWithoutEndpointIo()
    {
        using var pipe = new ScriptedCommTransport();
        EnqueueIdentity(pipe, MultiFamilyChip, AuthenticatedConfiguration);
        using var device = new BypassUsbSession(pipe);
        using var session = new MtkSession(new UsbDeviceTransport(device));
        await session.InitializeAsync(TestContext.Current.CancellationToken);
        var request = new MtkBypassRequest
        {
            AllowExploitBypass = true,
            Payload = MtkExploitPayloadSource.StageOnePatcher,
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => MtkBypassService.RunAsync(session,
            request, null, TestContext.Current.CancellationToken));

        Assert.Contains("volatile write must be granted separately", error.Message);
        Assert.Equal(IdentityWriteCount, pipe.Writes.Count);
        Assert.Empty(device.ControlTransfers);
    }

    [Fact]
    public async Task RunAsync_AmonetOnGcpuCapableChip_LiftsBlacklistAndHandsOffToDownloadAgentUpload()
    {
        var profile = MtkKamakiriRunnerTests.Require(MtkBromChipTable.Find(MultiFamilyChip));
        using var model = HwCryptoModel(MultiFamilyChip);
        model.Program(profile.GcpuBase!.Value + 0x800, 1);
        model.Program(profile.GcpuBase!.Value + 0x418, 1);
        model.Pipe.OnWrite = model.Handle;
        using var session = new MtkSession(model.Pipe);

        var handoff = await MtkBypassService.RunAsync(session, AuthorisedRequest(MtkExploitFamily.Amonet), null,
            TestContext.Current.CancellationToken);

        Assert.Equal(MtkExploitFamily.Amonet, handoff.Result.Family);
        Assert.Equal(MultiFamilyChip, handoff.Result.HardwareCode);
        Assert.Equal("MT6765/MT8768t", handoff.Result.ChipName);
        Assert.Equal(0x201000u, handoff.Result.LandingAddress);
        Assert.Equal("generic_patcher_payload.bin", handoff.PayloadIdentity);
        Assert.Matches("^[0-9A-F]{64}$", handoff.PayloadSha256!);
        Assert.True(handoff.DaUploadHandoffReady);
        Assert.Equal(MtkSessionState.Bootloader, handoff.SessionState);
        Assert.Same(handoff.Result, session.LastBypassResult);
        Assert.Contains(model.Journal, entry => entry == W(profile.GcpuBase!.Value + 0xC00, 0x7E));
        Assert.Contains(model.Journal, entry =>
            entry == W(profile.BlacklistRanges[0].Address + BlacklistOffset, handoff.Result.LandingAddress));
    }

    [Fact]
    public async Task RunAsync_HashimotoOnCqdmaCapableChip_LiftsBlacklistAndHandsOffToDownloadAgentUpload()
    {
        var profile = MtkKamakiriRunnerTests.Require(MtkBromChipTable.Find(MultiFamilyChip));
        using var model = HwCryptoModel(MultiFamilyChip);
        model.TreatWritesAsSelfClearing(profile.CqdmaBase!.Value + 0x08);
        model.Pipe.OnWrite = model.Handle;
        using var session = new MtkSession(model.Pipe);

        var handoff = await MtkBypassService.RunAsync(session, AuthorisedRequest(MtkExploitFamily.Hashimoto), null,
            TestContext.Current.CancellationToken);

        Assert.Equal(MtkExploitFamily.Hashimoto, handoff.Result.Family);
        Assert.Equal(MultiFamilyChip, handoff.Result.HardwareCode);
        Assert.Equal(0x201000u, handoff.Result.LandingAddress);
        Assert.Equal("generic_patcher_payload.bin", handoff.PayloadIdentity);
        Assert.True(handoff.DaUploadHandoffReady);
        Assert.Equal(MtkSessionState.Bootloader, handoff.SessionState);
        Assert.Contains(model.Journal, entry => entry == W(profile.CqdmaBase!.Value + 0x08, 1));
        Assert.Contains(model.Journal, entry =>
            entry == W(profile.BlacklistRanges[0].Address + BlacklistOffset, handoff.Result.LandingAddress));
    }

    [Fact]
    public async Task RunAsync_UndocumentedHardwareCode_RefusesWithoutExploitBytes()
    {
        using var pipe = new ScriptedCommTransport();
        EnqueueIdentity(pipe, UndocumentedChip, AuthenticatedConfiguration);
        using var device = new BypassUsbSession(pipe);
        using var session = new MtkSession(new UsbDeviceTransport(device));
        await session.InitializeAsync(TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => MtkBypassService.RunAsync(session,
            AuthorisedRequest(MtkExploitFamily.Kamakiri), null, TestContext.Current.CancellationToken));

        Assert.Contains("records no Boot ROM sites", error.Message);
        Assert.Equal(IdentityWriteCount, pipe.Writes.Count);
        Assert.Empty(device.ControlTransfers);
        Assert.Equal(MtkSessionState.Bootloader, session.State);
    }

    [Fact]
    public async Task RunAsync_MultiFamilyChipWithoutNamedFamily_RefusesPreferenceOrder()
    {
        using var pipe = new ScriptedCommTransport();
        EnqueueIdentity(pipe, MultiFamilyChip, AuthenticatedConfiguration);
        using var device = new BypassUsbSession(pipe);
        using var session = new MtkSession(new UsbDeviceTransport(device));
        await session.InitializeAsync(TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => MtkBypassService.RunAsync(session,
            AuthorisedRequest(null), null, TestContext.Current.CancellationToken));

        Assert.Contains("name one explicitly", error.Message);
        Assert.Equal(IdentityWriteCount, pipe.Writes.Count);
        Assert.Empty(device.ControlTransfers);
        Assert.Null(session.LastBypassResult);
    }

    [Fact]
    public async Task RunAsync_ScriptedKamakiriReportedPayload_ReportsIdentityAndHandsOffToDownloadAgentUpload()
    {
        using var pipe = new ScriptedCommTransport();
        EnqueueIdentity(pipe, MultiFamilyChip, AuthenticatedConfiguration);
        using var device = new BypassUsbSession(pipe) { FailZeroLengthControls = true };
        using var session = new MtkSession(new UsbDeviceTransport(device));
        await session.InitializeAsync(TestContext.Current.CancellationToken);
        MtkKamakiriRunnerTests.EnqueueKamakiriResponses(pipe, PatchedPayloadLength);
        EnqueueIdentity(pipe, MultiFamilyChip, 0x00000000);
        var stages = new List<FlashProgress>();
        var progress = new ProgressCollector(stages);

        var handoff = await MtkBypassService.RunAsync(session,
            AuthorisedRequest(MtkExploitFamily.Kamakiri, MtkExploitPayloadSource.StageOnePatcher), progress,
            TestContext.Current.CancellationToken);

        Assert.Equal(MtkExploitFamily.Kamakiri, handoff.Result.Family);
        Assert.Equal(MultiFamilyChip, handoff.Result.HardwareCode);
        Assert.Equal("MT6765/MT8768t", handoff.Result.ChipName);
        Assert.Equal(PatchedPayloadLength, handoff.Result.PayloadLength);
        Assert.Equal(0x100A00u, handoff.Result.LandingAddress);
        Assert.Equal(AuthenticatedConfiguration, handoff.Result.TargetConfigurationBefore);
        Assert.Equal(0x00000000u, handoff.Result.TargetConfigurationAfter);
        Assert.True(handoff.Result.SignatureChecksOpen);
        Assert.True(handoff.Result.TargetConfigurationChanged);
        Assert.True(handoff.Result.ControlProbeStalled);
        Assert.Equal("generic_patcher_payload.bin", handoff.PayloadIdentity);
        Assert.Matches("^[0-9A-F]{64}$", handoff.PayloadSha256!);
        Assert.True(handoff.DaUploadHandoffReady);
        Assert.Equal(MtkSessionState.Bootloader, handoff.SessionState);
        Assert.Same(handoff.Result, session.LastBypassResult);
        Assert.Contains("MT6765/MT8768t", handoff.Summary, StringComparison.Ordinal);
        Assert.Contains("generic_patcher_payload.bin", handoff.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("正在", handoff.Summary, StringComparison.Ordinal);
        Assert.Contains(stages, stage => stage.Percent == 100);
        Assert.Contains(pipe.Writes, write => write.Length == PatchedPayloadLength);

        pipe.Enqueue([0xD7, 0, 0x20, 0, 0, 0, 0, 0, 8, 0, 0, 0, 2, 0, 0]);
        pipe.Enqueue([8, 0, 0, 0]);
        using var image = new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8]);
        await session.UploadDownloadAgentAsync(image, 0x200000, 8, 2, null,
            TestContext.Current.CancellationToken);

        Assert.Equal(MtkSessionState.AgentUploaded, session.State);
    }

    [Fact]
    public async Task RunAsync_SolePreloaderFamilySucceeded_ReportsNoPayloadIdentity()
    {
        using var pipe = new ScriptedCommTransport();
        EnqueueIdentity(pipe, SolePreloaderChip, AuthenticatedConfiguration);
        using var device = new BypassUsbSession(pipe);
        using var session = new MtkSession(new UsbDeviceTransport(device));
        await session.InitializeAsync(TestContext.Current.CancellationToken);
        EnqueueIdentity(pipe, SolePreloaderChip, 0x00000008);

        var handoff = await MtkBypassService.RunAsync(session,
            AuthorisedRequest(null, MtkExploitPayloadSource.ChipProfilePayload), null,
            TestContext.Current.CancellationToken);

        Assert.Equal(MtkExploitFamily.KamakiriPreloader, handoff.Result.Family);
        Assert.Equal(0, handoff.Result.PayloadLength);
        Assert.Null(handoff.PayloadIdentity);
        Assert.Null(handoff.PayloadSha256);
        Assert.True(handoff.DaUploadHandoffReady);
        Assert.Equal(17, device.ControlTransfers.Count);
        Assert.Contains("no payload uploaded", handoff.Summary);
    }

    [Fact]
    public async Task RunAsync_ChipProfilePayloadOnKamakiriChip_ReportsPerChipBlobIdentity()
    {
        using var pipe = new ScriptedCommTransport();
        EnqueueIdentity(pipe, MultiFamilyChip, AuthenticatedConfiguration);
        using var device = new BypassUsbSession(pipe) { FailZeroLengthControls = true };
        using var session = new MtkSession(new UsbDeviceTransport(device));
        await session.InitializeAsync(TestContext.Current.CancellationToken);
        MtkKamakiriRunnerTests.EnqueueKamakiriResponses(pipe, PerChipBlobLength);
        EnqueueIdentity(pipe, MultiFamilyChip, 0x00000000);

        var handoff = await MtkBypassService.RunAsync(session,
            AuthorisedRequest(MtkExploitFamily.Kamakiri, MtkExploitPayloadSource.ChipProfilePayload), null,
            TestContext.Current.CancellationToken);

        Assert.Equal("mt6765_payload.bin", handoff.PayloadIdentity);
        Assert.Equal(PerChipBlobLength, handoff.Result.PayloadLength);
        Assert.Matches("^[0-9A-F]{64}$", handoff.PayloadSha256!);
        Assert.Contains(pipe.Writes, write => write.Length == PerChipBlobLength);
        Assert.Contains("mt6765_payload.bin", handoff.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ChipWithoutNamedFamilyRegisters_RefusesNamedFamilyWithoutIo()
    {
        using var pipe = new ScriptedCommTransport();
        EnqueueIdentity(pipe, SolePreloaderChip, AuthenticatedConfiguration);
        using var device = new BypassUsbSession(pipe);
        using var session = new MtkSession(new UsbDeviceTransport(device));
        await session.InitializeAsync(TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => MtkBypassService.RunAsync(session,
            AuthorisedRequest(MtkExploitFamily.Kamakiri), null, TestContext.Current.CancellationToken));

        Assert.Contains("brom_payload_addr", error.Message);
        Assert.Equal(IdentityWriteCount, pipe.Writes.Count);
        Assert.Empty(device.ControlTransfers);
    }

    [Fact]
    public async Task RunAsync_PreCancelledToken_ThrowsWithoutContactingEndpoint()
    {
        using var pipe = new ScriptedCommTransport();
        using var device = new BypassUsbSession(pipe);
        using var session = new MtkSession(new UsbDeviceTransport(device));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => MtkBypassService.RunAsync(session,
            AuthorisedRequest(MtkExploitFamily.Kamakiri), null, cancellation.Token));

        Assert.Empty(pipe.Writes);
        Assert.Empty(device.ControlTransfers);
    }

    [Fact]
    public async Task RunAsync_WrongAcknowledgementFaultsSession_RefusesReplayWithoutNewBytes()
    {
        using var pipe = new ScriptedCommTransport();
        EnqueueIdentity(pipe, MultiFamilyChip, AuthenticatedConfiguration);
        using var device = new BypassUsbSession(pipe) { FailZeroLengthControls = true };
        using var session = new MtkSession(new UsbDeviceTransport(device));
        await session.InitializeAsync(TestContext.Current.CancellationToken);
        MtkKamakiriRunnerTests.EnqueueKamakiriResponses(pipe, PatchedPayloadLength,
            acknowledgement: [0xC1, 0xC2, 0xC3, 0xC4]);

        await Assert.ThrowsAsync<MtkProtocolException>(() => MtkBypassService.RunAsync(session,
            AuthorisedRequest(MtkExploitFamily.Kamakiri), null, TestContext.Current.CancellationToken));
        var writesAfterFault = pipe.Writes.Count;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => MtkBypassService.RunAsync(session,
            AuthorisedRequest(MtkExploitFamily.Kamakiri), null, TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.LastBypassResult);
        Assert.Equal(writesAfterFault, pipe.Writes.Count);
        Assert.Contains("current state is Faulted", error.Message);
        Assert.Contains(session.Evidence, entry => entry.Phase == "exploit-bypass"
            && entry.Outcome != CommandOutcomeKind.Completed);
    }

    [Fact]
    public async Task RunOnEndpointAsync_MissingPermissions_RefusesBeforeLease()
    {
        var endpoint = new DetectedDevice
        {
            Key = "usb:fake-mtk",
            Serial = "fake",
            Protocol = DeviceProtocolKind.Mtk,
            Transport = DeviceTransportKind.Usb,
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MtkBypassService.RunOnEndpointAsync(endpoint,
                new MtkBypassRequest { Payload = MtkExploitPayloadSource.StageOnePatcher }, null,
                CancellationToken.None));

        Assert.Contains("exploit bypass subsystem stays closed", error.Message);
    }

    [Fact(Skip = "Requires a physical MTK device in Boot ROM mode; set UOTAN_TEST_MTK_USB_ID to run read-only identity probing.")]
    public async Task RunAsync_PhysicalBootRomEndpoint_ReportsChipTableIdentityWithoutUploading() =>
        await Task.CompletedTask;

    private static MtkBypassRequest AuthorisedRequest(MtkExploitFamily? family,
        MtkExploitPayloadSource payload = MtkExploitPayloadSource.StageOnePatcher) => new()
        {
            AllowExploitBypass = true,
            AllowUnsafeVolatileWrite = true,
            Family = family,
            Payload = payload,
        };

    private static void EnqueueIdentity(ScriptedCommTransport transport, ushort hardwareCode,
        uint targetConfiguration) => transport.Enqueue([
            0x5F, 0xF5, 0xAF, 0xFA,
            0xFD, (byte)(hardwareCode >> 8), (byte)hardwareCode, 0, 0,
            0xFC, 0x8A, 0, 0xCA, 1, 0, 2, 0, 0,
            0xD8,
            (byte)(targetConfiguration >> 24), (byte)(targetConfiguration >> 16),
            (byte)(targetConfiguration >> 8), (byte)targetConfiguration,
            0, 0,
        ]);

    private const uint BlacklistOffset = 0x40;

    private static MtkDaRegisterModel HwCryptoModel(ushort hardwareCode)
    {
        var ranges = MtkBromChipTable.Find(hardwareCode)!.BlacklistRanges;
        return new MtkDaRegisterModel
        {
            HardwareCode = hardwareCode,
            TargetConfiguration = AuthenticatedConfiguration,
            AcknowledgementTriggerAddress = ranges[0].Address + BlacklistOffset,
            AcknowledgementAfterTrigger = [0xA1, 0xA2, 0xA3, 0xA4],
        };
    }

    private static string W(uint address, params uint[] values) =>
        MtkDaRegisterModel.WriteEntry(address, values);

    private sealed class ProgressCollector(List<FlashProgress> stages) : IProgress<FlashProgress>
    {
        public void Report(FlashProgress value) => stages.Add(value);
    }
}