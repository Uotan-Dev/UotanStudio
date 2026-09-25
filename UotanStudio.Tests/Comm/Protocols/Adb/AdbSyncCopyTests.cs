using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Adb;

public sealed class AdbSyncCopyTests
{
    [Fact]
    public void DirectStream_WriteBeyondPeerLimit_SplitsWithoutChangingBytes()
    {
        var data = new byte[145];
        new Random(17).NextBytes(data);
        using var transport = new FakeAdbDeviceTransport { PeerMaxPayload = 64 };
        using var adb = new AdbSession(transport, AdbAuthentication.CreateNew());
        adb.Connect();
        using var stream = (AdbStream)adb.OpenStream("shell:cat");

        stream.Write(data);

        Assert.Equal([64, 64, 17], transport.WrtePayloads.Select(packet => packet.Length));
        Assert.Equal(data, transport.WrtePayloads.SelectMany(packet => packet).ToArray());
    }

    [Fact]
    public async Task DirectStream_DelayedOkay_WaitsBeforeNextWrite()
    {
        var firstWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var transport = new FakeAdbDeviceTransport
        {
            PeerMaxPayload = 64,
            AutoAcknowledgeWrites = false,
            OnWrte = _ => firstWrite.TrySetResult()
        };
        using var adb = new AdbSession(transport, AdbAuthentication.CreateNew());
        adb.Connect();
        using var stream = (AdbStream)adb.OpenStream("shell:cat");
        var writing = Task.Run(() => stream.Write(new byte[65]), TestContext.Current.CancellationToken);

        await firstWrite.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(writing.IsCompleted);
        Assert.Single(transport.WrtePayloads);
        transport.Enqueue(AdbMessaging.Serialize(new AdbMessage(AdbCommand.Okay, 1, stream.LocalId)));
        await writing.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal([64, 1], transport.WrtePayloads.Select(packet => packet.Length));
    }

    [Fact]
    public async Task DirectStream_ClosedWhileWaitingForOkay_UnblocksWriter()
    {
        var firstWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var transport = new FakeAdbDeviceTransport
        {
            PeerMaxPayload = 64,
            AutoAcknowledgeWrites = false,
            OnWrte = _ => firstWrite.TrySetResult()
        };
        using var adb = new AdbSession(transport, AdbAuthentication.CreateNew());
        adb.Connect();
        var stream = adb.OpenStream("shell:cat");
        var writing = Task.Run(() => stream.Write(new byte[65]), TestContext.Current.CancellationToken);

        await firstWrite.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        stream.Dispose();

        await Assert.ThrowsAsync<IOException>(() => writing.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Single(transport.WrtePayloads);
    }

    [Fact]
    public async Task DirectStream_PeerClose_ClosesLocallyWithoutEcho()
    {
        using var transport = new FakeAdbDeviceTransport();
        using var adb = new AdbSession(transport, AdbAuthentication.CreateNew());
        adb.Connect();
        using var stream = adb.OpenStream("reboot:now");

        Assert.Null(stream.Read(5000));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(0, transport.CloseCount);
    }

    [Theory]
    [InlineData(0, 17)]
    [InlineData(131079, 65536)]
    [InlineData(4099, 17)]
    public void PushStream_ShortReads_EmitsExactIndependentPackets(int length, int maximumRead)
    {
        var data = new byte[length];
        new Random(42).NextBytes(data);
        using var transport = new FakeAdbDeviceTransport();
        using var adb = new AdbSession(transport, AdbAuthentication.CreateNew());
        adb.Connect();
        using var sync = adb.CreateSyncClient();
        using var source = new ShortReadStream(data, maximumRead);

        sync.PushStream(source, "/simulation/file.bin", mtime: 123);

        var offset = 0;
        foreach (var packet in transport.WrtePayloads)
        {
            if (!packet.AsSpan().StartsWith("DATA"u8))
            {
                continue;
            }
            var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4));
            Assert.InRange(count, 1, maximumRead);
            Assert.Equal(8 + count, packet.Length);
            Assert.True(data.AsSpan(offset, count).SequenceEqual(packet.AsSpan(8)));
            offset += count;
        }
        Assert.Equal(length, offset);
        var done = Assert.Single(transport.WrtePayloads, p => p.AsSpan().StartsWith("DONE"u8));
        Assert.Equal(123u, BinaryPrimitives.ReadUInt32LittleEndian(done.AsSpan(4)));
    }

    private sealed class ShortReadStream(byte[] bytes, int maximumRead) : MemoryStream(bytes, writable: false)
    {
        public override int Read(byte[] buffer, int offset, int count)
            => base.Read(buffer, offset, Math.Min(count, maximumRead));
    }
}