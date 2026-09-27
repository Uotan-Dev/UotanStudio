using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed partial class MtkSessionTests
{
    [Theory]
    [InlineData(MtkLegacyNandEraseMode.Normal)]
    [InlineData(MtkLegacyNandEraseMode.Force)]
    [InlineData(MtkLegacyNandEraseMode.MarkBadBlock)]
    public async Task EraseLegacyNandStorageAsync_SelectedAction_UsesSdkStatusLoopAndRequiresFinalAck(MtkLegacyNandEraseMode mode)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNandSessionAsync(transport);
        EnqueueLegacySdkErase(transport);
        transport.Enqueue([0x5A, 0x5A]);
        var before = transport.Writes.Count;
        var progress = new LegacyEraseProgressRecorder();

        var result = await session.EraseLegacyNandStorageAsync(131072, 262144, LegacyNandEraseOptions() with { Mode = mode },
            progress: progress, cancellationToken: TestContext.Current.CancellationToken);

        var expected = Convert.FromHexString("D40100000100000000000200000000000000040000");
        expected[2] = (byte)mode;
        Assert.Equal(expected, transport.Writes[before]);
        Assert.Equal(4, transport.Writes.Count - before);
        Assert.All(transport.Writes.Skip(before + 1), bytes => Assert.Equal(new byte[] { 0x5A }, bytes));
        Assert.Equal(new[] { 0, 50, 100 }, progress.Values);
        Assert.Null(result);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);

        await session.ShutdownLegacyAsync(MtkLegacyShutdownMode.PowerOff, TestContext.Current.CancellationToken);

        Assert.Equal(MtkSessionState.LegacyShutdownAcknowledged, session.State);
    }

    [Fact]
    public async Task EraseLegacyNorStorageAsync_ExplicitBlockSize_DoesNotSwitchEmmcPartition()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNorSessionAsync(transport);
        EnqueueLegacySdkErase(transport);
        var before = transport.Writes.Count;

        await session.EraseLegacyNorStorageAsync(1024, 2048, 1024,
            LegacySdkEraseOptions() with { ValidateAfterErase = true }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Convert.FromHexString("D40000010000000000000004000000000000000800"), transport.Writes[before]);
        Assert.Equal(4, transport.Writes.Count - before);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Fact]
    public async Task EraseLegacyStorageAsync_SdkProfile_SwitchesPartitionAndDoesNotExpectRepeatedAckPairs()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyStorageSessionAsync(transport);
        transport.Enqueue(Convert.FromHexString("5A015A5A"));
        EnqueueLegacySdkErase(transport);
        var before = transport.Writes.Count;

        await session.EraseLegacyStorageAsync(8, 512, 1024, LegacySdkEraseOptions(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new byte[] { 0x72 }, transport.Writes[before]);
        Assert.Equal(new byte[] { 0x60 }, transport.Writes[before + 1]);
        Assert.Equal(new byte[] { 8 }, transport.Writes[before + 2]);
        Assert.Equal(Convert.FromHexString("D40200000000000000000002000000000000000400"), transport.Writes[before + 3]);
        Assert.Equal(7, transport.Writes.Count - before);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Fact]
    public async Task EraseLegacyNandStorageAsync_LargeLogicalAddress_Preserves64BitRangeAndValidationByte()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNandSessionAsync(transport, capacity: 8UL * 1024 * 1024 * 1024);
        EnqueueLegacySdkErase(transport);
        var options = LegacyNandEraseOptions() with
        {
            AddressType = MtkLegacyNandAddressType.Logical,
            LogicalCapacityBytes = 6L * 1024 * 1024 * 1024,
            Wire = LegacySdkEraseOptions() with { ValidateAfterErase = true }
        };
        var before = transport.Writes.Count;

        await session.EraseLegacyNandStorageAsync(0x1_0000_0000, 131072, options,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Convert.FromHexString("D40100010000000001000000000000000000020000"), transport.Writes[before]);
    }

    [Fact]
    public async Task EraseLegacyNorStorageAsync_HundredPercentInProgress_DoesNotPublishCompletionBeforeDoneStatus()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNorSessionAsync(transport);
        transport.Enqueue(Convert.FromHexString("5A5A00000BD16400000000645A5A"));
        var progress = new LegacyEraseProgressRecorder();
        var before = transport.Writes.Count;

        await session.EraseLegacyNorStorageAsync(0, 1024, 1024, LegacySdkEraseOptions(), progress: progress,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new[] { 99, 100 }, progress.Values);
        Assert.Equal(3, transport.Writes.Count - before);
    }

    [Fact]
    public async Task EraseLegacyNorStorageAsync_DoneStatusWithLowerPercentage_UsesAuthoritativeCompletionStatus()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNorSessionAsync(transport);
        transport.Enqueue(Convert.FromHexString("5A5A00000000325A5A"));
        var progress = new LegacyEraseProgressRecorder();

        await session.EraseLegacyNorStorageAsync(0, 1024, 1024, LegacySdkEraseOptions(), progress: progress,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(new[] { 100 }, progress.Values);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EraseLegacyNorStorageAsync_PostProcessWait_ConsumesOnlySelectedCompletion(bool wait)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNorSessionAsync(transport);
        transport.Enqueue(Convert.FromHexString("5A5A00000000645A"));
        transport.Enqueue(wait ? [0xA5, 0x5A] : [0x5A]);
        transport.Enqueue([0x5A, 0x5A]);
        var before = transport.Writes.Count;

        await session.EraseLegacyNorStorageAsync(0, 1024, 1024, LegacySdkEraseOptions(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(before + 2, transport.Writes.Count);
        await session.ShutdownLegacyAsync(MtkLegacyShutdownMode.PowerOff, TestContext.Current.CancellationToken);
        Assert.Equal(MtkSessionState.LegacyShutdownAcknowledged, session.State);
    }

    [Theory]
    [InlineData(0x69)]
    [InlineData(0xA5)]
    public async Task EraseLegacyNandStorageAsync_DeviceError_FaultsEvenWhenAgentOffersContinuation(byte disposition)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNandSessionAsync(transport);
        transport.Enqueue(Convert.FromHexString("5A5A00000C100000000000020000"));
        transport.Enqueue([disposition]);
        var before = transport.Writes.Count;
        var progress = new LegacyEraseProgressRecorder();

        var error = await Assert.ThrowsAsync<MtkProtocolException>(() =>
            session.EraseLegacyNandStorageAsync(0, 131072, LegacyNandEraseOptions(), progress: progress,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0xC10U, error.Status);
        Assert.Contains("0000000000020000", error.Message);
        Assert.Equal(before + 1, transport.Writes.Count);
        Assert.Empty(progress.Values);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.LegacyStorage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EraseLegacyNorStorageAsync_InitialRejection_PreservesDeviceStatus(bool range)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNorSessionAsync(transport);
        transport.Enqueue(Convert.FromHexString(range ? "5AA500001234" : "A500001234"));
        var before = transport.Writes.Count;

        var error = await Assert.ThrowsAsync<MtkProtocolException>(() => session.EraseLegacyNorStorageAsync(0, 1024, 1024,
            LegacySdkEraseOptions(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0x1234U, error.Status);
        Assert.Equal(before + 1, transport.Writes.Count);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Theory]
    [InlineData("allowance")]
    [InlineData("range")]
    [InlineData("progress-truncated")]
    [InlineData("percent-truncated")]
    [InlineData("out-of-range")]
    [InlineData("decreased")]
    [InlineData("unknown-marker")]
    [InlineData("missing-final")]
    [InlineData("bad-final")]
    [InlineData("missing-after-wait")]
    [InlineData("repeated-wait")]
    [InlineData("error-truncated")]
    public async Task EraseLegacyNorStorageAsync_InvalidResponse_FaultsWithoutReportingSuccess(string failure)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNorSessionAsync(transport);
        var response = failure switch
        {
            "allowance" => "A5",
            "range" => "5AA5",
            "progress-truncated" => "5A5A0000",
            "percent-truncated" => "5A5A00000000",
            "out-of-range" => "5A5A00000BD165",
            "decreased" => "5A5A00000BD13200000BD131",
            "unknown-marker" => "5A5A000000006469",
            "missing-final" => "5A5A00000000645A",
            "bad-final" => "5A5A00000000645A69",
            "missing-after-wait" => "5A5A00000000645AA5",
            "repeated-wait" => "5A5A00000000645AA5A5",
            _ => "5A5A00000C100000"
        };
        transport.Enqueue(Convert.FromHexString(response));
        var progress = new LegacyEraseProgressRecorder();

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.EraseLegacyNorStorageAsync(0, 1024, 1024,
            LegacySdkEraseOptions(), progress: progress, cancellationToken: TestContext.Current.CancellationToken));

        Assert.DoesNotContain(100, progress.Values);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.LegacyStorage);
    }

    [Fact]
    public async Task EraseLegacyNorStorageAsync_UnendingProgress_FaultsAtBoundedReportCount()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNorSessionAsync(transport);
        transport.Enqueue([0x5A, 0x5A]);
        for (var index = 0; index < 4096; index++)
        {
            transport.Enqueue(Convert.FromHexString("00000BD100"));
        }
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.EraseLegacyNorStorageAsync(0, 1024, 1024,
            LegacySdkEraseOptions(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before + 4097, transport.Writes.Count);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task EraseLegacyStorageAsync_CompatibilityDeviceError_DoesNotIgnoreStatusWord()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyStorageSessionAsync(transport);
        transport.Enqueue(Convert.FromHexString("5A015A5A5A5A00000C10"));
        var before = transport.Writes.Count;

        var error = await Assert.ThrowsAsync<MtkProtocolException>(() => session.EraseLegacyStorageAsync(8, 0, 512,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0xC10U, error.Status);
        Assert.Equal(before + 4, transport.Writes.Count);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    private static MtkLegacyEraseOptions LegacySdkEraseOptions() => new() { Profile = MtkLegacyEraseProfile.Sdk64 };

    private static MtkLegacyNandEraseOptions LegacyNandEraseOptions() => new()
    {
        Wire = LegacySdkEraseOptions(),
        AddressType = MtkLegacyNandAddressType.Physical,
        Mode = MtkLegacyNandEraseMode.Normal
    };

    private static void EnqueueLegacySdkErase(ScriptedCommTransport transport) =>
        transport.Enqueue(Convert.FromHexString("5A5A00000BD10000000BD13200000000645A5A"));

    private sealed class LegacyEraseProgressRecorder : IProgress<int>
    {
        internal List<int> Values { get; } = [];
        public void Report(int value) => Values.Add(value);
    }
}