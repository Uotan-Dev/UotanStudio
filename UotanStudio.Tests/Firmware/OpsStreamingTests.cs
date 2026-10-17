using System.Text;
using UotanStudio.Firmware.VendorFirmware.Oppo.Crypto;
using UotanStudio.Firmware.VendorFirmware.Oppo.Models;
using UotanStudio.Firmware.VendorFirmware.Oppo.Parsers;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class OpsStreamingTests
{
    [Fact]
    public void OpsCipher_StreamingRoundTrip_MatchesInMemoryResult()
    {
        var plaintext = new byte[(2 * 81920) + 37];
        for (var i = 0; i < plaintext.Length; i++)
        {
            plaintext[i] = (byte)(i * 31 + 7);
        }

        var provider = new OpsFormatCryptoProvider();
        var inMemory = provider.Encrypt(plaintext, "mbox5");

        using var streamingEncrypted = new MemoryStream();
        provider.Encrypt(new MemoryStream(plaintext), streamingEncrypted, "mbox5", TestContext.Current.CancellationToken);
        Assert.Equal(inMemory, streamingEncrypted.ToArray());

        using var streamingPlaintext = new MemoryStream();
        provider.Decrypt(new MemoryStream(inMemory), streamingPlaintext, "mbox5", TestContext.Current.CancellationToken);
        Assert.Equal(plaintext, streamingPlaintext.ToArray());
    }

    [Fact]
    public void OpsXml_DeclarationBeyondProbeWindow_StillSelectsKey()
    {
        var xml = Encoding.UTF8.GetBytes(new string('x', 70 * 1024) +
            "<?xml version=\"1.0\"?><root><Program><file filename=\"boot.img\" FileOffsetInSrc=\"1\" SizeInByteInSrc=\"16\"/></Program></root>");
        var provider = new OpsFormatCryptoProvider();
        var encrypted = provider.Encrypt(xml, "mbox5");

        var result = provider.AutoDecryptOpsXml(encrypted);

        Assert.NotNull(result);
        Assert.Equal("mbox5", result.Value.KeyId);
        Assert.StartsWith("<?xml", result.Value.Xml);
        Assert.EndsWith("</root>", result.Value.Xml);
    }

    [Fact]
    public void OpsXml_DeclarationInsideProbeWindow_SelectsFirstMatchingKey()
    {
        var xml = Encoding.UTF8.GetBytes(new string('x', 4096) +
            "<?xml version=\"1.0\"?><root><Program><file filename=\"boot.img\" FileOffsetInSrc=\"1\" SizeInByteInSrc=\"16\"/></Program></root>");
        var provider = new OpsFormatCryptoProvider();
        var encrypted = provider.Encrypt(xml, "mbox6");

        var result = provider.AutoDecryptOpsXml(encrypted);

        Assert.NotNull(result);
        Assert.Equal("mbox6", result.Value.KeyId);
        Assert.StartsWith("<?xml", result.Value.Xml);
    }

    [Fact]
    public void OpsXml_DeclaredLengthAboveLimit_IsRejectedWithoutReading()
    {
        using var source = new MemoryStream(new byte[0x400]);
        var tail = new OpsTailPage
        {
            Version = 2,
            Flags = 1,
            MagicValue = OpsTailPage.Magic,
            ConfigSectorPos = 0,
            XmlLength = (uint)OppOpsParser.MaximumXmlBytes + 1
        };

        var result = OppOpsParser.TryDecryptXml(source, tail);

        Assert.Null(result);
        Assert.Equal(0, source.Position);
    }

    [Fact]
    public void OpsXml_DeclaredLengthAtLimit_ReadsTheDeclaredRegion()
    {
        var xml = Encoding.ASCII.GetBytes("<?xml version=\"1.0\"?><root/>");
        var declared = ((xml.Length + 15) / 16) * 16;
        var data = new byte[declared];
        xml.CopyTo(data, 0);
        using var source = new MemoryStream(data);
        var tail = new OpsTailPage
        {
            Version = 2,
            Flags = 1,
            MagicValue = OpsTailPage.Magic,
            ConfigSectorPos = 0,
            XmlLength = (uint)declared
        };

        var result = OppOpsParser.TryDecryptXml(source, tail);

        Assert.NotNull(result);
        Assert.Equal("plaintext", result.Value.KeyId);
        Assert.StartsWith("<?xml", result.Value.Xml);
    }
}