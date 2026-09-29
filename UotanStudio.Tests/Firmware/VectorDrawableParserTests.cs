using System.Buffers.Binary;
using UotanStudio.Firmware.AndroidMeta.Apk;
using UotanStudio.Firmware.AndroidMeta.AXml;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Compiled vector-drawable parsing tests. The parser consumes AXML bytes produced by <see cref="AXmlWriter"/>, so
/// each case pins one documented tolerance: a non-vector root, a malformed magic, a truncated buffer and a
/// structurally corrupt chunk all yield <see langword="null"/> instead of throwing, while well-formed input decodes
/// float, complex-dimension and color attributes with the documented fallbacks.
/// <para>编译后 vector drawable 解析测试。解析器消费 <see cref="AXmlWriter"/> 产出的 AXML 字节，
/// 因此每个用例钉住一项已记录的容错：非 vector 根元素、错误魔数、被截断的缓冲区以及结构性损坏的数据块，
/// 都返回 <see langword="null"/> 而不抛出；合法输入则按已记录的回退规则解码 float、complex 尺寸与颜色属性。</para>
/// </summary>
public class VectorDrawableParserTests
{
    private const string AndroidNamespace = "http://schemas.android.com/apk/res/android";

    private static byte[] Write(string rootName, AXmlElement root)
    {
        var document = new AXmlDocument();
        document.Namespaces.Add(new AXmlNamespace { Prefix = "android", Uri = AndroidNamespace });
        root.Name = rootName;
        document.Root = root;
        return AXmlWriter.Write(document);
    }

    private static AXmlAttribute Float(string name, float value) => new()
    {
        Name = name,
        Namespace = AndroidNamespace,
        ValueType = AXmlValueType.Float,
        ValueData = (uint)BitConverter.SingleToInt32Bits(value)
    };

    private static AXmlAttribute Dimension(string name, int mantissa, int radix) => new()
    {
        Name = name,
        Namespace = AndroidNamespace,
        ValueType = AXmlValueType.Dimension,
        ValueData = (uint)((mantissa << 8) | (radix << 4) | 1)
    };

    private static AXmlAttribute ColorArgb8(string name, uint argb) => new()
    {
        Name = name,
        Namespace = AndroidNamespace,
        ValueType = AXmlValueType.IntColorArgb8,
        ValueData = argb
    };

    private static AXmlAttribute ColorRgb8(string name, uint rgb) => new()
    {
        Name = name,
        Namespace = AndroidNamespace,
        ValueType = AXmlValueType.IntColorRgb8,
        ValueData = rgb
    };

    private static AXmlAttribute PathData(string value) => new()
    {
        Name = "pathData",
        Namespace = AndroidNamespace,
        RawValue = value,
        ValueType = AXmlValueType.String,
        ValueData = 1
    };

    private static AXmlElement Path(string pathData)
    {
        var element = new AXmlElement { Name = "path" };
        element.Attributes.Add(PathData(pathData));
        return element;
    }

    private static AXmlElement Group(params AXmlElement[] children)
    {
        var element = new AXmlElement { Name = "group" };
        element.Children.AddRange(children);
        return element;
    }

    [Fact]
    public void Parse_NullOrShortOrBadMagic_ReturnsNull()
    {
        Assert.Null(VectorDrawableParser.Parse(null));
        Assert.Null(VectorDrawableParser.Parse([0x03, 0x00, 0x08]));
        Assert.Null(VectorDrawableParser.Parse([0x02, 0x00, 0x08, 0x00, 0x10, 0x00, 0x00, 0x00]));
    }

    [Fact]
    public void Parse_ValidAxmlButNotVectorRoot_ReturnsNull()
    {
        var bytes = Write("shape", new AXmlElement());

        Assert.Equal(0x03, bytes[0]);
        Assert.Null(VectorDrawableParser.Parse(bytes));
    }

    [Fact]
    public void Parse_TruncatedAxmlBuffer_ReturnsNullInsteadOfThrowing()
    {
        var bytes = Write("vector", BuildVector());

        Assert.Null(VectorDrawableParser.Parse(bytes.AsSpan(0, bytes.Length / 2).ToArray()));
    }

    [Fact]
    public void Parse_OversizedDeclaredFileLength_ReturnsNullInsteadOfThrowing()
    {
        var bytes = Write("vector", BuildVector());
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), uint.MaxValue);

        Assert.Null(VectorDrawableParser.Parse(bytes));
    }

    [Fact]
    public void Parse_DeclaredFileLengthShorterThanBuffer_ReturnsNullInsteadOfThrowing()
    {
        var bytes = Write("vector", BuildVector());
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(bytes.Length - 8));

        Assert.Null(VectorDrawableParser.Parse(bytes));
    }

    [Fact]
    public void Parse_MinimalVector_AppliesDocumentedDefaults()
    {
        var drawable = VectorDrawableParser.Parse(Write("vector", new AXmlElement()));

        Assert.NotNull(drawable);
        Assert.Equal(24f, drawable!.Width);
        Assert.Equal(24f, drawable.Height);
        Assert.Equal(24f, drawable.ViewportWidth);
        Assert.Equal(24f, drawable.ViewportHeight);
        Assert.Equal(1f, drawable.Alpha);
        Assert.Empty(drawable.Paths);
    }

    [Fact]
    public void Parse_DimensionAndFloatAttributes_DecodeIntrinsicSizeAndAlpha()
    {
        var vector = new AXmlElement();
        vector.Attributes.Add(Dimension("width", 48, 0));
        vector.Attributes.Add(Dimension("height", 96, 0));
        vector.Attributes.Add(Float("viewportWidth", 32f));
        vector.Attributes.Add(Float("viewportHeight", 64f));
        vector.Attributes.Add(Float("alpha", 0.5f));

        var drawable = VectorDrawableParser.Parse(Write("vector", vector));

        Assert.NotNull(drawable);
        Assert.Equal(48f, drawable!.Width);
        Assert.Equal(96f, drawable.Height);
        Assert.Equal(32f, drawable.ViewportWidth);
        Assert.Equal(64f, drawable.ViewportHeight);
        Assert.Equal(0.5f, drawable.Alpha);
    }

    [Theory]
    [InlineData(0, 1f)]
    [InlineData(1, 2f)]
    [InlineData(2, 4f)]
    [InlineData(3, 8f)]
    public void Parse_DimensionRadix_ScalesMantissaByPowerOfTwo(int radix, float expected)
    {
        var vector = new AXmlElement();
        vector.Attributes.Add(Dimension("width", 1, radix));

        var drawable = VectorDrawableParser.Parse(Write("vector", vector));

        Assert.Equal(expected, drawable!.Width);
    }

    [Fact]
    public void Parse_UnsupportedAttributeValueType_FallsBackToDocumentedDefault()
    {
        var vector = new AXmlElement();
        vector.Attributes.Add(new AXmlAttribute
        {
            Name = "width",
            Namespace = AndroidNamespace,
            ValueType = AXmlValueType.IntDec,
            ValueData = 7
        });

        var drawable = VectorDrawableParser.Parse(Write("vector", vector));

        Assert.Equal(24f, drawable!.Width);
    }

    [Fact]
    public void Parse_ViewportAttributeOfAnyType_ReinterpretsDataAsFloatBits()
    {
        var vector = new AXmlElement();
        vector.Attributes.Add(new AXmlAttribute
        {
            Name = "viewportHeight",
            Namespace = AndroidNamespace,
            ValueType = AXmlValueType.IntDec,
            ValueData = 7
        });

        var drawable = VectorDrawableParser.Parse(Write("vector", vector));

        Assert.Equal(BitConverter.Int32BitsToSingle(7), drawable!.ViewportHeight);
    }

    [Fact]
    public void Parse_AttributeNameLookup_IgnoresCaseAndNamespacePrefix()
    {
        var vector = new AXmlElement();
        vector.Attributes.Add(new AXmlAttribute
        {
            Name = "android:VIEWPORTWIDTH",
            Namespace = AndroidNamespace,
            ValueType = AXmlValueType.Float,
            ValueData = (uint)BitConverter.SingleToInt32Bits(11f)
        });

        var drawable = VectorDrawableParser.Parse(Write("vector", vector));

        Assert.Equal(11f, drawable!.ViewportWidth);
    }

    [Fact]
    public void Parse_SinglePath_CollectsFillStrokeAndAlpha()
    {
        var vector = new AXmlElement();
        var path = Path("M0,0 L1,1");
        path.Attributes.Add(ColorArgb8("fillColor", 0x80FF0000));
        path.Attributes.Add(Float("fillAlpha", 0.25f));
        path.Attributes.Add(ColorRgb8("strokeColor", 0x00FF00));
        path.Attributes.Add(Dimension("strokeWidth", 2, 0));
        path.Attributes.Add(Float("strokeAlpha", 0.75f));
        vector.Children.Add(path);

        var drawable = VectorDrawableParser.Parse(Write("vector", vector));
        var decoded = Assert.Single(drawable!.Paths);

        Assert.Equal("M0,0 L1,1", decoded.PathData);
        Assert.Equal(0x80FF0000u, decoded.FillColor);
        Assert.Equal(0.25f, decoded.FillAlpha);
        Assert.Equal(0xFF00FF00u, decoded.StrokeColor);
        Assert.Equal(2f, decoded.StrokeWidth);
        Assert.Equal(0.75f, decoded.StrokeAlpha);
    }

    [Fact]
    public void Parse_PathWithoutPaint_ReportsNullColorsAndDefaultAlphas()
    {
        var vector = new AXmlElement();
        vector.Children.Add(Path("M0,0"));

        var decoded = Assert.Single(VectorDrawableParser.Parse(Write("vector", vector))!.Paths);

        Assert.Null(decoded.FillColor);
        Assert.Null(decoded.StrokeColor);
        Assert.Equal(1f, decoded.FillAlpha);
        Assert.Equal(1f, decoded.StrokeAlpha);
        Assert.Equal(0f, decoded.StrokeWidth);
    }

    [Fact]
    public void Parse_PathWithoutPathData_IsSkipped()
    {
        var vector = new AXmlElement();
        vector.Children.Add(new AXmlElement { Name = "path" });
        vector.Children.Add(Path("   "));
        vector.Children.Add(Path("M1,1"));

        var drawable = VectorDrawableParser.Parse(Write("vector", vector));

        Assert.Equal("M1,1", Assert.Single(drawable!.Paths).PathData);
    }

    [Fact]
    public void Parse_PathsInsideGroups_AreFlattenedInDocumentOrder()
    {
        var vector = new AXmlElement();
        vector.Children.Add(Path("M0,0"));
        vector.Children.Add(Group(Path("M1,1"), Group(Path("M2,2"))));
        vector.Children.Add(Path("M3,3"));

        var drawable = VectorDrawableParser.Parse(Write("vector", vector));

        Assert.Equal(["M0,0", "M1,1", "M2,2", "M3,3"], drawable!.Paths.Select(path => path.PathData).ToArray());
    }

    [Fact]
    public void Parse_RawValueStringAttribute_WinsOverPoolIndex()
    {
        var vector = new AXmlElement();
        var path = new AXmlElement { Name = "path" };
        path.Attributes.Add(new AXmlAttribute
        {
            Name = "pathData",
            Namespace = AndroidNamespace,
            RawValue = "M9,9",
            ValueType = AXmlValueType.String,
            ValueData = 0
        });
        vector.Children.Add(path);

        Assert.Equal("M9,9", Assert.Single(VectorDrawableParser.Parse(Write("vector", vector))!.Paths).PathData);
    }

    [Fact]
    public void Parse_NonColorAttributeType_ReportsNoPaint()
    {
        var vector = new AXmlElement();
        var path = Path("M0,0");
        path.Attributes.Add(new AXmlAttribute
        {
            Name = "fillColor",
            Namespace = AndroidNamespace,
            ValueType = AXmlValueType.Reference,
            ValueData = 0x7f010000
        });
        vector.Children.Add(path);

        Assert.Null(Assert.Single(VectorDrawableParser.Parse(Write("vector", vector))!.Paths).FillColor);
    }

    private static AXmlElement BuildVector()
    {
        var vector = new AXmlElement();
        vector.Attributes.Add(Dimension("width", 24, 0));
        vector.Children.Add(Path("M0,0 L24,24"));
        return vector;
    }
}