using UotanStudio.Services.Execution;
using UotanStudio.Services.Projects;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class NodeArtifactPublisherTests
{
    [Fact]
    public async Task PublishAsync_TextLooksLikeExistingPath_WritesLiteralText()
    {
        var directory = Directory.CreateTempSubdirectory("uotan-publish-").FullName;
        var source = Path.Combine(directory, "source.txt");
        var destination = Path.Combine(directory, "result.txt");
        var token = TestContext.Current.CancellationToken;
        try
        {
            await File.WriteAllTextAsync(source, "source contents must not leak", token);

            await NodeArtifactPublisher.PublishAsync(source, destination, token);

            Assert.Equal(source, await File.ReadAllTextAsync(destination, token));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PublishAsync_FileInput_PreservesBytesAndReplacesDestination()
    {
        var directory = Directory.CreateTempSubdirectory("uotan-publish-").FullName;
        var source = Path.Combine(directory, "source.bin");
        var destination = Path.Combine(directory, "result.bin");
        var token = TestContext.Current.CancellationToken;
        try
        {
            var bytes = Enumerable.Range(0, 512 * 1024).Select(value => (byte)value).ToArray();
            await File.WriteAllBytesAsync(source, bytes, token);
            await File.WriteAllTextAsync(destination, "previous", token);

            var length = await NodeArtifactPublisher.PublishAsync(new FileArtifact(source), destination, token);

            Assert.Equal(bytes.Length, length);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(destination, token));
            Assert.Equal(2, Directory.GetFiles(directory).Length);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PublishAsync_SourceFailsAfterPartialCopy_PreservesDestinationAndCleansTemporary()
    {
        var directory = Directory.CreateTempSubdirectory("uotan-publish-").FullName;
        var destination = Path.Combine(directory, "result.bin");
        var token = TestContext.Current.CancellationToken;
        try
        {
            await File.WriteAllTextAsync(destination, "previous", token);
            await using var source = new FailingStream();

            await Assert.ThrowsAsync<IOException>(() => NodeArtifactPublisher.PublishAsync(source, destination, token));

            Assert.Equal("previous", await File.ReadAllTextAsync(destination, token));
            Assert.Single(Directory.GetFiles(directory));
            Assert.InRange(source.MaximumReadSize, 1, 128 * 1024);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PublishAsync_CancelDuringCopy_PreservesDestinationAndLeavesSourceOpen()
    {
        var directory = Directory.CreateTempSubdirectory("uotan-publish-").FullName;
        var destination = Path.Combine(directory, "result.bin");
        var token = TestContext.Current.CancellationToken;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            await File.WriteAllTextAsync(destination, "previous", token);
            await using var source = new FailingStream(cancellation);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NodeArtifactPublisher.PublishAsync(source, destination, cancellation.Token));

            Assert.Equal("previous", await File.ReadAllTextAsync(destination, token));
            Assert.True(source.CanRead);
            Assert.Single(Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FailingStream(CancellationTokenSource? cancellation = null) : Stream
    {
        private int _reads;
        public int MaximumReadSize { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            MaximumReadSize = Math.Max(MaximumReadSize, buffer.Length);
            cancellationToken.ThrowIfCancellationRequested();
            if (_reads++ > 0)
            {
                throw new IOException("Source interrupted.");
            }
            buffer.Span[..Math.Min(32, buffer.Length)].Fill(0x5A);
            cancellation?.Cancel();
            return ValueTask.FromResult(Math.Min(32, buffer.Length));
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}