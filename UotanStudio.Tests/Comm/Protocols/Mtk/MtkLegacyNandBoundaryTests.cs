using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed partial class MtkSessionTests
{
    [Theory]
    [InlineData(-2048L, 2048L)]
    [InlineData(0L, 0L)]
    [InlineData(0L, -2048L)]
    [InlineData(1L, 2048L)]
    [InlineData(0L, 2047L)]
    [InlineData(8L * 1024 * 1024, 2048L)]
    [InlineData(8L * 1024 * 1024 - 2048, 4096L)]
    [InlineData(long.MaxValue - 2047, 2048L)]
    [InlineData(0L, long.MaxValue - 2047)]
    public async Task ReadLegacyNandStorageAsync_InvalidMainRange_RejectsBeforeIo(long offset, long length)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNandSessionAsync(transport);
        using var destination = new MemoryStream();
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.ReadLegacyNandStorageAsync(offset,
            length, destination, LegacyNandReadOptions(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("address")]
    [InlineData("verify-mode")]
    [InlineData("fdm-mode")]
    [InlineData("length-unit")]
    [InlineData("zero-pages")]
    [InlineData("too-many-pages")]
    [InlineData("packet-overflow")]
    [InlineData("missing-logical-bound")]
    [InlineData("negative-logical-bound")]
    [InlineData("zero-logical-bound")]
    [InlineData("unaligned-logical-bound")]
    [InlineData("oversized-logical-bound")]
    [InlineData("past-logical-bound")]
    [InlineData("physical-with-logical-bound")]
    public async Task ReadLegacyNandStorageAsync_InvalidProfile_RejectsBeforeIo(string failure)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNandSessionAsync(transport);
        using var destination = new MemoryStream();
        var options = LegacyNandReadOptions();
        options = failure switch
        {
            "profile" => options with { Profile = (MtkLegacyNandReadProfile)1 },
            "address" => options with { AddressType = (MtkLegacyNandAddressType)2 },
            "verify-mode" => options with { Mode = (MtkLegacyNandReadMode)5 },
            "fdm-mode" => options with { Mode = (MtkLegacyNandReadMode)7 },
            "length-unit" => options with { LengthUnit = (MtkLegacyNandLengthUnit)2 },
            "zero-pages" => options with { PagesPerPacket = 0 },
            "too-many-pages" => options with { PagesPerPacket = 4097 },
            "packet-overflow" => options with { PagesPerPacket = 497 },
            "missing-logical-bound" => options with { AddressType = MtkLegacyNandAddressType.Logical },
            "negative-logical-bound" => options with { AddressType = MtkLegacyNandAddressType.Logical, LogicalCapacityBytes = -2048 },
            "zero-logical-bound" => options with { AddressType = MtkLegacyNandAddressType.Logical, LogicalCapacityBytes = 0 },
            "unaligned-logical-bound" => options with { AddressType = MtkLegacyNandAddressType.Logical, LogicalCapacityBytes = 2049 },
            "oversized-logical-bound" => options with { AddressType = MtkLegacyNandAddressType.Logical, LogicalCapacityBytes = 16 * 1024 * 1024 },
            "past-logical-bound" => options with { AddressType = MtkLegacyNandAddressType.Logical, LogicalCapacityBytes = 2048 },
            _ => options with { LogicalCapacityBytes = 8 * 1024 * 1024 }
        };
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.ReadLegacyNandStorageAsync(2048,
            2048, destination, options, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(0, destination.Length);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Theory]
    [InlineData(MtkLegacyNandReadMode.PageAndSpare)]
    [InlineData(MtkLegacyNandReadMode.SpareOnly)]
    [InlineData(MtkLegacyNandReadMode.PageAndSpareWithEcc)]
    [InlineData(MtkLegacyNandReadMode.PageAndSpareNoRandom)]
    public void LegacyNandReadOptions_MissingSpare_RejectsSpareModes(MtkLegacyNandReadMode mode)
    {
        var details = LegacyNandDetails();
        details.AsSpan(2, 2).Clear();
        var geometry = MtkLegacyNandGeometry.Parse(LegacyNandInventory(true), [0x11, 0x22], details, true);
        var options = LegacyNandReadOptions() with { Mode = mode };

        Assert.Throws<ArgumentOutOfRangeException>(() => options.GetTransferLength(geometry, 2048));
    }

    [Theory]
    [InlineData(MtkLegacyNandReadMode.PageOnly)]
    [InlineData(MtkLegacyNandReadMode.PageWithEcc)]
    public void LegacyNandReadOptions_MissingSpare_PreservesMainOnlyModes(MtkLegacyNandReadMode mode)
    {
        var details = LegacyNandDetails();
        details.AsSpan(2, 2).Clear();
        var geometry = MtkLegacyNandGeometry.Parse(LegacyNandInventory(true), [0x11, 0x22], details, true);
        var options = LegacyNandReadOptions() with { Mode = mode };

        Assert.Equal(4096, options.GetTransferLength(geometry, 4096));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(86400001)]
    public async Task ReadLegacyNandStorageAsync_InvalidDeadline_RejectsBeforeIo(int timeout)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNandSessionAsync(transport);
        using var destination = new MemoryStream();
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.ReadLegacyNandStorageAsync(0, 2048,
            destination, LegacyNandReadOptions(), timeoutMs: timeout, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Fact]
    public async Task ReadLegacyNandStorageAsync_ReadOnlyDestination_RejectsBeforeIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNandSessionAsync(transport);
        using var destination = new MemoryStream([], writable: false);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<ArgumentException>(() => session.ReadLegacyNandStorageAsync(0, 2048,
            destination, LegacyNandReadOptions(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Theory]
    [InlineData("before")]
    [InlineData("header")]
    [InlineData("last-ack")]
    public async Task ReadLegacyNandStorageAsync_Cancellation_PreservesOnlyPreIoSession(string stage)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNandSessionAsync(transport);
        EnqueueLegacyNandRead(transport, 2112, 1, 1);
        using var destination = new MemoryStream();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        if (stage == "before")
        {
            cancellation.Cancel();
        }
        else
        {
            transport.OnWrite = bytes =>
            {
                if (stage == "header" || bytes.SequenceEqual(new byte[] { 0x5A }))
                {
                    cancellation.Cancel();
                }
                return [];
            };
        }
        var before = transport.Writes.Count;
        var progress = new ProgressRecorder();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ReadLegacyNandStorageAsync(0, 2048,
            destination, LegacyNandReadOptions(), progress: progress, cancellationToken: cancellation.Token));

        Assert.Equal(stage switch { "before" => 0, "header" => 1, _ => 3 }, transport.Writes.Count - before);
        Assert.Equal(stage == "last-ack" ? 2112 : 0, destination.Length);
        Assert.Empty(progress.Values);
        Assert.Equal(stage == "before" ? MtkSessionState.LegacyStorageReady : MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task ReadLegacyNandStorageAsync_BlockedDestination_DeadlineFaultsWithoutAck()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNandSessionAsync(transport);
        EnqueueLegacyNandRead(transport, 2112, 1, 1);
        using var destination = new DeadlineStorageStream();
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<TimeoutException>(() => session.ReadLegacyNandStorageAsync(0, 2048, destination,
            LegacyNandReadOptions(), timeoutMs: 100, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before + 2, transport.Writes.Count);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.LegacyStorage);
    }

    [Fact]
    public async Task ReadLegacyNandStorageAsync_DestinationFailure_FaultsWithoutAck()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNandSessionAsync(transport);
        EnqueueLegacyNandRead(transport, 2112, 1, 1);
        using var destination = new FailingLegacyNandDestination();
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<IOException>(() => session.ReadLegacyNandStorageAsync(0, 2048, destination,
            LegacyNandReadOptions(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before + 2, transport.Writes.Count);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task ReadLegacyNandStorageAsync_ConcurrentShutdown_WaitsUntilDestinationAndAckFinish()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNandSessionAsync(transport);
        var data = EnqueueLegacyNandRead(transport, 2112, 1, 1);
        transport.Enqueue([0x5A, 0x5A]);
        using var destination = new PausedLegacyNandDestination();
        var read = session.ReadLegacyNandStorageAsync(0, 2048, destination,
            LegacyNandReadOptions(), cancellationToken: TestContext.Current.CancellationToken);
        Task shutdown;
        try
        {
            await destination.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            var before = transport.Writes.Count;
            shutdown = session.ShutdownLegacyAsync(MtkLegacyShutdownMode.PowerOff, TestContext.Current.CancellationToken);
            Assert.False(shutdown.IsCompleted);
            Assert.Equal(before, transport.Writes.Count);
        }
        finally
        {
            destination.Resume.TrySetResult();
            await read;
        }
        await shutdown;
        Assert.Equal(data, destination.ToArray());
        Assert.Equal(MtkSessionState.LegacyShutdownAcknowledged, session.State);
    }

    private sealed class FailingLegacyNandDestination : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("The destination rejected a verified packet."));
    }

    private sealed class PausedLegacyNandDestination : MemoryStream
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Resume.Task.WaitAsync(cancellationToken);
            await base.WriteAsync(buffer, cancellationToken);
        }
    }
}