using System.Xml.Linq;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.AXml.Models;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.AXml.Parsing;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.AXml.Writing;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>Checks AXML value and namespace preservation. <para>检查 AXML 值和命名空间保留。</para></summary>
public class AXmlFormatConformanceTests
{
    [Fact]
    public void Save_PreservesTypedStringAndResourceMapIndices()
    {
        var doc = new AXmlDocument { Root = new AXmlElement { Name = "manifest", Text = "root text" } };
        doc.StringPool.Intern("wanted-value");
        doc.StringPool.Intern("label");
        doc.ResourceMap.Add(0);
        doc.ResourceMap.Add(0x01010001);
        doc.Namespaces.Add(new AXmlNamespace { Prefix = "android", Uri = "http://schemas.android.com/apk/res/android" });
        doc.Root.Attributes.Add(new AXmlAttribute { Name = "label", Namespace = doc.Namespaces[0].Uri, ValueType = AXmlValueType.String, ValueData = 0 });
        var saved = AXmlParser.Parse(AXmlWriter.Write(doc));
        Assert.Equal("wanted-value", saved.Root.Attributes[0].FormatValue(saved.StringPool));
        Assert.Equal("label", saved.StringPool.GetString(1));
        Assert.Equal(0x01010001u, saved.ResourceMap[1]);
        Assert.Equal("root text", saved.Root.Text);
    }

    [Fact]
    public void TextExport_IsValidXmlWithQualifiedAttributesAndElements()
    {
        const string uri = "urn:test";
        var doc = new AXmlDocument { Root = new AXmlElement { Name = "root", Namespace = uri } };
        doc.Namespaces.Add(new AXmlNamespace { Prefix = "test", Uri = uri });
        doc.Root.Attributes.Add(new AXmlAttribute { Name = "value", Namespace = uri, RawValue = "<&\"" });
        doc.Root.Children.Add(new AXmlElement { Name = "child", Namespace = uri, Text = "a & b" });
        var xml = XDocument.Parse(doc.ToXml());
        Assert.Equal(XName.Get("root", uri), xml.Root!.Name);
        Assert.Equal("<&\"", xml.Root.Attribute(XName.Get("value", uri))!.Value);
        Assert.Equal("a & b", xml.Root.Element(XName.Get("child", uri))!.Value);
    }

    [Theory]
    [InlineData(0x00000c01u, AXmlValueType.Dimension, "12dip")]
    [InlineData(0xfffff401u, AXmlValueType.Dimension, "-12dip")]
    [InlineData(0x40000030u, AXmlValueType.Fraction, "50%")]
    [InlineData(0x40000031u, AXmlValueType.Fraction, "50%p")]
    public void ComplexValues_UseAndroidMantissaAndRadix(uint data, AXmlValueType type, string expected)
        => Assert.Equal(expected, new AXmlAttribute { ValueData = data, ValueType = type }.FormatValue(null));
}