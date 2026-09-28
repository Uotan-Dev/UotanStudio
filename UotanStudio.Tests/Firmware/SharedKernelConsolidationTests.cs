using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Checksums;
using UotanStudio.Firmware.Core.Streams;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public class Crc32cTests
{
    [Fact]
    public void Compute_KnownVector_MatchesCrc32cRegisterSemantics()
    {
        Assert.Equal(0x1CF96D7Cu, Crc32c.Compute("123456789"u8));
    }

    [Fact]
    public void Compute_DisjointSpans_ContinuationEqualsSinglePass()
    {
        var single = Crc32c.Compute("123456789"u8);
        var continued = Crc32c.Compute("6789"u8, Crc32c.Compute("12345"u8));

        Assert.Equal(single, continued);
    }

    [Fact]
    public void Compute_EmptySpan_ReturnsSeedUnchanged()
    {
        Assert.Equal(0x5045B54Au, Crc32c.Compute(ReadOnlySpan<byte>.Empty, 0x5045B54Au));
    }
}

public class Crc32Tests
{
    [Fact]
    public void Compute_KnownVector_MatchesCrc32RegisterSemantics()
    {
        Assert.Equal(0x340BC6D9u, Crc32.Compute("123456789"u8, 0xFFFF_FFFFu));
    }

    [Fact]
    public void Compute_CustomSeed_MatchesBitwiseReference()
    {
        var seed = 0xF2F52010u;

        var tabled = Crc32.Compute("0123456789abcdef"u8, seed);

        var bitwise = seed;
        foreach (var b in "0123456789abcdef"u8)
        {
            bitwise ^= b;
            for (var j = 0; j < 8; j++)
            {
                bitwise = (bitwise & 1) != 0 ? (bitwise >> 1) ^ 0xEDB88320u : bitwise >> 1;
            }
        }

        Assert.Equal(bitwise, tabled);
    }
}

public class BinaryReaderHelpersTests
{
    [Fact]
    public void ReadUInt32LE_Stream_DecodesLittleEndianBytes()
    {
        using var stream = new MemoryStream([0x01, 0x02, 0x03, 0x04]);

        Assert.Equal(0x04030201u, BinaryReaderHelpers.ReadUInt32LE(stream));
    }

    [Fact]
    public void ReadUInt16LE_Stream_TruncatedStream_ThrowsEndOfStream()
    {
        using var stream = new MemoryStream([0x01]);

        Assert.Throws<EndOfStreamException>(() => BinaryReaderHelpers.ReadUInt16LE(stream));
    }

    [Fact]
    public void ReadUInt32LE_Buffer_OffsetReadsFieldBytes()
    {
        byte[] buffer = [0xFF, 0x01, 0x02, 0x03, 0x04];

        Assert.Equal(0x04030201u, BinaryReaderHelpers.ReadUInt32LE(buffer, 1));
    }

    [Fact]
    public void ReadUInt32LE_Buffer_OffsetBeyondField_Throws()
    {
        byte[] buffer = [0x01, 0x02, 0x03];

        Assert.Throws<ArgumentOutOfRangeException>(() => BinaryReaderHelpers.ReadUInt32LE(buffer, 0));
    }

    [Fact]
    public void ReadUInt32BE_Buffer_DecodesBigEndianBytes()
    {
        byte[] buffer = [0x01, 0x02, 0x03, 0x04];

        Assert.Equal(0x01020304u, BinaryReaderHelpers.ReadUInt32BE(buffer, 0));
    }

    [Fact]
    public void ReadUInt64LE_Buffer_DecodesLittleEndianBytes()
    {
        byte[] buffer = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];

        Assert.Equal(0x0807060504030201ul, BinaryReaderHelpers.ReadUInt64LE(buffer, 0));
    }

    [Fact]
    public async Task ReadUInt32BEAsync_Stream_DecodesBigEndianBytes()
    {
        using var stream = new MemoryStream([0x01, 0x02, 0x03, 0x04]);

        var value = await BinaryReaderHelpers.ReadUInt32BEAsync(stream, TestContext.Current.CancellationToken);

        Assert.Equal(0x01020304u, value);
    }

    [Fact]
    public async Task ReadUInt64BEAsync_Stream_TruncatedStream_ThrowsEndOfStream()
    {
        using var stream = new MemoryStream([0x01, 0x02]);

        await Assert.ThrowsAsync<EndOfStreamException>(() => BinaryReaderHelpers.ReadUInt64BEAsync(stream, TestContext.Current.CancellationToken));
    }
}

public class HeaderProbeTests : IDisposable
{
    private readonly string _filePath = Path.Combine(Path.GetTempPath(), $"uotan-probe-{Guid.NewGuid():N}.bin");

    public void Dispose()
    {
        File.Delete(_filePath);
    }

    [Fact]
    public void ProbeMagic_FileCarriesLeUintAtOffset_ReturnsTrue()
    {
        File.WriteAllBytes(_filePath, [0, 0, 0, 0, 0, 0, 0, 0, 0x56, 0x19, 0xB5, 0x27]);

        Assert.True(HeaderProbe.ProbeMagic(_filePath, 12, 8, 0x27B51956u));
    }

    [Fact]
    public void ProbeMagic_ByteMagicAtOffset_ReturnsTrue()
    {
        File.WriteAllBytes(_filePath, [0xAA, (byte)'C', (byte)'r', (byte)'A', (byte)'U']);

        Assert.True(HeaderProbe.ProbeMagic(_filePath, 0, 1, "CrAU"u8));
    }

    [Fact]
    public void ProbeMagic_FileShorterThanMinimum_ReturnsFalse()
    {
        File.WriteAllBytes(_filePath, [0x56, 0x19, 0xB5, 0x27]);

        Assert.False(HeaderProbe.ProbeMagic(_filePath, 12, 0, 0x27B51956u));
    }

    [Fact]
    public void ProbeMagic_WrongMagic_ReturnsFalse()
    {
        File.WriteAllBytes(_filePath, [0x01, 0x02, 0x03, 0x04]);

        Assert.False(HeaderProbe.ProbeMagic(_filePath, 0, 0, 0x27B51956u));
    }

    [Fact]
    public void ProbeMagic_MissingFile_ReturnsFalse()
    {
        Assert.False(HeaderProbe.ProbeMagic(_filePath, 0, 0, 0x27B51956u));
    }
}

public class CancellationStreamTests
{
    [Fact]
    public void CancellationReadStream_CanceledToken_ReadThrowsOperationCanceled()
    {
        using var inner = new MemoryStream([0x01]);
        using var stream = new CancellationReadStream(inner, new CancellationToken(canceled: true));

        Assert.Throws<OperationCanceledException>(() => stream.Read([0], 0, 1));
    }

    [Fact]
    public void CancellationReadStream_LiveToken_ReadsPassThrough()
    {
        using var inner = new MemoryStream([0x01, 0x02]);
        using var stream = new CancellationReadStream(inner, CancellationToken.None);

        Assert.Equal(2, stream.Read(new byte[2], 0, 2));
    }

    [Fact]
    public void CancellationWriteStream_CanceledToken_WriteThrowsOperationCanceled()
    {
        using var inner = new MemoryStream();
        using var stream = new CancellationWriteStream(inner, new CancellationToken(canceled: true));

        Assert.Throws<OperationCanceledException>(() => stream.Write([0x01], 0, 1));
    }

    [Fact]
    public void CancellationWriteStream_LiveToken_WritePassesThroughAndPositionFollows()
    {
        using var inner = new MemoryStream();
        using var stream = new CancellationWriteStream(inner, CancellationToken.None);

        stream.Write([0x01, 0x02], 0, 2);

        Assert.Equal(2, inner.Length);
        Assert.Equal(2, stream.Position);
    }

    [Fact]
    public void CappedWriteStream_UnderLimit_WritesAndCounts()
    {
        using var inner = new MemoryStream();
        using var stream = new CappedWriteStream(inner, 16);

        stream.Write([0x01, 0x02, 0x03], 0, 3);

        Assert.Equal(3, stream.Written);
        Assert.Equal(3, inner.Length);
    }

    [Fact]
    public void CappedWriteStream_OverLimit_ThrowsInvalidDataWithoutWriting()
    {
        using var inner = new MemoryStream();
        using var stream = new CappedWriteStream(inner, 4);

        Assert.Throws<InvalidDataException>(() => stream.Write([0x01, 0x02, 0x03, 0x04, 0x05], 0, 5));
        Assert.Equal(0, inner.Length);
    }
}

public class FirmwareProgressAdapterTests
{
    private sealed class ProgressSink : IFirmwareProgress
    {
        public double Percent { get; private set; }

        public string Message { get; private set; } = string.Empty;

        public void Report(double percent, string message)
        {
            Percent = percent;
            Message = message;
        }
    }

    [Fact]
    public void FirmwareProgressAdapter_Report_ScalesFractionAndAttachesLabel()
    {
        var sink = new ProgressSink();
        IProgress<double> adapter = new FirmwareProgressAdapter(sink, "Unpack DAT");

        adapter.Report(0.5);

        Assert.Equal(50, sink.Percent);
        Assert.Equal("Unpack DAT", sink.Message);
    }

    [Fact]
    public void CallbackProgress_Report_ForwardsValueSynchronously()
    {
        var observed = new List<int>();
        IProgress<int> progress = new CallbackProgress<int>(observed.Add);

        progress.Report(7);

        Assert.Equal([7], observed);
    }
}