using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Models;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Smali;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class DexSmaliFieldValueTests
{
    [Fact]
    public void Assemble_ImplicitStaticValues_OmitsEncodedArray()
    {
        var dex = DexFile.Load(SmaliAssembler.Assemble("""
            .class public LDefaults;
            .super Ljava/lang/Object;
            .field public static integer:I
            .field public static reference:Ljava/lang/Object;
            .field public static wide:J
            """).ToArray());

        var cls = Assert.Single(dex.Classes);
        Assert.True(cls.StaticValues is null or { Count: 0 });
        Assert.Equal(dex.ToArray(), SmaliAssembler.Assemble(Disassembler.DisassembleAll(dex)).ToArray());
    }

    [Fact]
    public void Assemble_UnsortedStaticValues_KeepsImplicitPrefixAndOmitsImplicitSuffix()
    {
        var dex = DexFile.Load(SmaliAssembler.Assemble("""
            .class public LDefaults;
            .super Ljava/lang/Object;
            .field public static z:I
            .field public static m:I = 42
            .field public static a:I
            """).ToArray());

        var cls = Assert.Single(dex.Classes);
        Assert.Equal(new[] { "a", "m", "z" }, cls.StaticFields.Select(field => dex.Strings[dex.Fields[field.FieldIndex].NameIndex]));
        Assert.Equal(new[] { 0, 42 }, cls.StaticValues!.Select(value => value.AsInt()));
        Assert.Equal(dex.ToArray(), SmaliAssembler.Assemble(Disassembler.DisassembleAll(dex)).ToArray());
    }

    [Theory]
    [InlineData("0.0f", 0)]
    [InlineData("-0.0f", int.MinValue)]
    public void Assemble_ExplicitTrailingZero_PreservesEncodedBits(string literal, int bits)
    {
        var dex = DexFile.Load(SmaliAssembler.Assemble($"""
            .class public LDefaults;
            .super Ljava/lang/Object;
            .field public static value:F = {literal}
            """).ToArray());

        var value = Assert.Single(Assert.Single(dex.Classes).StaticValues!);
        Assert.Equal(bits, BitConverter.SingleToInt32Bits(Assert.IsType<float>(value.Value)));
        Assert.Equal(dex.ToArray(), SmaliAssembler.Assemble(Disassembler.DisassembleAll(dex)).ToArray());
    }

    internal const string Source = """
        .class public LExample;
        .super Ljava/lang/Object;
        .field public static zArray:[I = {
            0xffffffff,
            1,
            { 1t, true, null }
        }
        .field public static metadata:LMeta; = .subannotation LMeta;
            z = "nested"
            a = 2
        .end subannotation
            .annotation runtime LFieldMarker;
            .end annotation
        .end field
        .field public static prototype:Ljava/lang/invoke/MethodType; = (I)V
        .field public static handle:Ljava/lang/invoke/MethodHandle; = invoke-static@LTarget;->call(I)V
        .field public static fieldRef:Ljava/lang/Object; = LTarget;->flag:I
        .field public static methodRef:Ljava/lang/Object; = LTarget;->call(I)V
        .field public static enumRef:LState; = .enum LState;->ON:LState;
        .field public static declaredByte:B = 128
        .field public static declaredBoolean:Z = 2
        .field public static declaredFloat:F = 1
        .field public static declaredArray:[I = 1
        .field public static minusByte:B = 0xfft
        .field public static nan:F = NaNf
        .field public static hexFloat:F = 0x1.8p2f
        .field public static octal:I = 010
        """;

    [Fact]
    public void Assemble_ExplicitFieldValues_PreservesLiteralTypesWithoutDescriptorCoercion()
    {
        var dex = DexFile.Load(SmaliAssembler.Assemble(Source).ToArray());
        var cls = Assert.Single(dex.Classes);
        var values = cls.StaticFields.Select((field, i) => (Name: dex.Strings[dex.Fields[field.FieldIndex].NameIndex], Value: cls.StaticValues![i]))
            .ToDictionary(item => item.Name, item => item.Value);
        Assert.Equal(EncodedValueType.Int, values["declaredByte"].Type);
        Assert.Equal(128, values["declaredByte"].AsInt());
        Assert.Equal(EncodedValueType.Int, values["declaredBoolean"].Type);
        Assert.Equal(EncodedValueType.Int, values["declaredFloat"].Type);
        Assert.Equal(EncodedValueType.Int, values["declaredArray"].Type);
        Assert.Equal(EncodedValueType.Array, values["zArray"].Type);
        Assert.Equal(EncodedValueType.MethodHandle, values["handle"].Type);
        Assert.Equal(EncodedValueType.MethodType, values["prototype"].Type);
        Assert.Equal(8, values["octal"].AsInt());
        Assert.Equal(6f, Assert.IsType<float>(values["hexFloat"].Value));
        Assert.Equal(0x7fc00000, BitConverter.SingleToInt32Bits(Assert.IsType<float>(values["nan"].Value)));
        var metadata = Assert.IsType<AnnotationItem>(values["metadata"].Value);
        Assert.Equal(new[] { "a", "z" }, metadata.Elements.Select(element => dex.Strings[element.NameIndex]));
        Assert.Single(cls.Annotations!.FieldAnnotations);
        Assert.Equal(dex.ToArray(), SmaliAssembler.Assemble(Disassembler.DisassembleAll(dex)).ToArray());
    }
}