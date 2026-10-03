using System.Xml;
using System.Xml.Linq;
using UotanStudio.Firmware.AndroidMeta.AXml;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.VendorFirmware.Oppo.Exceptions;
using UotanStudio.Firmware.VendorFirmware.Oppo.Parsers;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class UntrustedXmlDocumentTests
{
    private const string InternalEntityDocument =
        "<!DOCTYPE r [<!ENTITY a \"0123456789\">]><r>&a;</r>";

    private const string ExternalEntityDocument =
        "<!DOCTYPE r [<!ENTITY x SYSTEM \"file:///Windows/win.ini\">]><r>&x;</r>";

    [Fact]
    public void BareXDocumentParse_InternalEntity_IsExpandedInsteadOfRefused()
    {
        var document = XDocument.Parse(InternalEntityDocument);

        Assert.Equal("0123456789", document.Root?.Value);
    }

    [Fact]
    public void Parse_InternalEntity_IsRefusedBeforeAnyExpansion()
    {
        Assert.Throws<XmlException>(() => UntrustedXmlDocument.Parse(InternalEntityDocument));
    }

    [Fact]
    public void Parse_ExternalEntity_IsRefusedBeforeAnyResolution()
    {
        Assert.Throws<XmlException>(() => UntrustedXmlDocument.Parse(ExternalEntityDocument));
    }

    [Fact]
    public void Parse_PlainDocument_KeepsRootAndText()
    {
        var document = UntrustedXmlDocument.Parse("<data><program value=\"ACK\"/></data>");

        Assert.Equal("data", document.Root?.Name.LocalName);
        Assert.Single(document.Root!.Elements());
    }

    [Fact]
    public void Parse_PreserveWhitespaceOption_StillRefusesTheEntityDocument()
    {
        Assert.Throws<XmlException>(() =>
            UntrustedXmlDocument.Parse(InternalEntityDocument, preserveWhitespace: true));
    }

    [Fact]
    public void AXmlTextFormatParse_DtdDeclaration_IsRejectedAsInvalidText()
    {
        Assert.Throws<AXmlException>(() => AXmlTextFormat.Parse(InternalEntityDocument));
    }

    [Fact]
    public void OppParsers_DtdDeclaration_IsRejectedBeforeEntryWalk()
    {
        Assert.Throws<InvalidOppFormatException>(() => OppOpsParser.ParseEntries(InternalEntityDocument));
        Assert.Throws<InvalidOppFormatException>(() => OfpQcParser.ParseEntries(ExternalEntityDocument, 512));
    }

    [Fact]
    public void CreateSettings_EveryPackageParserShares_TheSameBudgets()
    {
        var settings = UntrustedXmlDocument.CreateSettings();

        Assert.Equal(DtdProcessing.Prohibit, settings.DtdProcessing);
        Assert.Equal(4 * 1024 * 1024, settings.MaxCharactersInDocument);
        Assert.Equal(1024L, settings.MaxCharactersFromEntities);
    }
}