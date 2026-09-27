using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed partial class MtkSessionTests
{
    [Theory]
    [InlineData(-131072L, 131072L)]
    [InlineData(0L, 0L)]
    [InlineData(0L, -131072L)]
    [InlineData(2048L, 131072L)]
    [InlineData(0L, 2048L)]
    [InlineData(0L, 131073L)]
    [InlineData(8L * 1024 * 1024, 131072L)]
    [InlineData(8L * 1024 * 1024 - 131072, 262144L)]
    [InlineData(long.MaxValue, 131072L)]
    [InlineData(0L, long.MaxValue)]
    public async Task WriteLegacyNandStorageAsync_InvalidRange_RejectsBeforeIo(long offset, long length)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNandSessionAsync(transport);
        using var source = new MemoryStream(new byte[270336]);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.WriteLegacyNandStorageAsync(
            offset, length, source, LegacyNandWriteOptions(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(0, source.Position);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("address")]
    [InlineData("fdm")]
    [InlineData("mode")]
    [InlineData("missing-bound")]
    [InlineData("zero-bound")]
    [InlineData("negative-bound")]
    [InlineData("unaligned-bound")]
    [InlineData("oversized-bound")]
    [InlineData("past-bound")]
    [InlineData("physical-bound")]
    public async Task WriteLegacyNandStorageAsync_InvalidOptions_RejectsBeforeIo(string failure)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNandSessionAsync(transport);
        using var source = new MemoryStream(new byte[135168]);
        var options = LegacyNandWriteOptions();
        var logical = options with { AddressType = MtkLegacyNandAddressType.Logical };
        options = failure switch
        {
            "profile" => options with { Profile = (MtkLegacyNandWriteProfile)1 },
            "address" => options with { AddressType = (MtkLegacyNandAddressType)2 },
            "fdm" => options with { Mode = (MtkLegacyNandWriteMode)3 },
            "mode" => options with { Mode = (MtkLegacyNandWriteMode)5 },
            "missing-bound" => logical,
            "zero-bound" => logical with { LogicalCapacityBytes = 0 },
            "negative-bound" => logical with { LogicalCapacityBytes = -131072 },
            "unaligned-bound" => logical with { LogicalCapacityBytes = 131072 + 2048 },
            "oversized-bound" => logical with { LogicalCapacityBytes = 16 * 1024 * 1024 },
            "past-bound" => logical with { LogicalCapacityBytes = 131072 },
            _ => options with { LogicalCapacityBytes = 8 * 1024 * 1024 }
        };
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.WriteLegacyNandStorageAsync(
            131072, 131072, source, options, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(0, source.Position);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteLegacyNandStorageAsync_ShortSource_DoesNotPadOrSendPartialPage(bool seekable)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNandSessionAsync(transport);
        using var source = seekable ? new MemoryStream(new byte[2111]) : new NonSeekableStorageSource(new byte[2111]);
        transport.Enqueue([0x5A]);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<EndOfStreamException>(() => session.WriteLegacyNandStorageAsync(
            0, 131072, source, LegacyNandWriteOptions(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(seekable ? 0 : 1, transport.Writes.Count - before);
        Assert.Equal(seekable ? MtkSessionState.LegacyStorageReady : MtkSessionState.Faulted, session.State);
    }

    [Theory]
    [InlineData(MtkLegacyNandWriteMode.PageAndSpare)]
    [InlineData(MtkLegacyNandWriteMode.SpareOnly)]
    [InlineData(MtkLegacyNandWriteMode.PageAndSpareWithEcc)]
    public void LegacyNandWriteOptions_MissingSpare_RejectsSpareModes(MtkLegacyNandWriteMode mode)
    {
        var details = LegacyNandDetails();
        details.AsSpan(2, 2).Clear();
        var geometry = MtkLegacyNandGeometry.Parse(LegacyNandInventory(true), [0x11, 0x22], details, true);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (LegacyNandWriteOptions() with { Mode = mode }).GetTransferLength(geometry, 131072));
        Assert.Equal(131072, (LegacyNandWriteOptions() with { Mode = MtkLegacyNandWriteMode.PageOnly })
            .GetTransferLength(geometry, 131072));
    }

    [Theory]
    [InlineData("input")]
    [InlineData("first-block")]
    [InlineData("last-block")]
    public void LegacyNandWriteOptions_Overflow_RejectsWithoutTruncation(string field)
    {
        var capacity = ((ulong)uint.MaxValue + 2) * 131072;
        var geometry = MtkLegacyNandGeometry.Parse(LegacyNandInventory(true, capacity), [0x11, 0x22], LegacyNandDetails(), true);
        var options = LegacyNandWriteOptions();
        var offset = field == "input" ? 0 : ((long)uint.MaxValue + (field == "first-block" ? 1 : 0)) * 131072;
        var length = field == "input" ? 0x1_0000_0000L : field == "last-block" ? 262144 : 131072;

        Assert.Throws<ArgumentOutOfRangeException>(() => options.CreateHeader(geometry, offset, length));
    }

    [Fact]
    public void LegacyNandWriteOptions_LastRepresentableBlock_PreservesAllIndexBits()
    {
        var capacity = ((ulong)uint.MaxValue + 1) * 131072;
        var geometry = MtkLegacyNandGeometry.Parse(LegacyNandInventory(true, capacity), [0x11, 0x22], LegacyNandDetails(), true);

        var header = LegacyNandWriteOptions().CreateHeader(geometry, (long)uint.MaxValue * 131072, 131072);

        Assert.Equal(Convert.FromHexString("B30100FFFFFFFF000000010000084000000000"), header);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteLegacyNandStorageAsync_DifferentStorage_RejectsBeforeIo(bool nor)
    {
        using var transport = new ScriptedCommTransport();
        using var session = nor ? await CreateLegacyNorSessionAsync(transport) : await CreateLegacyStorageSessionAsync(transport);
        using var source = new MemoryStream(new byte[135168]);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<NotSupportedException>(() => session.WriteLegacyNandStorageAsync(
            0, 131072, source, LegacyNandWriteOptions(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Fact]
    public async Task WriteLegacyNandStorageAsync_ClosedSource_RejectsBeforeIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNandSessionAsync(transport);
        using var source = new MemoryStream();
        source.Close();
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<ArgumentException>(() => session.WriteLegacyNandStorageAsync(0, 131072,
            source, LegacyNandWriteOptions(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(86400001)]
    public async Task WriteLegacyNandStorageAsync_InvalidDeadline_RejectsBeforeIo(int timeout)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNandSessionAsync(transport);
        using var source = new MemoryStream(new byte[135168]);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.WriteLegacyNandStorageAsync(0, 131072,
            source, LegacyNandWriteOptions(), timeoutMs: timeout, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
    }

    [Theory]
    [InlineData("before")]
    [InlineData("header")]
    [InlineData("checksum")]
    public async Task WriteLegacyNandStorageAsync_Cancellation_DoesNotReplayOrReportUnacknowledgedBytes(string stage)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNandSessionAsync(transport);
        using var source = new MemoryStream(new byte[135168]);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        transport.Enqueue([0x5A, 0x69]);
        if (stage == "before")
        {
            cancellation.Cancel();
        }
        else
        {
            transport.OnWrite = bytes =>
            {
                if (stage == "header" || bytes.Length == 2)
                {
                    cancellation.Cancel();
                }
                return [];
            };
        }
        var before = transport.Writes.Count;
        var progress = new ProgressRecorder();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.WriteLegacyNandStorageAsync(0, 131072,
            source, LegacyNandWriteOptions(), progress: progress, cancellationToken: cancellation.Token));

        Assert.Equal(stage switch { "before" => 0, "header" => 1, _ => 4 }, transport.Writes.Count - before);
        Assert.Empty(progress.Values);
        Assert.Equal(stage == "before" ? MtkSessionState.LegacyStorageReady : MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task WriteLegacyNandStorageAsync_BlockedSource_DeadlineFaultsBeforeSendingPage()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNandSessionAsync(transport);
        using var source = new LegacyNandProgramSource();
        transport.Enqueue([0x5A]);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<TimeoutException>(() => session.WriteLegacyNandStorageAsync(0, 131072,
            source, LegacyNandWriteOptions(), timeoutMs: 100, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before + 1, transport.Writes.Count);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task WriteLegacyNandStorageAsync_ConcurrentShutdown_WaitsForAllPageAcknowledgements()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNandSessionAsync(transport);
        using var source = new LegacyNandProgramSource();
        transport.Enqueue([0x5A, .. Enumerable.Repeat((byte)0x69, 64), 0x5A, 0x5A]);
        var write = session.WriteLegacyNandStorageAsync(0, 131072, source,
            LegacyNandWriteOptions(), cancellationToken: TestContext.Current.CancellationToken);
        Task shutdown;
        try
        {
            await source.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            var before = transport.Writes.Count;
            shutdown = session.ShutdownLegacyAsync(MtkLegacyShutdownMode.PowerOff, TestContext.Current.CancellationToken);
            Assert.False(shutdown.IsCompleted);
            Assert.Equal(before, transport.Writes.Count);
        }
        finally
        {
            source.Resume.TrySetResult();
            await write;
        }
        await shutdown;
        Assert.Equal(135168, source.Position);
        Assert.Equal(MtkSessionState.LegacyShutdownAcknowledged, session.State);
    }

    private sealed class LegacyNandProgramSource() : MemoryStream(new byte[135168])
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Resume.Task.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}