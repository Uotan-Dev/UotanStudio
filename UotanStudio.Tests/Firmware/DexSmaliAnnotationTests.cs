using System.Buffers.Binary;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Models;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Smali;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class DexSmaliAnnotationTests
{
    internal const string Source = """
        .class public abstract LExample;
        .super Ljava/lang/Object;
        .annotation system Lz/Meta;
            nested = .subannotation LNested;
                text = "nested#value"
                values = { {1, 2}, {} }
            .end subannotation
            values = {
                true,
                -1t,
                0xffffs,
                '\u0041',
                0xffffffff,
                -4294967296L,
                1.25f,
                -2.5,
                null,
                "value#=",
                Ljava/lang/String;,
                .enum LState;->ON:LState;,
                LExample;->a:I,
                LExample;->consume(I)V
            }
            prototype = (I)Ljava/lang/String;
        .end annotation
        .annotation build La/Meta;
            value = 42
        .end annotation
        .field public static z:I = 1
            .annotation runtime LFieldMarker;
                value = "z"
            .end annotation
        .end field
        .field public a:I
            .annotation runtime LFieldMarker;
                value = "a"
            .end annotation
        .end field
        .method public run(JDLjava/lang/String;)V
            .registers 7
            .annotation runtime LMethodMarker;
                value = "run"
            .end annotation
            .param p1, "wideJ"
                .annotation runtime LParameterMarker;
                    value = "long"
                .end annotation
            .end param
            .param v4, "wideD"
                .annotation runtime LParameterMarker;
                    value = "double"
                .end annotation
            .end param
            .param p5, "text"
            return-void
        .end method
        .method public abstract consume(I)V
            .param p1
                .annotation runtime LParameterMarker;
                    value = "abstract"
                .end annotation
            .end param
        .end method
        """;

    [Fact]
    public void Assemble_AnnotationsAndWideParameters_PreservesAllTargetsAndNestedValues()
    {
        var dex = SmaliAssembler.Assemble(Source);
        var loaded = DexFile.Load(dex.ToArray());
        var cls = Assert.Single(loaded.Classes);
        var directory = Assert.IsType<AnnotationsDirectory>(cls.Annotations);
        Assert.Equal(new[] { "La/Meta;", "Lz/Meta;" }, directory.ClassAnnotations!.Annotations.Select(item => loaded.Types[item.TypeIndex]));
        var metadata = directory.ClassAnnotations.Annotations[1];
        Assert.Equal(new[] { "nested", "prototype", "values" }, metadata.Elements.Select(element => loaded.Strings[element.NameIndex]));
        var nested = Assert.IsType<AnnotationItem>(metadata.Elements[0].Value.Value);
        Assert.Equal("LNested;", loaded.Types[nested.TypeIndex]);
        Assert.Equal("nested#value", loaded.Strings[nested.Elements[0].Value.AsIndex()]);
        Assert.Equal(EncodedValueType.MethodType, metadata.Elements[1].Value.Type);
        var values = Assert.IsType<EncodedValue[]>(metadata.Elements[2].Value.Value);
        Assert.Equal(new[]
        {
            EncodedValueType.Boolean, EncodedValueType.Byte, EncodedValueType.Short, EncodedValueType.Char,
            EncodedValueType.Int, EncodedValueType.Long, EncodedValueType.Float, EncodedValueType.Double,
            EncodedValueType.Null, EncodedValueType.String, EncodedValueType.Type, EncodedValueType.Enum,
            EncodedValueType.Field, EncodedValueType.Method
        }, values.Select(value => value.Type));
        Assert.Equal("ON", loaded.Strings[loaded.Fields[values[11].AsIndex()].NameIndex]);
        Assert.Equal("a", loaded.Strings[loaded.Fields[values[12].AsIndex()].NameIndex]);
        Assert.Equal("consume", loaded.Strings[loaded.Methods[values[13].AsIndex()].NameIndex]);
        Assert.Equal(new[] { "a", "z" }, directory.FieldAnnotations.Select(item => loaded.Strings[loaded.Fields[item.FieldIndex].NameIndex]));
        Assert.Equal("run", loaded.Strings[loaded.Methods[Assert.Single(directory.MethodAnnotations).MethodIndex].NameIndex]);
        Assert.Equal(2, directory.ParameterAnnotations.Count);
        var run = directory.ParameterAnnotations.Single(item => loaded.Strings[loaded.Methods[item.MethodIndex].NameIndex] == "run");
        Assert.Equal(3, run.Parameters.Sets.Count);
        Assert.Null(run.Parameters.Sets[2]);
        var text = Disassembler.DisassembleAll(loaded);
        Assert.Contains(".param p1, \"wideJ\"", text);
        Assert.Contains(".param p3, \"wideD\"", text);
        Assert.Contains(".param p5, \"text\"", text);
        Assert.Contains(".subannotation LNested;", text);
        Assert.Contains(".enum LState;->ON:LState;", text);
        Assert.Equal(dex.ToArray(), SmaliAssembler.Assemble(text).ToArray());
    }

    [Fact]
    public void Assemble_AnnotationWithoutMemberTerminator_AttachesToEnclosingDeclaration()
    {
        var dex = SmaliAssembler.Assemble("""
            .class public abstract LExample;
            .field public a:I
            .annotation runtime LClassMarker;
            .end annotation
            .method public abstract run(I)V
                .param p1
                .annotation runtime LMethodMarker;
                .end annotation
            .end method
            """);
        var directory = Assert.IsType<AnnotationsDirectory>(Assert.Single(dex.Classes).Annotations);
        Assert.Empty(directory.FieldAnnotations);
        Assert.Empty(directory.ParameterAnnotations);
        Assert.Equal("LClassMarker;", dex.Types[Assert.Single(directory.ClassAnnotations!.Annotations).TypeIndex]);
        Assert.Equal("LMethodMarker;", dex.Types[Assert.Single(Assert.Single(directory.MethodAnnotations).Annotations.Annotations).TypeIndex]);
    }

    [Theory]
    [InlineData(".annotation runtime LMarker;\nvalue = 1\nvalue = 2\n.end annotation")]
    [InlineData(".annotation runtime LMarker;\nvalue = {1,}\n.end annotation")]
    [InlineData(".annotation runtime LMarker;\nvalue = {1 2}\n.end annotation")]
    [InlineData(".annotation runtime I\n.end annotation")]
    [InlineData(".annotation invalid LMarker;\n.end annotation")]
    [InlineData(".annotation runtime LMarker;\nvalue = .subannotation LNested;\nx = 1\n.end annotation")]
    [InlineData(".annotation runtime LMarker;\n.end annotation\n.annotation build LMarker;\n.end annotation")]
    public void Assemble_InvalidAnnotation_RejectsInput(string source)
    {
        Assert.Throws<FormatException>(() => SmaliAssembler.Assemble(".class public LExample;\n" + source));
    }

    [Theory]
    [InlineData("p0")]
    [InlineData("p2")]
    [InlineData("p4")]
    [InlineData("v0")]
    public void Assemble_InvalidParameterWord_RejectsReceiverWideTailOrLocal(string register)
    {
        Assert.Throws<FormatException>(() => SmaliAssembler.Assemble(
            ".class public LExample;\n.method public run(JI)V\n.registers 5\n.param " + register
            + "\nreturn-void\n.end method"));
    }

    [Fact]
    public void Assemble_ExcessiveAnnotationDepth_RejectsBeforeStackExhaustion()
    {
        var source = ".class public LExample;\n.annotation runtime LMarker;\nvalue = "
            + new string('{', 65) + "1" + new string('}', 65) + "\n.end annotation";
        Assert.Throws<FormatException>(() => SmaliAssembler.Assemble(source));
    }

    [Fact]
    public void Assemble_BodylessNamedParameter_DoesNotInternUnrepresentableDebugName()
    {
        var dex = SmaliAssembler.Assemble("""
            .class public abstract LExample;
            .method public abstract run(I)V
                .param p1, "debugOnlyName"
            .end method
            """);
        Assert.DoesNotContain("debugOnlyName", dex.Strings);
        var bytes = dex.ToArray();
        Assert.Equal(bytes, SmaliAssembler.Assemble(Disassembler.DisassembleAll(DexFile.Load(bytes))).ToArray());
    }

    [Theory]
    [InlineData(0x20)]
    [InlineData(0x43)]
    [InlineData(0x42)]
    [InlineData(0x84)]
    [InlineData(0x90)]
    [InlineData(0x3c)]
    [InlineData(0x3d)]
    [InlineData(0x3e)]
    [InlineData(0x5f)]
    public void Load_InvalidEncodedValueWidth_RejectsInput(byte header)
    {
        var bytes = SmaliAssembler.Assemble(".class public LExample;\n.field public static value:I = 1").ToArray();
        var classOffset = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(100));
        var valuesOffset = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(classOffset + 28));
        bytes[valuesOffset + 1] = header;
        Assert.Throws<InvalidDataException>(() => DexFile.Load(bytes));
    }

    [Fact]
    public void Load_EncodedArrayCountBeyondInput_RejectsBeforeAllocation()
    {
        var dex = SmaliAssembler.Assemble(".class public LExample;\n.field public static value:I = 1");
        dex.Classes[0].StaticValues = [EncodedValue.Array([EncodedValue.Long(long.MaxValue)])];
        var bytes = dex.ToArray();
        var classOffset = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(100));
        var valuesOffset = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(classOffset + 28));
        new byte[] { 0xff, 0xff, 0xff, 0xff, 0x0f }.CopyTo(bytes, valuesOffset + 2);
        Assert.Throws<InvalidDataException>(() => DexFile.Load(bytes));
    }

    [Fact]
    public void ToArray_ExcessiveEncodedValueDepth_RejectsBeforeStackExhaustion()
    {
        var dex = SmaliAssembler.Assemble(".class public LExample;\n.field public static value:I = 1");
        var value = EncodedValue.Int(1);
        for (var i = 0; i < 66; i++)
        {
            value = EncodedValue.Array([value]);
        }
        dex.Classes[0].StaticValues = [value];
        Assert.Throws<InvalidDataException>(() => dex.ToArray());
    }
}