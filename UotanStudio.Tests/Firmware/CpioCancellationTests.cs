using UotanStudio.Firmware.Archive.Extraction;
using UotanStudio.Firmware.Archive.Models;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class CpioCancellationTests
{
    [Fact]
    public async Task ExtractToDirectory_CancelDuringPayload_PreservesExistingFile()
    {
        var directory = Directory.CreateTempSubdirectory("cpio-cancel-").FullName;
        var destination = Path.Combine(directory, "image.bin");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var source = new CancellingStream(cancellation);
        try
        {
            await File.WriteAllTextAsync(destination, "previous", TestContext.Current.CancellationToken);
            var entry = new ArchiveEntry
            {
                Name = "image.bin",
                Data = [],
                DataStream = source,
                DeclaredDataLength = 2 * 81920,
                Metadata = new ArchiveEntryMetadata { FileType = CpioFileType.Regular }
            };

            Assert.ThrowsAny<OperationCanceledException>(() => new CpioExtractor().ExtractToDirectory(
                [entry], directory, new ExtractOptions { CancellationToken = cancellation.Token }));

            Assert.Equal("previous", await File.ReadAllTextAsync(destination, TestContext.Current.CancellationToken));
            Assert.Single(Directory.GetFiles(directory));
            Assert.True(source.CanRead);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class CancellingStream(CancellationTokenSource cancellation) : Stream
    {
        private int _reads;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 2 * 81920;
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)0x5A, offset, count);
            if (++_reads == 2)
            {
                cancellation.Cancel();
            }
            return count;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}