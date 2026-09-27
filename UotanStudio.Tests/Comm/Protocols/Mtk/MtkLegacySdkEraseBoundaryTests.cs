using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed partial class MtkSessionTests
{
    [Theory]
    [InlineData(-1L, 1024L, 1024)]
    [InlineData(0L, 0L, 1024)]
    [InlineData(0L, -1024L, 1024)]
    [InlineData(1L, 1024L, 1024)]
    [InlineData(0L, 1023L, 1024)]
    [InlineData(8192L, 1024L, 1024)]
    [InlineData(7168L, 2048L, 1024)]
    [InlineData(long.MaxValue, 1024L, 1024)]
    [InlineData(0L, long.MaxValue, 1024)]
    [InlineData(0L, 1024L, 0)]
    [InlineData(0L, 1024L, -1)]
    [InlineData(0L, 1024L, 8193)]
    [InlineData(0L, 1024L, 67108865)]
    public async Task EraseLegacyNorStorageAsync_InvalidRangeOrBlockSize_RejectsBeforeIo(long offset, long length, int blockSize)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNorSessionAsync(transport);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.EraseLegacyNorStorageAsync(offset, length,
            blockSize, LegacySdkEraseOptions(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("ack-marker")]
    [InlineData("zero-timeout")]
    [InlineData("negative-timeout")]
    [InlineData("long-timeout")]
    public async Task EraseLegacyNorStorageAsync_InvalidWireOptions_RejectsBeforeIo(string failure)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNorSessionAsync(transport);
        var options = failure switch
        {
            "profile" => LegacySdkEraseOptions() with { Profile = (MtkLegacyEraseProfile)1 },
            "ack-marker" => LegacySdkEraseOptions() with { StatisticsMarker = 0x5A },
            _ => LegacySdkEraseOptions()
        };
        var timeout = failure switch { "zero-timeout" => 0, "negative-timeout" => -1, "long-timeout" => 86400001, _ => 120000 };
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.EraseLegacyNorStorageAsync(0, 1024,
            1024, options, timeoutMs: timeout, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Theory]
    [InlineData("action")]
    [InlineData("address")]
    [InlineData("offset")]
    [InlineData("length")]
    [InlineData("past-capacity")]
    [InlineData("overflow")]
    [InlineData("missing-bound")]
    [InlineData("negative-bound")]
    [InlineData("unaligned-bound")]
    [InlineData("oversized-bound")]
    [InlineData("past-logical-bound")]
    [InlineData("physical-with-bound")]
    public async Task EraseLegacyNandStorageAsync_InvalidPolicyOrRange_RejectsBeforeIo(string failure)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNandSessionAsync(transport);
        var options = failure switch
        {
            "action" => LegacyNandEraseOptions() with { Mode = (MtkLegacyNandEraseMode)3 },
            "address" => LegacyNandEraseOptions() with { AddressType = (MtkLegacyNandAddressType)2 },
            "missing-bound" => LegacyNandEraseOptions() with { AddressType = MtkLegacyNandAddressType.Logical },
            "negative-bound" => LegacyNandEraseOptions() with { AddressType = MtkLegacyNandAddressType.Logical, LogicalCapacityBytes = -131072 },
            "unaligned-bound" => LegacyNandEraseOptions() with { AddressType = MtkLegacyNandAddressType.Logical, LogicalCapacityBytes = 131072 + 2048 },
            "oversized-bound" => LegacyNandEraseOptions() with { AddressType = MtkLegacyNandAddressType.Logical, LogicalCapacityBytes = 16 * 1024 * 1024 },
            "past-logical-bound" => LegacyNandEraseOptions() with { AddressType = MtkLegacyNandAddressType.Logical, LogicalCapacityBytes = 131072 },
            "physical-with-bound" => LegacyNandEraseOptions() with { LogicalCapacityBytes = 131072 },
            _ => LegacyNandEraseOptions()
        };
        var offset = failure switch
        {
            "offset" => 2048L,
            "past-capacity" => 8L * 1024 * 1024,
            "overflow" => long.MaxValue,
            "past-logical-bound" => 131072L,
            _ => 0L
        };
        var length = failure == "length" ? 2048 : 131072;
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.EraseLegacyNandStorageAsync(offset,
            length, options, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Theory]
    [InlineData("emmc-to-nand")]
    [InlineData("emmc-to-nor")]
    [InlineData("nand-to-nor")]
    [InlineData("nor-to-nand")]
    [InlineData("nand-to-emmc")]
    [InlineData("nor-to-emmc")]
    public async Task LegacySdkEraseAsync_WrongStorageFamily_RejectsBeforeIo(string scenario)
    {
        using var transport = new ScriptedCommTransport();
        using var session = scenario.StartsWith("emmc", StringComparison.Ordinal)
            ? await CreateLegacyStorageSessionAsync(transport)
            : scenario.StartsWith("nand", StringComparison.Ordinal)
                ? await CreateLegacyNandSessionAsync(transport) : await CreateLegacyNorSessionAsync(transport);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<NotSupportedException>(() => scenario.EndsWith("nand", StringComparison.Ordinal)
            ? session.EraseLegacyNandStorageAsync(0, 131072, LegacyNandEraseOptions(), cancellationToken: TestContext.Current.CancellationToken)
            : scenario.EndsWith("nor", StringComparison.Ordinal)
                ? session.EraseLegacyNorStorageAsync(0, 1024, 1024, LegacySdkEraseOptions(), cancellationToken: TestContext.Current.CancellationToken)
                : session.EraseLegacyStorageAsync(8, 0, 512, LegacySdkEraseOptions(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Fact]
    public async Task EraseLegacyStorageAsync_SdkRpmb_RejectsBeforeIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyStorageSessionAsync(transport);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<NotSupportedException>(() => session.EraseLegacyStorageAsync(3, 0, 512,
            LegacySdkEraseOptions(), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EraseLegacyNandStorageAsync_Cancellation_DistinguishesPreIoAndUnknownTargetResult(bool inFlight)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyNandSessionAsync(transport);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        if (inFlight)
        {
            transport.OnWrite = _ =>
            {
                cancellation.Cancel();
                return [];
            };
        }
        else
        {
            cancellation.Cancel();
        }
        var before = transport.Writes.Count;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.EraseLegacyNandStorageAsync(0, 131072,
            LegacyNandEraseOptions(), cancellationToken: cancellation.Token));

        Assert.Equal(before + (inFlight ? 1 : 0), transport.Writes.Count);
        Assert.Equal(inFlight ? MtkSessionState.Faulted : MtkSessionState.LegacyStorageReady, session.State);
    }

    [Fact]
    public async Task EraseLegacyNorStorageAsync_ProgressWait_UsesWholeDeadlineAndFaultsOnExpiry()
    {
        using var dir = new TempDirectory();
        using var scripted = new ScriptedCommTransport();
        using var transport = new LegacyEraseWaitTransport(scripted);
        EnqueueLegacyBootstrap(scripted, false, norInventory: LegacyNorInventory());
        using var session = await MtkBootstrap.InitializeAsync(transport, MtkDaProtocol.Legacy,
            LegacyBootstrapOptions(dir) with { StorageKind = MtkXFlashStorageKind.Nor }, TestContext.Current.CancellationToken);
        EnqueueLegacySdkErase(scripted);
        transport.BlockNextProgressRead = true;
        var before = scripted.Writes.Count;

        await Assert.ThrowsAsync<TimeoutException>(() => session.EraseLegacyNorStorageAsync(0, 1024, 1024,
            LegacySdkEraseOptions(), timeoutMs: 100, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(100, transport.BlockedReadTimeoutMs);
        Assert.Equal(before + 1, scripted.Writes.Count);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.LegacyStorage);
    }

    [Fact]
    public async Task EraseLegacyNorStorageAsync_ConcurrentShutdown_WaitsForEraseCompletion()
    {
        using var dir = new TempDirectory();
        using var scripted = new ScriptedCommTransport();
        using var transport = new LegacyEraseWaitTransport(scripted);
        EnqueueLegacyBootstrap(scripted, false, norInventory: LegacyNorInventory());
        using var session = await MtkBootstrap.InitializeAsync(transport, MtkDaProtocol.Legacy,
            LegacyBootstrapOptions(dir) with { StorageKind = MtkXFlashStorageKind.Nor }, TestContext.Current.CancellationToken);
        EnqueueLegacySdkErase(scripted);
        scripted.Enqueue([0x5A, 0x5A]);
        transport.BlockNextProgressRead = true;
        var erase = session.EraseLegacyNorStorageAsync(0, 1024, 1024,
            LegacySdkEraseOptions(), cancellationToken: TestContext.Current.CancellationToken);
        Task shutdown;
        try
        {
            await transport.ReadEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
            var before = scripted.Writes.Count;
            shutdown = session.ShutdownLegacyAsync(MtkLegacyShutdownMode.PowerOff, TestContext.Current.CancellationToken);
            Assert.False(shutdown.IsCompleted);
            Assert.Equal(before, scripted.Writes.Count);
        }
        finally
        {
            transport.Resume.TrySetResult();
            await erase;
        }
        await shutdown;
        Assert.Equal(MtkSessionState.LegacyShutdownAcknowledged, session.State);
    }

    private sealed class LegacyEraseWaitTransport(ScriptedCommTransport inner) : ICommTransport
    {
        internal bool BlockNextProgressRead { get; set; }
        internal int BlockedReadTimeoutMs { get; private set; }
        internal TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsConnected => inner.IsConnected;
        public void Connect() => inner.Connect();
        public void Disconnect() => inner.Disconnect();
        public void Reset() => inner.Reset();
        public Stream GetStream() => inner.GetStream();
        public void Dispose() => inner.Dispose();
        public long Write(byte[] data, int offset, int length, int timeoutMs) => inner.Write(data, offset, length, timeoutMs);
        public int Read(byte[] data, int offset, int length, int timeoutMs) => inner.Read(data, offset, length, timeoutMs);
        public Task<long> WriteAsync(byte[] data, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(data, offset, length, timeoutMs, cancellationToken);

        public async Task<int> ReadAsync(byte[] data, int offset, int length, int timeoutMs, CancellationToken cancellationToken = default)
        {
            if (BlockNextProgressRead && length == 4)
            {
                BlockNextProgressRead = false;
                BlockedReadTimeoutMs = timeoutMs;
                ReadEntered.TrySetResult();
                await Resume.Task.WaitAsync(cancellationToken);
            }
            return await inner.ReadAsync(data, offset, length, timeoutMs, cancellationToken);
        }
    }
}