using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.IO;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class SparseWriterOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteToStream_SplitRawProvider_ReleasesTemporaryHandleOnSuccessAndFailure(bool fail)
    {
        var root = Path.Combine(Path.GetTempPath(), "uotan-sparse-ownership-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var input = Path.Combine(root, "raw.img");
            await File.WriteAllBytesAsync(input, new byte[4096], TestContext.Current.CancellationToken);
            using var sparse = SparseReader.FromRawFile(input);
            using Stream destination = fail ? new RejectDataStream() : new MemoryStream();
            if (fail)
            {
                Assert.Throws<IOException>(() => SparseWriter.WriteToStream(sparse, destination, cancellationToken: TestContext.Current.CancellationToken));
            }
            else
            {
                SparseWriter.WriteToStream(sparse, destination, cancellationToken: TestContext.Current.CancellationToken);
            }
            using (var exclusive = new FileStream(input, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Equal(4096, exclusive.Length);
            }
            using var retry = new MemoryStream();
            SparseWriter.WriteToStream(sparse, retry, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(retry.Length > 4096);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RejectDataStream : Stream
    {
        private int _writes;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (++_writes == 3)
            {
                throw new IOException("Reject the first RAW payload after its provider has been read.");
            }
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}