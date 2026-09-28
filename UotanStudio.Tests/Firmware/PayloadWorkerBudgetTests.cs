using System.Collections.Concurrent;
using System.Security.Cryptography;
using Google.Protobuf;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.OtaDelta.Payload;
using UotanStudio.Firmware.OtaDelta.Payload.Proto;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

[Collection("Firmware concurrency")]
public sealed class PayloadWorkerBudgetTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExtractPartitions_FourPartitions_SharesFourWorkerBudgetAndPreservesBytes(bool asynchronous)
    {
        var previous = FirmwareParallelism.Workers;
        FirmwareParallelism.Workers = 4;
        try
        {
            using var temp = new TempDirectory();
            var bytes = new byte[16 * 4096];
            new Random(22).NextBytes(bytes);
            var manifest = new DeltaArchiveManifest { BlockSize = 4096 };
            for (var p = 0; p < 4; p++)
            {
                var partition = new PartitionUpdate { PartitionName = "part" + p };
                for (var i = 0; i < 16; i++)
                {
                    var op = new InstallOperation
                    {
                        Type = InstallOperation.Types.Type.Replace,
                        DataOffset = (ulong)(i * 4096),
                        DataLength = 4096,
                        DataSha256Hash = ByteString.CopyFrom(SHA256.HashData(bytes.AsSpan(i * 4096, 4096)))
                    };
                    op.DstExtents.Add(new Extent { StartBlock = (ulong)i, NumBlocks = 1 });
                    partition.Operations.Add(op);
                }
                manifest.Partitions.Add(partition);
            }
            var handles = new ConcurrentBag<ThreadTrackingStream>();
            Stream Open()
            {
                var handle = new ThreadTrackingStream(bytes);
                handles.Add(handle);
                return handle;
            }
            using var shared = new MemoryStream(bytes, writable: false);
            var extractor = new PayloadExtractor();
            if (asynchronous)
            {
                await extractor.ExtractPartitionsAsync(manifest, shared, 0, [], temp.Path,
                    TestContext.Current.CancellationToken, openPayloadStream: Open);
            }
            else
            {
                extractor.ExtractPartitions(manifest, shared, 0, [], temp.Path, openPayloadStream: Open);
            }
            Assert.Equal(4, handles.Count);
            Assert.All(handles, handle => Assert.Single(handle.Threads));
            for (var p = 0; p < 4; p++)
            {
                Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(temp.Path, "part" + p + ".img"), TestContext.Current.CancellationToken));
            }
        }
        finally
        {
            FirmwareParallelism.Workers = previous;
        }
    }

    private sealed class ThreadTrackingStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        internal ConcurrentDictionary<int, bool> Threads { get; } = new();
        public override int Read(byte[] buffer, int offset, int count)
        {
            Threads.TryAdd(Environment.CurrentManagedThreadId, true);
            return base.Read(buffer, offset, count);
        }
        public override int Read(Span<byte> buffer)
        {
            Threads.TryAdd(Environment.CurrentManagedThreadId, true);
            return base.Read(buffer);
        }
    }
}