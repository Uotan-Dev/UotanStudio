using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk.Rpmb;

public sealed class MtkRpmbLegacyTests
{
    [Fact]
    public async Task ReadLegacyRpmbAsync_ValidRange_SwitchesRpmbPartitionAndVerifiesChecksum()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateLegacyRpmbSessionAsync(transport);
        var data = Enumerable.Repeat((byte)0xCD, 512).ToArray();
        MtkRpmbTestHarness.EnqueueLegacyRpmbRead(transport, data);
        using var destination = new MemoryStream();
        var before = transport.Writes.Count;

        var result = await session.ReadLegacyRpmbAsync(0, 512, destination,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([0x72], transport.Writes[before]);
        Assert.Equal([0x60], transport.Writes[before + 1]);
        Assert.Equal([0x03], transport.Writes[before + 2]);
        var header = transport.Writes[before + 3];
        Assert.Equal(23, header.Length);
        Assert.Equal(0xD6, header[0]);
        Assert.Equal(0x0C, header[1]);
        Assert.Equal(0x02, header[2]);
        Assert.Equal(Convert.FromHexString("0000000000000000000000000000020000000200"),
            header.AsSpan(3).ToArray());
        Assert.Equal([0x5A], transport.Writes[before + 4]);
        Assert.Equal(5, transport.Writes.Count - before);
        Assert.Equal(data, destination.ToArray());
        Assert.Equal(0, result.ByteOffset);
        Assert.Equal(512, result.ByteLength);
        Assert.Equal(3072, result.ReportedRpmbCapacityBytes);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Fact]
    public async Task ReadLegacyRpmbAsync_MultiPacketRange_DeliversEveryByte()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateLegacyRpmbSessionAsync(transport);
        var data = Enumerable.Repeat((byte)0x5A, 1024).ToArray();
        MtkRpmbTestHarness.EnqueueLegacyRpmbRead(transport, data);
        using var destination = new MemoryStream();
        var progress = new List<long>();

        var result = await session.ReadLegacyRpmbAsync(512, 1024, destination, null,
            new ProgressCollector(progress), TestContext.Current.CancellationToken);

        Assert.Equal(1024, destination.Length);
        Assert.Equal(1024, result.ByteLength);
        Assert.Equal(512, result.ByteOffset);
        Assert.Equal([512, 1024], progress);
    }

    [Fact]
    public async Task ReadLegacyRpmbAsync_BadPacketChecksum_FaultsWithoutRetry()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateLegacyRpmbSessionAsync(transport);
        var data = Enumerable.Repeat((byte)0xCD, 512).ToArray();
        MtkRpmbTestHarness.EnqueueLegacyRpmbRead(transport, data, corruptChecksum: true);
        using var destination = new MemoryStream();

        var error = await Assert.ThrowsAsync<MtkProtocolException>(() => session.ReadLegacyRpmbAsync(
            0, 512, destination, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("checksum", error.Message, StringComparison.Ordinal);
        Assert.Equal(MtkSessionState.Faulted, session.State);
        Assert.Null(session.LegacyStorage);
    }

    [Fact]
    public async Task ReadLegacyRpmbAsync_HeaderRejected_FaultsWithoutRetry()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateLegacyRpmbSessionAsync(transport);
        transport.Enqueue([0x5A]);
        transport.Enqueue([0x02]);
        transport.Enqueue([0x5A]);
        transport.Enqueue([0x5A]);
        transport.Enqueue([0xA5]);
        using var destination = new MemoryStream();
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<MtkProtocolException>(() => session.ReadLegacyRpmbAsync(
            0, 512, destination, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(4, transport.Writes.Count - before);
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public async Task ReadLegacyRpmbAsync_RpmbAbsentFromInventory_RefusesBeforeIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateLegacyRpmbSessionAsync(transport, rpmbBytes: 0);
        using var destination = new MemoryStream();
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ReadLegacyRpmbAsync(
            0, 512, destination, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Theory]
    [InlineData(0, 511)]
    [InlineData(1, 512)]
    [InlineData(2816, 512)]
    [InlineData(2560, 1024)]
    public async Task ReadLegacyRpmbAsync_InvalidRange_RefusesBeforeIo(long offset, long length)
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateLegacyRpmbSessionAsync(transport);
        using var destination = new MemoryStream();
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.ReadLegacyRpmbAsync(
            offset, length, destination, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Fact]
    public async Task ReadLegacyRpmbAsync_RangeAboveCeiling_RefusesBeforeIo()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateLegacyRpmbSessionAsync(transport);
        using var destination = new MemoryStream();
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.ReadLegacyRpmbAsync(
            0, 512, destination, new MtkRpmbOptions { MaximumReadFrames = 1 }, null,
            TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
    }

    [Fact]
    public async Task ReadLegacyRpmbAsync_OrdinaryPathStillRefusesRpmbRegion()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateLegacyRpmbSessionAsync(transport);
        using var destination = new MemoryStream();

        await Assert.ThrowsAsync<NotSupportedException>(() => session.ReadLegacyStorageAsync(
            3, 0, 512, destination, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(0, destination.Length);
        Assert.Equal(MtkSessionState.LegacyStorageReady, session.State);
    }

    [Fact]
    public async Task ReadLegacyRpmbAsync_WrongSessionState_SendsNothing()
    {
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateStartedBootloaderSessionAsync(transport);
        using var destination = new MemoryStream();
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ReadLegacyRpmbAsync(
            0, 512, destination, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.Equal(MtkSessionState.Bootloader, session.State);
    }

    private sealed class ProgressCollector(List<long> values) : IProgress<long>
    {
        public void Report(long value) => values.Add(value);
    }
}