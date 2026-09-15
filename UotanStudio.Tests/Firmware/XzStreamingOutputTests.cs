using UotanStudio.Firmware.CrossCutting.Compress;
using UotanStudio.Firmware.CrossCutting.Compress.Compressors;
using UotanStudio.Firmware.CrossCutting.Compress.Internal.Xz;
using Xunit;

namespace UotanStudio.Tests.Firmware;

[Collection("XzSpoolFiles")]
public class XzStreamingOutputTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(4097)]
    [InlineData(2097169)]
    public void Crc32Spooling_PreservesBytesAndRoundTripsOnNonSeekableOutput(int length)
        => AssertSpooling(length, 1, verifyRoundTrip: true);

    [Fact]
    public void Crc64Spooling_PreservesLegacyBytesOnly()
        => AssertSpooling(2097169, 4, verifyRoundTrip: false);

    [Fact]
    public void LargeCompressibleInput_DoesNotFallBackToUncompressedChunks()
    {
        var data = new byte[2 * 1024 * 1024 + 12345];
        for (var i = 0; i < data.Length; i++)
            data[i] = (byte)(i % 251);

        var compressed = XzCompressor.Compress(data, maxDegreeOfParallelism: 1);

        Assert.True(compressed.Length < data.Length * 0.2,
            $"Expected LZMA2 compression below 20% of input, got {compressed.Length} of {data.Length} bytes.");
        Assert.Equal(data, XzCompressor.Decompress(compressed));
    }

    private static void AssertSpooling(int length, byte check, bool verifyRoundTrip)
    {
        byte[] data = new byte[length];
        new Random(123).NextBytes(data);
        byte[] expected = XzCompressor.Compress(data, checkType: check, maxDegreeOfParallelism: 1);
        using var input = new MemoryStream(data);
        using var output = new NonSeekableOutput();
        XzCompressor.Compress(input, output, checkType: check, maxDegreeOfParallelism: 1);
        Assert.Equal(expected, output.Bytes);
        // CRC64 is retained byte-for-byte here; the existing writer uses a different
        // CRC64 variant than XZ and its interoperability defect is outside this optimization.
        // CRC64 此处仅验证旧字节兼容；既有写端 CRC64 变体不符 XZ，另行记录该格式缺陷。
        if (verifyRoundTrip) Assert.Equal(data, XzCompressor.Decompress(output.Bytes));
    }

    [Fact]
    public void Container_RejectsTruncatedCompressedStream()
    {
        using var source = new MemoryStream([0]);
        using var output = new MemoryStream();
        Assert.Throws<EndOfStreamException>(() => XzContainer.WriteContainer(output, source, 2, 0, new byte[4], 8388608));
    }

    [Fact]
    public void OutputFailure_ReleasesTemporarySpool()
    {
        var before = Directory.GetFiles(Path.GetTempPath(), "uotan-xz-*.tmp").ToHashSet(StringComparer.Ordinal);
        using var input = new MemoryStream(new byte[1024]);
        using var output = new FailingOutput();
        Assert.Throws<CompressionException>(() => XzCompressor.Compress(input, output, maxDegreeOfParallelism: 1));
        Assert.DoesNotContain(Directory.GetFiles(Path.GetTempPath(), "uotan-xz-*.tmp"), path => !before.Contains(path));
    }

    private sealed class NonSeekableOutput : Stream
    {
        private readonly MemoryStream _output = new();
        internal byte[] Bytes => _output.ToArray();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => _output.Write(buffer, offset, count);
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _output.Dispose(); base.Dispose(disposing); }
    }

    private sealed class FailingOutput : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("output failed");
    }
}

[CollectionDefinition("XzSpoolFiles", DisableParallelization = true)]
public class XzSpoolFilesCollection;