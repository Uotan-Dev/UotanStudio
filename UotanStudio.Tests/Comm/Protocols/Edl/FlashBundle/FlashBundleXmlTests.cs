using System.Text;
using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Comm.Protocols.Edl.FlashBundle;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Edl.FlashBundle;

public class FlashBundleXmlTests
{
    [Theory]
    [InlineData("rawprogram")]
    [InlineData("patch")]
    [InlineData("ufs")]
    public void Parse_UntrustedXml_RejectsInvalidAndExcessiveDocuments(string parser)
    {
        string[] documents =
        [
            "<!DOCTYPE data [<!ENTITY value 'test'>]><data/>",
            "<!DOCTYPE data SYSTEM 'file:///not-a-real-resource'><data/>",
            "<data><command><nested/></command></data>",
            "<data>unexpected text</data>",
            "<data><![CDATA[unexpected text]]></data>",
            "<data/><data/>",
            "<data><!--" + new string('x', 4 * 1024 * 1024) + "--></data>",
            "<data>" + string.Concat(Enumerable.Repeat("<command/>", 16385)) + "</data>",
            "<data><command " + string.Join(" ", Enumerable.Range(0, 129).Select(i => $"a{i}='0'")) + "/></data>"
        ];

        foreach (var document in documents)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(document));

            var exception = Assert.Throws<EdlException>(() => Parse(parser, stream));

            Assert.Equal(EdlErrorCode.XmlParseError, exception.ErrorCode);
            Assert.True(stream.CanRead);
        }
    }

    [Theory]
    [InlineData("rawprogram", "<data><program filename='system.img'/></data>")]
    [InlineData("patch", "<patches><patch filename='DISK'/></patches>")]
    [InlineData("ufs", "<data><ufs type='common'/></data>")]
    public void Parse_ValidManifestWithComments_PreservesEntryAndStream(string parser, string document)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("<?xml version='1.0'?><!--manifest-->" + document));

        var count = Parse(parser, stream);

        Assert.Equal(1, count);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void Parse_EntryCountAtLimit_PreservesEveryEntry()
    {
        var document = "<data>" + string.Concat(Enumerable.Repeat("<program filename='system.img'/>", 16384)) + "</data>";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(document));

        var entries = RawProgramParser.Parse(stream);

        Assert.Equal(16384, entries.Count);
        Assert.All(entries, entry => Assert.Equal("system.img", entry.Filename));
    }

    [Fact]
    public void Parse_AttributeCountAtLimit_AcceptsManifest()
    {
        var attributes = string.Join(" ", Enumerable.Range(0, 127).Select(i => $"a{i}='0'"));
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes($"<data><program filename='system.img' {attributes}/></data>"));

        var entries = RawProgramParser.Parse(stream);

        Assert.Equal("system.img", Assert.Single(entries).Filename);
    }

    private static int Parse(string parser, Stream stream) => parser switch
    {
        "rawprogram" => RawProgramParser.Parse(stream).Count,
        "patch" => PatchParser.Parse(stream).Count,
        "ufs" => UfsProvisionParser.Parse(stream).Entries.Count,
        _ => throw new ArgumentOutOfRangeException(nameof(parser))
    };
}