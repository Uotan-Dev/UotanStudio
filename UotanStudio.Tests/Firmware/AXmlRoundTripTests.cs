using UotanStudio.Firmware.BoundedContexts.AndroidMeta.AXml.Models;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.AXml.Parsing;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.AXml.Writing;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// AXML round-trip tests: programmatically build a small AXML document model (root element with
/// attributes, namespaces and a child), serialize it, parse it back and verify the model is
/// preserved; then re-serialize and assert byte stability.
/// <para>AXML 往返测试：程序化构造小型 AXML 文档模型（含命名空间、属性与子元素的根元素），序列化后
/// 重新解析，验证模型保持一致；再次序列化断言字节稳定。</para>
/// </summary>
public class AXmlRoundTripTests
{
    [Fact]
    public void WriteThenParse_PreservesElementTree()
    {
        var bytes = AXmlWriter.Write(BuildDocument());

        var doc = AXmlParser.Parse(bytes);

        Assert.Equal(0x0003, bytes[0] | (bytes[1] << 8));
        Assert.Equal(8, bytes[2] | (bytes[3] << 8));

        var root = doc.Root;
        Assert.Equal("manifest", root.Name);
        // 根元素自身无命名空间（声明在 doc.Namespaces 中）。
        // The root element itself has no namespace (declarations live in doc.Namespaces).
        Assert.Null(root.Namespace);
        Assert.Single(doc.Namespaces);
        Assert.Equal("android", doc.Namespaces[0].Prefix);
        Assert.Equal(2, root.Attributes.Count);
        Assert.Single(root.Children);

        var application = root.Children[0];
        Assert.Equal("application", application.Name);
        Assert.Single(application.Attributes);
        Assert.Equal("app_name", application.Attributes[0].Name);
        Assert.Equal("http://schemas.android.com/apk/res/android", application.Attributes[0].Namespace);
        Assert.Equal("@string/app_name", application.Attributes[0].RawValue);
    }

    [Fact]
    public void WriteParseWrite_IsByteStable()
    {
        var first = AXmlWriter.Write(BuildDocument());
        var doc = AXmlParser.Parse(first);
        var second = AXmlWriter.Write(doc);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Parse_ThrowsOnInvalidMagic()
    {
        var data = new byte[] { 0x01, 0x00, 0x08, 0x00, 0x10, 0x00, 0x00, 0x00 };
        Assert.Throws<AXmlException>(() => AXmlParser.Parse(data));
    }

    [Fact]
    public void IsAxml_DetectsHeader()
    {
        var bytes = AXmlWriter.Write(BuildDocument());
        Assert.True(AXmlParser.IsAxml(bytes));
        Assert.False(AXmlParser.IsAxml(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void EditStringValue_RebuildsPoolAndPreservesValue()
    {
        // 模拟编辑器编辑 String 型属性：Intern 新字符串并更新 ValueData，重建后值应保留。
        // Simulates editing a String-typed attribute: intern a new string, update ValueData,
        // then rebuild — the edited value must survive the round trip.
        var first = AXmlWriter.Write(BuildDocument());
        var doc = AXmlParser.Parse(first);

        // 找到 String 型属性（package 的 RawValue 是字符串，但这里编辑 ValueData 指向的值：
        // 直接改 RawValue 分支走的是文本；对 String 型无 RawValue 的属性，模拟 Intern 更新。
        // Locate a String-typed attribute; edit its pool-backed value via Intern + ValueData.
        var target = doc.Root.Attributes.First(a => a.ValueType == AXmlValueType.String && a.RawValue is not null);
        target.RawValue = "com.example.renamed";

        var rebuilt = AXmlWriter.Write(doc);
        var doc2 = AXmlParser.Parse(rebuilt);

        var renamed = doc2.Root.Attributes.First(a => a.Name == "package");
        Assert.Equal("com.example.renamed", renamed.RawValue);

        // 无 RawValue 的 String 型属性（ValueData 直接引用池）：Intern 后索引稳定。
        // String-typed attribute without RawValue (ValueData references the pool directly):
        // interning keeps the index stable across the rebuild.
        var valueOnly = new AXmlAttribute
        {
            Name = "label",
            ValueType = AXmlValueType.String,
            ValueData = (uint)doc.StringPool.Intern("original-label")
        };
        doc.Root.Attributes.Add(valueOnly);

        var rebuilt2 = AXmlWriter.Write(doc);
        var doc3 = AXmlParser.Parse(rebuilt2);
        var label = doc3.Root.Attributes.First(a => a.Name == "label");
        Assert.Equal("original-label", label.FormatValue(doc3.StringPool));
    }

    /// <summary>
    /// <para>构造用于往返测试的 AXML 模型：manifest 根 + application 子元素。</para>
    /// Builds the AXML model for round-trip tests: a manifest root plus an application child.
    /// </summary>
    internal static AXmlDocument BuildDocument()
    {
        var doc = new AXmlDocument();
        doc.Namespaces.Add(new AXmlNamespace
        {
            Prefix = "android",
            Uri = "http://schemas.android.com/apk/res/android"
        });

        var root = new AXmlElement
        {
            Name = "manifest",
            Namespace = null
        };
        root.Attributes.Add(new AXmlAttribute
        {
            Name = "package",
            RawValue = "com.example.app",
            ValueType = AXmlValueType.String,
            ValueData = 1
        });
        root.Attributes.Add(new AXmlAttribute
        {
            Name = "versionCode",
            ValueType = AXmlValueType.IntDec,
            ValueData = 2
        });

        var application = new AXmlElement
        {
            Name = "application",
            Namespace = null
        };
        application.Attributes.Add(new AXmlAttribute
        {
            Name = "app_name",
            Namespace = "http://schemas.android.com/apk/res/android",
            RawValue = "@string/app_name",
            ValueType = AXmlValueType.Reference,
            ValueData = 0x7f010000
        });
        root.Children.Add(application);

        doc.Root = root;
        return doc;
    }
}
