using System.Buffers.Binary;
using UotanStudio.Comm.Protocols.Adb;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Comm;

public sealed class AdbSyncCopyTests
{
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