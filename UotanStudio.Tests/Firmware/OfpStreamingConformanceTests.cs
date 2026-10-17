using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.VendorFirmware.Oppo;
using UotanStudio.Firmware.VendorFirmware.Oppo.Crypto;
using UotanStudio.Firmware.VendorFirmware.Oppo.Exceptions;
using UotanStudio.Firmware.VendorFirmware.Oppo.Models;
using UotanStudio.Firmware.VendorFirmware.Oppo.Parsers;
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

    [Fact]
    public void AutoDecryptQcXml_DeclarationAfterFirstBlocks_SelectsSameKey()
    {
        using var provider = new OfpQcCryptoProvider();
        var xml = Encoding.UTF8.GetBytes(new string('x', 4096) +
            "<?xml version=\"1.0\"?><root><Firmware><file Path=\"entry.bin\" FileOffsetInSrc=\"1\" SizeInByteInSrc=\"16\"/></Firmware></root>");
        var encrypted = provider.Encrypt(xml, "V1.4.17");

        var result = provider.AutoDecryptQcXml(encrypted);

        Assert.NotNull(result);
        Assert.Equal("V1.4.17", result.Value.KeyId);
        Assert.Contains("<?xml", result.Value.Xml);
        Assert.EndsWith("</root>", result.Value.Xml);
    }

    [Fact]
    public void QcParseEntries_AfterParseMetadata_DoesNotReadTheStreamAgain()
    {
        var xml = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><root><Firmware><file Path=\"entry.bin\" FileOffsetInSrc=\"1\" SizeInByteInSrc=\"16\"/></Firmware></root>".PadRight(256));
        var data = new byte[1024];
        xml.CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(512 + 0x10), 0x7cef);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(512 + 0x18), (uint)xml.Length);
        using var source = new CountingReadStream(data);
        using var crypto = new OfpQcCryptoProvider();
        var parser = new OfpQcFormatParser(crypto);
        var metadata = parser.ParseMetadata(source, source.Length, null);
        var bytesRead = source.BytesRead;

        var entries = parser.ParseEntries(source, source.Length, metadata, null);

        Assert.Equal(bytesRead, source.BytesRead);
        Assert.Single(entries);
    }

    [Fact]
    public void ZipWrappedOfp_ExtractionReadsWrapperOnce()
    {
        var payload = new byte[256 * 1024];
        new Random(7).NextBytes(payload);
        using var zipBytes = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(zipBytes, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = archive.CreateEntry("inner.ofp", System.IO.Compression.CompressionLevel.NoCompression).Open();
            entry.Write(PlainMtkOfp(payload));
        }

        var zip = zipBytes.ToArray();
        var output = Path.Combine(Path.GetTempPath(), "uotan-zip-once-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var source = new CountingReadStream(zip);
            var result = new OppReader().ExtractFromStream(source, output);

            Assert.Equal(1, result.ExtractedFileCount);
            Assert.Equal(payload, File.ReadAllBytes(Path.Combine(output, "entry.bin")));
            Assert.True(source.BytesRead < zip.Length * 2,
                $"ZIP wrapper was read {source.BytesRead} bytes for a {zip.Length}-byte archive.");
        }
        finally
        {
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
        }
    }

    private static byte[] PlainMtkOfp() => PlainMtkOfp(Enumerable.Repeat((byte)0x52, 16).ToArray());

    private static byte[] PlainMtkOfp(byte[] payload)
    {
        var result = new byte[payload.Length + 0x60 + 0x6c];
        payload.CopyTo(result, 0);
        var entry = result.AsSpan(payload.Length, 0x60);
        "entry.bin"u8.CopyTo(entry);
        BinaryPrimitives.WriteInt64LittleEndian(entry[40..], payload.Length);
        var header = result.AsSpan(payload.Length + 0x60);
        "REVIEW"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header[72..], 1);
        ObfuscateMtkHeader(entry);
        ObfuscateMtkHeader(header);
        return result;
    }

    private static void ObfuscateMtkHeader(Span<byte> data)
    {
        ReadOnlySpan<byte> key = "geyixue"u8;
        for (var i = 0; i < data.Length; i++)
        {
            var value = data[i] ^ key[i % key.Length];
            data[i] = (byte)((value >> 4) | (value << 4));
        }
    }

    private sealed class CountingReadStream(byte[] data) : MemoryStream(data, writable: false)
    {
        public long BytesRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = base.Read(buffer);
            BytesRead += read;
            return read;
        }

        public override int ReadByte()
        {
            var value = base.ReadByte();
            if (value >= 0)
            {
                BytesRead++;
            }

            return value;
        }
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