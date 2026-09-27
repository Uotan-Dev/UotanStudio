using System.Buffers.Binary;
using System.Security.Cryptography;
using ICSharpCode.SharpZipLib.BZip2;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Builder;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Proto;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class PayloadMovedBlocksTests
{
    private const int BlockSize = 4096;

    [Theory]
    [InlineData(0)]
    [InlineData(137)]
    public async Task Build_MovedAndEditedBlocks_IndependentReplayMatchesTarget(int prefix)
    {
        using var temp = new TempDirectory();
        var old = new byte[300 * BlockSize];
        new Random(921).NextBytes(old);
        var target = new byte[old.Length + BlockSize];
        old.AsSpan(128 * BlockSize).CopyTo(target);
        old.AsSpan(0, 128 * BlockSize).CopyTo(target.AsSpan(172 * BlockSize));
        old.AsSpan(200 * BlockSize, BlockSize).CopyTo(target.AsSpan(300 * BlockSize));
        old.AsSpan(10 * BlockSize, BlockSize).CopyTo(target.AsSpan(10 * BlockSize));
        target[10 * BlockSize + 17] ^= 1;
        new Random(172).NextBytes(target.AsSpan(20 * BlockSize, BlockSize));
        using var sourceStream = new MemoryStream(new byte[prefix].Concat(old).ToArray());
        using var targetStream = new MemoryStream(new byte[prefix].Concat(target).ToArray());
        sourceStream.Position = prefix;
        targetStream.Position = prefix;
        using var builder = new PayloadBuilder();
        builder.AddPartitionIncremental("system", targetStream, sourceStream);
        var payload = Path.Combine(temp.Path, "payload.bin");
        await builder.BuildAsync(payload, TestContext.Current.CancellationToken);

        using var input = File.OpenRead(payload);
        var header = new byte[24];
        input.ReadExactly(header);
        Assert.Equal("CrAU"u8.ToArray(), header[..4]);
        var manifestBytes = new byte[checked((int)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(12)))];
        input.ReadExactly(manifestBytes);
        var dataStart = input.Position + BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20));
        var manifest = DeltaArchiveManifest.Parser.ParseFrom(manifestBytes);
        var partition = Assert.Single(manifest.Partitions);
        Assert.NotNull(partition.OldPartitionInfo);
        Assert.NotNull(partition.NewPartitionInfo);
        var oldInfo = partition.OldPartitionInfo!;
        var newInfo = partition.NewPartitionInfo!;
        Assert.Equal(SHA256.HashData(old), oldInfo.Hash.ToByteArray());
        Assert.Equal((ulong)old.Length, oldInfo.Size);
        Assert.Contains(partition.Operations, operation => operation.Type == InstallOperation.Types.Type.SourceCopy
            && operation.SrcExtents[0].StartBlock != operation.DstExtents[0].StartBlock);
        Assert.Contains(partition.Operations, operation => operation.Type == InstallOperation.Types.Type.SourceBsdiff);
        Assert.Contains(partition.Operations, operation => operation.Type == InstallOperation.Types.Type.Replace);
        Assert.True(partition.Operations.Count < 20);
        var replay = new byte[target.Length];
        var covered = new bool[target.Length / BlockSize];
        foreach (var operation in partition.Operations)
        {
            var destination = Assert.Single(operation.DstExtents);
            var count = checked((int)destination.NumBlocks * BlockSize);
            byte[] bytes;
            byte[] source = [];
            if (operation.SrcExtents.Count != 0)
            {
                var extent = Assert.Single(operation.SrcExtents);
                source = old.AsSpan(checked((int)extent.StartBlock * BlockSize),
                    checked((int)extent.NumBlocks * BlockSize)).ToArray();
                Assert.Equal(SHA256.HashData(source), operation.SrcSha256Hash.ToByteArray());
            }
            if (operation.Type == InstallOperation.Types.Type.SourceCopy)
            {
                Assert.Equal(0UL, operation.DataLength);
                bytes = source;
            }
            else
            {
                input.Position = checked(dataStart + (long)operation.DataOffset);
                var blob = new byte[checked((int)operation.DataLength)];
                input.ReadExactly(blob);
                Assert.Equal(SHA256.HashData(blob), operation.DataSha256Hash.ToByteArray());
                bytes = operation.Type == InstallOperation.Types.Type.SourceBsdiff
                    ? ReplayBsdiff(source, blob) : blob;
            }
            Assert.Equal(count, bytes.Length);
            bytes.CopyTo(replay, checked((int)destination.StartBlock * BlockSize));
            for (var i = 0UL; i < destination.NumBlocks; i++)
            {
                var block = checked((int)(destination.StartBlock + i));
                Assert.False(covered[block]);
                covered[block] = true;
            }
        }
        Assert.All(covered, Assert.True);
        Assert.Equal(target, replay);
        Assert.Equal(SHA256.HashData(replay), newInfo.Hash.ToByteArray());
    }

    private static byte[] ReplayBsdiff(byte[] source, byte[] patch)
    {
        Assert.Equal("BSDIFF40"u8.ToArray(), patch[..8]);
        var controlLength = checked((int)ReadSigned(patch.AsSpan(8)));
        var diffLength = checked((int)ReadSigned(patch.AsSpan(16)));
        var result = new byte[checked((int)ReadSigned(patch.AsSpan(24)))];
        using var controls = new BZip2InputStream(new MemoryStream(patch, 32, controlLength));
        using var differences = new BZip2InputStream(new MemoryStream(patch, 32 + controlLength, diffLength));
        using var extras = new BZip2InputStream(new MemoryStream(patch, 32 + controlLength + diffLength,
            patch.Length - 32 - controlLength - diffLength));
        long oldPosition = 0;
        var position = 0;
        var tuple = new byte[24];
        while (position < result.Length)
        {
            controls.ReadExactly(tuple);
            var diff = checked((int)ReadSigned(tuple));
            var extra = checked((int)ReadSigned(tuple.AsSpan(8)));
            differences.ReadExactly(result.AsSpan(position, diff));
            for (var i = 0; i < diff; i++)
            {
                if (oldPosition + i >= 0 && oldPosition + i < source.Length)
                {
                    result[position + i] = unchecked((byte)(result[position + i] + source[checked((int)(oldPosition + i))]));
                }
            }
            position += diff;
            oldPosition += diff;
            extras.ReadExactly(result.AsSpan(position, extra));
            position += extra;
            oldPosition += ReadSigned(tuple.AsSpan(16));
        }
        return result;
    }

    private static long ReadSigned(ReadOnlySpan<byte> bytes)
    {
        var raw = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        var magnitude = checked((long)(raw & long.MaxValue));
        return (raw & (1UL << 63)) == 0 ? magnitude : -magnitude;
    }
}