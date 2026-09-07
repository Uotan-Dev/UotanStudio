using UotanStudio.Firmware.Oppo.Crypto;
using UotanStudio.Firmware.Oppo.Models;
using UotanStudio.Firmware.Oppo;
using UotanStudio.Firmware.Oppo.Parsers;
using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class OfpStreamingConformanceTests
{
    [Fact]
    public void PlainQcMetadataAndEntriesSurviveShortReads()
    {
        var xml = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><root><Firmware><file Path=\"entry.bin\" FileOffsetInSrc=\"1\" SizeInByteInSrc=\"16\"/></Firmware></root>".PadRight(256));
        var data = new byte[1024];
        xml.CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(512 + 0x10), 0x7cef);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(512 + 0x18), (uint)xml.Length);
        using var source = new ShortReadStream(data);
        using var crypto = new OfpQcCryptoProvider();
        var parser = new OfpQcFormatParser(crypto);
        var metadata = parser.ParseMetadata(source, source.Length, null);
        Assert.Equal("plaintext", metadata.KeyId);
        Assert.Equal(1, metadata.EntryCount);
        var entry = Assert.Single(parser.ParseEntries(source, source.Length, metadata, null));
        Assert.Equal(512, entry.Offset);
        Assert.Equal(16, entry.Size);
        Assert.Equal(0, source.Position);
    }

    [Theory]
    [InlineData(0U, 16777217U)]
    [InlineData(0U, uint.MaxValue)]
    [InlineData(uint.MaxValue, 256U)]
    [InlineData(0U, 0U)]
    public void QcXmlRejectsOversizedAndInvalidRanges(uint sectors, uint length)
    {
        using var source = new MemoryStream(new byte[512]);
        var tail = new OfpQcTailPage { PageSize = 512, XmlSectorOffset = sectors, XmlLength = length };
        Assert.Throws<InvalidOppFormatException>(() => OfpQcParser.ReadXmlData(source, tail, source.Length));
        Assert.Equal(0, source.Position);
    }

    [Theory]
    [InlineData("9223372036854775807", "1")]
    [InlineData("1", "9223372036854775807")]
    [InlineData("-1", "1")]
    public void QcXmlRejectsInvalidSectorArithmetic(string offset, string size)
    {
        var xml = $"<root><Firmware><file Path=\"entry.bin\" FileOffsetInSrc=\"{offset}\" SizeInSectorInSrc=\"{size}\"/></Firmware></root>";
        Assert.Throws<InvalidOppFormatException>(() => OfpQcParser.ParseEntries(xml, 4096));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EncryptedPrefixAndPlainTailSurviveShortReads(bool qc)
    {
        var data = new byte[1024 * 1024 + 17];
        new Random(124).NextBytes(data);
        using var mtk = new OfpMtkCryptoProvider();
        using var qualcomm = new OfpQcCryptoProvider();
        var key = qc ? "V1.4.17" : "MTK0";
        const int encryptedSize = 1024 * 1024;
        var encrypted = qc ? qualcomm.Encrypt(data[..encryptedSize], key) : mtk.Encrypt(data[..encryptedSize], key);
        using var input = new ShortReadStream(encrypted.Concat(data[encryptedSize..]).ToArray());
        using var output = new MemoryStream();
        IOppFormatParser parser = qc ? new OfpQcFormatParser(qualcomm) : new OfpMtkFormatParser(mtk);
        parser.ExtractEntry(input, output, new OppEntry
        {
            Name = "partition",
            Size = data.Length,
            EncryptedSize = encryptedSize
        }, new OppMetadata(), key, null);
        Assert.Equal(data, output.ToArray());
        Assert.Equal(0, input.Position);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TruncatedEntryIsRejectedBeforeWriting(bool qc)
    {
        using var mtk = new OfpMtkCryptoProvider();
        using var qualcomm = new OfpQcCryptoProvider();
        IOppFormatParser parser = qc ? new OfpQcFormatParser(qualcomm) : new OfpMtkFormatParser(mtk);
        using var input = new MemoryStream(new byte[32]);
        using var output = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => parser.ExtractEntry(input, output,
            new OppEntry { Name = "partition", Size = 33 }, new OppMetadata(), null, null));
        Assert.Equal(0, output.Length);
    }

    private sealed class ShortReadStream(byte[] data) : MemoryStream(data, writable: false)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            Assert.True(count <= 81920, "OFP extraction requested a whole encrypted payload buffer.");
            return base.Read(buffer, offset, Math.Min(count, 7));
        }
        public override int Read(Span<byte> buffer)
        {
            Assert.True(buffer.Length <= 81920, "OFP extraction requested a whole encrypted payload buffer.");
            return base.Read(buffer[..Math.Min(buffer.Length, 7)]);
        }
    }
}