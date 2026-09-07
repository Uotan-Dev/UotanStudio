using System.Security.Cryptography;
using Google.Protobuf;
using UotanStudio.Firmware.CrossCutting.Compress;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Core;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Exceptions;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Proto;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class PayloadStreamingTests
{
    [Fact]
    public void Replace_StreamsAcrossMultipleDestinationExtents()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var operation = CreateOperation(InstallOperation.Types.Type.Replace, payload, 4,
            new Extent { StartBlock = 0, NumBlocks = 1 },
            new Extent { StartBlock = 2, NumBlocks = 1 });

        using var payloadStream = new MemoryStream(payload);
        using var output = CreateTemporaryFile(12, out var path);
        try
        {
            OperationProcessor.ProcessOperation(operation, output, payloadStream, 0, 4, new object(), new object(), TestContext.Current.CancellationToken);
            output.Position = 0;
            var actual = new byte[12];
            output.ReadExactly(actual);

            Assert.Equal(new byte[] { 1, 2, 3, 4, 0, 0, 0, 0, 5, 6, 7, 8 }, actual);
        }
        finally
        {
            output.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void Replace_ChecksCancellationBetweenChunks()
    {
        var payload = new byte[128 * 1024];
        using var cancellation = new CancellationTokenSource();
        using var payloadStream = new CancellingReadStream(payload, cancellation);
        var operation = CreateOperation(InstallOperation.Types.Type.Replace, payload, 4096,
            new Extent { StartBlock = 0, NumBlocks = 32 });
        using var output = CreateTemporaryFile(payload.Length, out var path);
        try
        {
            Assert.Throws<OperationCanceledException>(() =>
                OperationProcessor.ProcessOperation(operation, output, payloadStream, 0, 4096,
                    new object(), new object(), cancellation.Token));
        }
        finally
        {
            output.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Replace_HoldsOutputLockUntilEntireStreamAndHashAreComplete()
    {
        var payload = Enumerable.Range(0, 128 * 1024).Select(i => (byte)i).ToArray();
        using var secondReadReached = new ManualResetEventSlim();
        using var allowSecondRead = new ManualResetEventSlim();
        using var lockAttempted = new ManualResetEventSlim();
        using var lockAcquired = new ManualResetEventSlim();
        using var payloadStream = new PausingReadStream(payload, secondReadReached, allowSecondRead);
        var operation = CreateOperation(InstallOperation.Types.Type.Replace, payload, 4096,
            new Extent { StartBlock = 0, NumBlocks = 32 });
        var writeLock = new object();
        using var output = CreateTemporaryFile(payload.Length, out var path);
        var replaceTask = Task.CompletedTask;
        var competingOperation = Task.CompletedTask;
        try
        {
            // Both workers intentionally block; use dedicated threads to avoid thread-pool starvation.
            replaceTask = Task.Factory.StartNew(() => OperationProcessor.ProcessOperation(
                operation, output, payloadStream, 0, 4096, writeLock, new object(), TestContext.Current.CancellationToken),
                TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            Assert.True(secondReadReached.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            competingOperation = Task.Factory.StartNew(() =>
            {
                lockAttempted.Set();
                lock (writeLock)
                {
                    lockAcquired.Set();
                }
            }, TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            Assert.True(lockAttempted.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.False(lockAcquired.Wait(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken));

            allowSecondRead.Set();
            await replaceTask;
            Assert.True(lockAcquired.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            await competingOperation;
        }
        finally
        {
            allowSecondRead.Set();
            try { await Task.WhenAll(replaceTask, competingOperation); }
            finally
            {
                output.Dispose();
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void ReplaceBzip2_StreamsIntoDestinationExtents()
    {
        var expected = Enumerable.Range(0, 8192).Select(i => (byte)(i * 17)).ToArray();
        var compressed = CompressionService.Compress(expected, CompressionFormat.Bzip2);
        var operation = CreateOperation(InstallOperation.Types.Type.ReplaceBz, compressed, 4096,
            new Extent { StartBlock = 0, NumBlocks = 1 },
            new Extent { StartBlock = 2, NumBlocks = 1 });

        using var payloadStream = new MemoryStream(compressed);
        using var output = CreateTemporaryFile(12288, out var path);
        try
        {
            OperationProcessor.ProcessOperation(operation, output, payloadStream, 0, 4096, new object(), new object(), TestContext.Current.CancellationToken);
            output.Position = 0;
            var first = new byte[4096];
            output.ReadExactly(first);
            output.Position = 8192;
            var second = new byte[4096];
            output.ReadExactly(second);

            Assert.Equal(expected.AsSpan(0, 4096).ToArray(), first);
            Assert.Equal(expected.AsSpan(4096, 4096).ToArray(), second);
        }
        finally
        {
            output.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void ReplaceCompressed_RejectsOutputBeyondDestinationExtents()
    {
        var expanded = new byte[8192];
        var compressed = CompressionService.Compress(expanded, CompressionFormat.Bzip2);
        var operation = CreateOperation(InstallOperation.Types.Type.ReplaceBz, compressed, 4096,
            new Extent { StartBlock = 0, NumBlocks = 1 });

        using var payloadStream = new MemoryStream(compressed);
        using var output = CreateTemporaryFile(4096, out var path);
        try
        {
            Assert.ThrowsAny<Exception>(() =>
                OperationProcessor.ProcessOperation(operation, output, payloadStream, 0, 4096, new object(), new object(), TestContext.Current.CancellationToken));
        }
        finally
        {
            output.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void SourceDependentOperation_RejectsParallelPathBeforeReadingData()
    {
        var operation = new InstallOperation
        {
            Type = InstallOperation.Types.Type.Puffdiff,
            DataLength = (ulong)int.MaxValue + 1
        };

        using var payloadStream = new SparseLengthStream((long)int.MaxValue + 1);
        using var output = CreateTemporaryFile(0, out var path);
        try
        {
            Assert.Throws<PayloadUnsupportedOperationException>(() =>
                OperationProcessor.ProcessOperation(operation, output, payloadStream, 0, 4096, new object(), new object(), TestContext.Current.CancellationToken));
        }
        finally
        {
            output.Dispose();
            File.Delete(path);
        }
    }

    private static InstallOperation CreateOperation(
        InstallOperation.Types.Type type,
        byte[] operationData,
        uint blockSize,
        params Extent[] extents)
    {
        var operation = new InstallOperation
        {
            Type = type,
            DataLength = (ulong)operationData.Length,
            DataSha256Hash = ByteString.CopyFrom(SHA256.HashData(operationData))
        };
        operation.DstExtents.Add(extents);
        operation.DstLength = (ulong)extents.Sum(extent => checked((long)extent.NumBlocks * blockSize));
        return operation;
    }

    private static FileStream CreateTemporaryFile(long length, out string path)
    {
        path = Path.Combine(Path.GetTempPath(), $"uotan-payload-{Guid.NewGuid():N}.tmp");
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        stream.SetLength(length);
        return stream;
    }

    private sealed class CancellingReadStream(byte[] data, CancellationTokenSource cancellation) : Stream
    {
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => _position = value; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(Math.Min(count, 4096), data.Length - (int)_position);
            data.AsSpan((int)_position, read).CopyTo(buffer.AsSpan(offset, read));
            _position += read;
            cancellation.Cancel();
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) =>
            _position = origin == SeekOrigin.Begin ? offset : throw new NotSupportedException();
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class PausingReadStream(
        byte[] data,
        ManualResetEventSlim secondReadReached,
        ManualResetEventSlim allowSecondRead) : Stream
    {
        private long _position;
        private int _readCount;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => _position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Interlocked.Increment(ref _readCount) == 2)
            {
                secondReadReached.Set();
                Assert.True(allowSecondRead.Wait(TimeSpan.FromSeconds(10)));
            }

            var read = Math.Min(count, data.Length - (int)_position);
            data.AsSpan((int)_position, read).CopyTo(buffer.AsSpan(offset, read));
            _position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            _position = origin == SeekOrigin.Begin ? offset : throw new NotSupportedException();
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class SparseLengthStream(long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length { get; } = length;
        public override long Position { get => _position; set => _position = value; }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(Length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            return _position;
        }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}