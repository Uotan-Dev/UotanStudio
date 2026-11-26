using System.Xml;
using UotanStudio.Comm.Protocols.Mtk;
using Xunit;

namespace UotanStudio.Tests.Protocols;

/// <summary>
/// Pins the XML agent's system-property reply shape against the vendor tool's published sample: a root
/// element that carries the document version as an attribute, and one item element per property.
/// </summary>
public sealed class MtkXmlSystemPropertyTests
{
    private const string VendorSample = """
        <?xml version="1.0" encoding="utf-8"?>
        <sys_prop version="1.0">
            <item key="to-be-impl">AAAAAAAA</item>
            <item key="to-be-impl2">BBBBBBBB</item>
        </sys_prop>
        """;

    [Fact]
    public void Parse_VendorSampleWithVersionAttributeAndSeveralItems_SelectsRequestedKey()
    {
        var property = MtkXmlSystemProperty.Parse(VendorSample, "to-be-impl2", "sys_prop");

        Assert.Equal("sys_prop", property.EnvelopeName);
        Assert.Equal("1.0", property.Version);
        Assert.Equal("to-be-impl2", property.Key);
        Assert.Equal("BBBBBBBB", property.Value);
    }

    [Fact]
    public void Parse_LeadingItemBeforeLaterSiblings_SelectsFirstItem()
    {
        var property = MtkXmlSystemProperty.Parse(VendorSample, "to-be-impl", null);

        Assert.Equal("AAAAAAAA", property.Value);
    }

    [Fact]
    public void Parse_VersionAsScalarElement_StillReadsVersion()
    {
        const string document = "<sys_prop><version>1.0</version><item key=\"DA.SLA\">DISABLED</item></sys_prop>";

        var property = MtkXmlSystemProperty.Parse(document, "DA.SLA", "sys_prop");

        Assert.Equal("1.0", property.Version);
        Assert.Equal("DISABLED", property.Value);
    }

    [Fact]
    public void Parse_RequestedKeyAbsent_Throws()
    {
        Assert.Throws<InvalidDataException>(
            () => MtkXmlSystemProperty.Parse(VendorSample, "DA.SLA", "sys_prop"));
    }

    [Fact]
    public void Parse_UnrecognizedRootAttribute_Throws()
    {
        const string document = "<sys_prop trust=\"yes\"><item key=\"a\">1</item></sys_prop>";

        Assert.Throws<InvalidDataException>(() => MtkXmlSystemProperty.Parse(document, "a", "sys_prop"));
    }

    [Fact]
    public void Parse_ExtraItemAttribute_Throws()
    {
        const string document = "<sys_prop><item key=\"a\" extra=\"1\">1</item></sys_prop>";

        Assert.Throws<InvalidDataException>(() => MtkXmlSystemProperty.Parse(document, "a", "sys_prop"));
    }

    [Fact]
    public void Parse_DocumentTypeDeclaration_Throws()
    {
        const string document =
            "<!DOCTYPE sys_prop [<!ENTITY x \"y\">]><sys_prop><item key=\"a\">1</item></sys_prop>";

        Assert.ThrowsAny<XmlException>(() => MtkXmlSystemProperty.Parse(document, "a", "sys_prop"));
    }
}