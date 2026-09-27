using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public sealed partial class MtkSessionTests
{
    [Fact]
    public async Task ReadLegacyStorageAsync_FragmentedPackets_VerifiesChecksumBeforePublication()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyStorageSessionAsync(transport);
        transport.Enqueue(Convert.FromHexString("5A015A5A5A"));
        foreach (var value in Enumerable.Repeat((byte)1, 512))
        {
            transport.Enqueue([value]);
        }
        transport.Enqueue([2, 0]);
        transport.Enqueue(Enumerable.Repeat((byte)2, 512).ToArray());
        transport.Enqueue([4, 0]);
        using var destination = new MemoryStream();
        var progress = new ProgressRecorder();
        var before = transport.Writes.Count;

        await session.ReadLegacyStorageAsync(8, 512, 1024, destination, progress: progress,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Enumerable.Repeat((byte)1, 512).Concat(Enumerable.Repeat((byte)2, 512)), destination.ToArray());
        Assert.Equal(new long[] { 512, 1024 }, progress.Values);
        Assert.Equal(Convert.FromHexString("D60C020000000000000200000000000000040000000200"), transport.Writes[before + 3]);
        Assert.Equal(new byte[] { 0x72 }, transport.Writes[before]);
        Assert.Equal(new byte[] { 0x60 }, transport.Writes[before + 1]);
        Assert.Equal(new byte[] { 8 }, transport.Writes[before + 2]);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
        Assert.True(destination.CanWrite);
    }

    [Fact]
    public async Task ReadLegacyStorageAsync_BadChecksum_FaultsWithoutPublishingOrAcknowledgingBadPacket()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyStorageSessionAsync(transport);
        transport.Enqueue(Convert.FromHexString("5A015A5A5A"));
        transport.Enqueue(Enumerable.Repeat((byte)1, 512).ToArray());
        transport.Enqueue([0, 0]);
        using var destination = new MemoryStream();
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.ReadLegacyStorageAsync(8, 0, 512, destination,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, destination.Length);
        Assert.Equal(4, transport.Writes.Count - before);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.LegacyStorage);
    }

    [Fact]
    public async Task WriteLegacyStorageAsync_TwoPackets_SendsExactDataAndAdditiveChecksums()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyStorageSessionAsync(transport);
        transport.Enqueue([0x5A, 0x69, 0x69]);
        using var source = new FragmentedSource(Enumerable.Repeat((byte)0xFF, 1024).ToArray());
        var progress = new ProgressRecorder();
        var before = transport.Writes.Count;

        await session.WriteLegacyStorageAsync(2, 512, 1024, source, progress: progress,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Convert.FromHexString("6201020000000000000200000000000000040000000200"), transport.Writes[before]);
        Assert.Equal(new byte[] { 0x5A }, transport.Writes[before + 1]);
        Assert.Equal(Enumerable.Repeat((byte)0xFF, 512), transport.Writes[before + 2]);
        Assert.Equal(new byte[] { 0xFE, 0 }, transport.Writes[before + 3]);
        Assert.Equal(new byte[] { 0xFE, 0 }, transport.Writes[before + 6]);
        Assert.Equal(7, transport.Writes.Count - before);
        Assert.Equal(new long[] { 512, 1024 }, progress.Values);
        Assert.Equal(1024, source.Position);
        Assert.True(source.CanRead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteLegacyStorageAsync_RejectedHeaderOrFinalPacket_FaultsWithoutRetry(bool headerAccepted)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyStorageSessionAsync(transport);
        transport.Enqueue(headerAccepted ? [0x5A, 0xA5] : [0xA5]);
        using var source = new MemoryStream(new byte[512]);
        var progress = new ProgressRecorder();
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.WriteLegacyStorageAsync(8, 0, 512, source,
            progress: progress, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(headerAccepted ? 4 : 1, transport.Writes.Count - before);
        Assert.Empty(progress.Values);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.LegacyStorage);
    }

    [Theory]
    [InlineData("rpmb")]
    [InlineData("unaligned")]
    [InlineData("past-end")]
    [InlineData("overflow")]
    [InlineData("short-source")]
    [InlineData("zero")]
    public async Task LegacyStorageAsync_InvalidRange_RejectsBeforeIoWithoutFaulting(string failure)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyStorageSessionAsync(transport);
        using var source = new MemoryStream(new byte[512]);
        var partition = failure == "rpmb" ? (byte)3 : (byte)8;
        var offset = failure switch
        {
            "unaligned" => 1L,
            "past-end" => 8192L,
            "overflow" => long.MaxValue - 511,
            _ => 0L
        };
        var length = failure == "short-source" ? 1024 : failure == "zero" ? 0 : 512;
        var before = transport.Writes.Count;

        await Assert.ThrowsAnyAsync<Exception>(() => session.WriteLegacyStorageAsync(partition, offset, length, source,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyStorageAsync_Cancellation_DistinguishesPreIoAndPartialExchange(bool inFlight)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyStorageSessionAsync(transport);
        using var source = new MemoryStream(new byte[512]);
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

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.WriteLegacyStorageAsync(8, 0, 512,
            source, cancellationToken: cancellation.Token));

        Assert.Equal(inFlight ? 1 : 0, transport.Writes.Count - before);
        Assert.Equal(inFlight ? MtkSessionState.Faulted : MtkSessionState.LegacyStorageReady, session.State);
    }

    [Fact]
    public async Task LegacyStorageAsync_ConcurrentRead_WaitsUntilWriteCompletes()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyStorageSessionAsync(transport);
        transport.Enqueue([0x5A, 0x69]);
        transport.Enqueue(Convert.FromHexString("5A015A5A5A"));
        transport.Enqueue(new byte[512]);
        transport.Enqueue([0, 0]);
        using var source = new PausedStorageSource();
        using var destination = new MemoryStream();
        var write = session.WriteLegacyStorageAsync(8, 0, 512, source, cancellationToken: TestContext.Current.CancellationToken);
        Task read;
        try
        {
            await source.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            var before = transport.Writes.Count;
            read = session.ReadLegacyStorageAsync(8, 0, 512, destination, cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(read.IsCompleted);
            Assert.Equal(before, transport.Writes.Count);
        }
        finally
        {
            source.Resume.TrySetResult();
            await write;
        }
        await read;
        Assert.Equal(512, destination.Length);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Fact]
    public async Task EraseLegacyStorageAsync_CompleteProgress_UsesD4AndRequiresFinalAcknowledgements()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyStorageSessionAsync(transport);
        transport.Enqueue(Convert.FromHexString("5A015A5A"));
        transport.Enqueue(Convert.FromHexString("5A5A00000000005A5A00000000645A5A"));
        var before = transport.Writes.Count;

        await session.EraseLegacyStorageAsync(8, 512, 1024, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Convert.FromHexString("D40200000000000000000002000000000000000400"), transport.Writes[before + 3]);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Theory]
    [InlineData("decreasing")]
    [InlineData("out-of-range")]
    [InlineData("missing-final")]
    [InlineData("rejected-final")]
    public async Task EraseLegacyStorageAsync_InvalidProgressOrCompletion_Faults(string failure)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyStorageSessionAsync(transport);
        transport.Enqueue(Convert.FromHexString("5A015A5A"));
        if (failure == "decreasing")
        {
            transport.Enqueue(Convert.FromHexString("5A5A00000000325A5A0000000010"));
        }
        else
        {
            transport.Enqueue(Convert.FromHexString(failure == "out-of-range" ? "5A5A00000000FF" : "5A5A0000000064"));
            if (failure == "rejected-final")
            {
                transport.Enqueue([0x5A, 0xA5]);
            }
        }

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.EraseLegacyStorageAsync(8, 0, 512,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.LegacyStorage);
    }

    [Theory]
    [InlineData(MtkLegacyShutdownMode.PowerOff)]
    [InlineData(MtkLegacyShutdownMode.NormalBoot)]
    [InlineData(MtkLegacyShutdownMode.Fastboot)]
    public async Task ShutdownLegacyAsync_CompleteExchange_UsesSelectedModeAndPreventsReuse(MtkLegacyShutdownMode mode)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyStorageSessionAsync(transport);
        transport.Enqueue([0x5A, 0x5A]);
        var before = transport.Writes.Count;

        await session.ShutdownLegacyAsync(mode, TestContext.Current.CancellationToken);

        Assert.Equal(new byte[] { 0xD9 }, transport.Writes[before]);
        Assert.Equal(new byte[] { 0, 0, 0, (byte)mode }, transport.Writes[before + 1]);
        Assert.False(session.IsConnected);
        Assert.Null(session.LegacyStorage);
        Assert.Equal(MtkSessionState.LegacyShutdownAcknowledged, session.State);
        using var destination = new MemoryStream();
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ReadLegacyStorageAsync(8, 0, 512, destination,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, transport.Writes.Count - before);
    }

    [Fact]
    public async Task ShutdownLegacyAsync_DisconnectFailure_PreservesTerminalState()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await CreateLegacyStorageSessionAsync(transport);
        transport.Enqueue([0x5A, 0x5A]);
        transport.OnDisconnect = () => throw new IOException("Local disconnect failed.");

        await Assert.ThrowsAsync<IOException>(() => session.ShutdownLegacyAsync(MtkLegacyShutdownMode.NormalBoot,
            TestContext.Current.CancellationToken));

        Assert.Equal(MtkSessionState.LegacyShutdownAcknowledged, session.State);
        Assert.True(transport.IsConnected);
        Assert.False(session.IsConnected);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("overflow")]
    [InlineData("alignment")]
    [InlineData("absent")]
    public void LegacyStorageInfo_InvalidGeometry_Rejects(string failure)
    {
        var bytes = LegacyEmmcGeometry();
        switch (failure)
        {
            case "status": bytes[3] = 1; break;
            case "overflow": BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(60), ulong.MaxValue - 511); break;
            case "alignment": bytes[67] = 1; break;
            case "absent": bytes.AsSpan(60, 8).Clear(); break;
        }

        Assert.Throws<MtkProtocolException>(() => MtkLegacyStorageInfo.Parse(bytes));
    }
}