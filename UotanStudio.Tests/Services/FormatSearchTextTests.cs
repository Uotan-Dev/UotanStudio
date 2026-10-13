using System.Text;
using UotanStudio.Firmware.AndroidMeta.Arsc;
using UotanStudio.Firmware.AndroidMeta.AXml;
using UotanStudio.Firmware.AndroidMeta.Dex;
using UotanStudio.Firmware.AndroidMeta.Dex.Models;
using UotanStudio.Firmware.DeviceTree.Dt;
using UotanStudio.Services.Workspace;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Format-adapter tests for project-scope search: DTB/ARSC/AXML/DEX text-ification, anchor mapping,
/// cancellation and skip semantics, plus XML→AXML recompilation preserving the byte semantics of
/// typed attributes after a string-value edit.
/// <para>项目范围格式适配层测试：验证 DTB/ARSC/AXML/DEX 的文本化、锚点映射、取消与跳过语义，
/// 以及 XML→AXML 重编译在编辑字符串值后保留强类型属性的字节语义。</para>
/// </summary>
public class FormatSearchTextTests
{
    private static readonly CancellationToken Token = TestContext.Current.CancellationToken;

    [Fact]
    public void TryRead_Dtb_ReturnsDtsTextWithProperty()
    {
        using var temp = new TempDirectory();
        var tree = new DeviceTree();
        tree.Root.AddProperty("compatible", Encoding.UTF8.GetBytes("uotan,board-x\0"));
        tree.Root.AddChild("soc").AddPropertyCells("reg", 0x1000_0000, 0x1000);
        var path = temp.WriteBytes("board.dtb", DtbFormat.Write(tree));

        var text = FormatSearchText.TryRead(path, Token);

        Assert.NotNull(text);
        Assert.Contains("uotan,board-x", text);
        Assert.Contains("soc", text);
    }

    [Fact]
    public void TryRead_Axml_ReturnsXmlTextIdenticalToPreview()
    {
        using var temp = new TempDirectory();
        var document = BuildDocument();
        var path = temp.WriteBytes("AndroidManifest.xml", AXmlWriter.Write(document));

        var text = FormatSearchText.TryRead(path, Token);

        Assert.Equal(document.ToXml(), text);
    }

    [Fact]
    public void TryRead_Dex_ReturnsSmaliText()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("classes.dex", BuildMinimalDex().ToArray());

        var text = FormatSearchText.TryRead(path, Token);

        Assert.NotNull(text);
        Assert.Contains("Lcom/example/Hello;", text);
    }

    [Fact]
    public void TryRead_UnrecognizedContent_ReturnsNull()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("random.bin", [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08]);

        Assert.Null(FormatSearchText.TryRead(path, Token));
    }

    [Fact]
    public void TryRead_HonorsCancellation()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteBytes("board.dtb", DtbFormat.Write(new DeviceTree()));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => FormatSearchText.TryRead(path, cancellation.Token));
    }

    [Fact]
    public void ProjectSearch_IncludesDtbFilesThroughFormatAdapter()
    {
        using var temp = new TempDirectory();
        var tree = new DeviceTree();
        tree.Root.AddProperty("model", Encoding.UTF8.GetBytes("uotan-target-board\0"));
        temp.WriteBytes("board.dtb", DtbFormat.Write(tree));

        var found = ProjectTextSearchService.Find(temp.Path, new("uotan-target-board"),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase), Token);

        var result = Assert.Single(found);
        Assert.Equal("board.dtb", Path.GetFileName(result.Path));
        Assert.Equal("uotan-target-board", result.Matches[0].Value);
    }

    [Fact]
    public void ResourceTableSearchText_MapsEntryOffsetsToAnchors()
    {
        var (table, entry, key) = BuildResourceTable();
        var anchors = new List<(int Offset, ResSearchAnchor Anchor)>();

        var text = ResourceTableSearchText.Serialize(table, (offset, anchor) => anchors.Add((offset, anchor)));

        Assert.Contains($"{key} = Hello", text);
        Assert.Equal(anchors.Count, anchors.Select(a => a.Offset).Distinct().Count());
        var match = anchors.First(a => a.Anchor.Entry is not null);
        Assert.Same(entry, match.Anchor.Entry);
        Assert.Same(table.Packages[0], match.Anchor.Package);
        Assert.Same(table.Packages[0].Types[0], match.Anchor.Type);
    }

    [Fact]
    public void TryRead_Arsc_ReturnsResourceText()
    {
        using var temp = new TempDirectory();
        var (table, _, key) = BuildResourceTable();
        var path = temp.WriteBytes("resources.arsc", ArscWriter.Write(table));

        var text = FormatSearchText.TryRead(path, Token);

        Assert.NotNull(text);
        Assert.Contains(key, text);
    }

    [Fact]
    public void AxmlTextFormat_Parse_PreservesTypedAttributesAndEditsStrings()
    {
        var reference = BuildDocument();
        var xml = reference.ToXml();

        var parsed = AXmlTextFormat.Parse(xml, reference);

        Assert.Equal(reference.Root.Name, parsed.Root.Name);
        Assert.Equal(reference.Root.Children.Count, parsed.Root.Children.Count);
        var versionCode = parsed.Root.Attributes.First(a => a.Name == "versionCode");
        Assert.Equal(AXmlValueType.IntDec, versionCode.ValueType);
        Assert.Equal(2u, versionCode.ValueData);

        var edited = xml.Replace("com.example.app", "com.example.renamed", StringComparison.Ordinal);
        var recompiled = AXmlTextFormat.Parse(edited, reference);

        var package = recompiled.Root.Attributes.First(a => a.Name == "package");
        Assert.Equal(AXmlValueType.String, package.ValueType);
        Assert.Equal("com.example.renamed", package.FormatValue(recompiled.StringPool));
        Assert.Equal(2u, recompiled.Root.Attributes.First(a => a.Name == "versionCode").ValueData);

        var bytes = AXmlWriter.Write(recompiled);
        var reparsed = AXmlParser.Parse(bytes);
        Assert.Equal("com.example.renamed",
            reparsed.Root.Attributes.First(a => a.Name == "package").FormatValue(reparsed.StringPool));
    }

    [Fact]
    public void AxmlTextFormat_Parse_InfersTypesWithoutReference()
    {
        var parsed = AXmlTextFormat.Parse("<root a=\"true\" b=\"0x0000002a\" c=\"text\" />");

        Assert.Equal(AXmlValueType.IntBoolean, parsed.Root.Attributes.First(a => a.Name == "a").ValueType);
        Assert.Equal(AXmlValueType.IntHex, parsed.Root.Attributes.First(a => a.Name == "b").ValueType);
        Assert.Equal(AXmlValueType.String, parsed.Root.Attributes.First(a => a.Name == "c").ValueType);
    }

    private static (ResourceTable Table, ResEntry Entry, string Key) BuildResourceTable()
    {
        var table = new ResourceTable();
        var package = new ResPackage { Id = 0x7f, Name = "com.example" };
        var type = new ResType { Id = 0x02, ConfigDescription = "default", ConfigHex = "00000000" };
        var key = "app_name";
        var entry = new ResEntry
        {
            KeyIndex = package.KeyStrings.Intern(key),
            Value = new ResValue
            {
                Type = ResValueType.String,
                Data = (uint)table.GlobalPool.Intern("Hello")
            }
        };
        type.Entries.Add(entry);
        package.TypeStrings.Intern("string");
        package.Types.Add(type);
        table.Packages.Add(package);
        return (table, entry, key);
    }

    private static AXmlDocument BuildDocument()
    {
        var document = new AXmlDocument();
        document.Namespaces.Add(new AXmlNamespace
        {
            Prefix = "android",
            Uri = "http://schemas.android.com/apk/res/android"
        });

        var root = new AXmlElement { Name = "manifest" };
        root.Attributes.Add(new AXmlAttribute
        {
            Name = "package",
            RawValue = "com.example.app",
            ValueType = AXmlValueType.String,
            ValueData = (uint)document.StringPool.Intern("com.example.app")
        });
        root.Attributes.Add(new AXmlAttribute
        {
            Name = "versionCode",
            ValueType = AXmlValueType.IntDec,
            ValueData = 2
        });

        var application = new AXmlElement { Name = "application" };
        application.Attributes.Add(new AXmlAttribute
        {
            Name = "app_name",
            Namespace = "http://schemas.android.com/apk/res/android",
            RawValue = "@string/app_name",
            ValueType = AXmlValueType.Reference,
            ValueData = 0x7f010000
        });
        root.Children.Add(application);
        document.Root = root;
        return document;
    }

    private static DexFile BuildMinimalDex()
    {
        var dex = new DexFile("035");
        dex.Strings.Add("Lcom/example/Hello;");
        dex.Strings.Add("Ljava/lang/Object;");
        dex.Strings.Add("main");
        dex.Strings.Add("V");
        dex.Strings.Add("([Ljava/lang/String;)V");
        dex.Strings.Add("Hello.java");
        dex.Strings.Add("[Ljava/lang/String;");

        dex.Types.Add("Lcom/example/Hello;");
        dex.Types.Add("Ljava/lang/Object;");
        dex.Types.Add("V");
        dex.Types.Add("[Ljava/lang/String;");

        dex.Protos.Add(new ProtoDefinition
        {
            ShortyIndex = 4,
            ReturnTypeIndex = 2,
            ParameterTypeIndices = [3]
        });
        dex.Methods.Add(new MethodId { ClassIndex = 0, ProtoIndex = 0, NameIndex = 2 });

        var cls = new ClassDefinition
        {
            TypeIndex = 0,
            AccessFlags = AccessFlags.Public,
            SuperclassIndex = 1,
            SourceFileIndex = 5
        };
        cls.DirectMethods.Add(new EncodedMethod
        {
            MethodIndex = 0,
            AccessFlags = AccessFlags.Public | AccessFlags.Static,
            Code = new CodeItem { RegistersSize = 1, RawInsns = [0x1200, 0x0E00] }
        });
        dex.Classes.Add(cls);
        return dex;
    }
}