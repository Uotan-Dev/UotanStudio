using System.Buffers.Binary;
using UotanStudio.Firmware.AndroidMeta.Dex;
using UotanStudio.Firmware.AndroidMeta.Dex.Models;
using UotanStudio.Firmware.AndroidMeta.Dex.Smali;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class DexSmaliCallSiteTests
{
    internal const string Source = """
        .class public LExample;
        .super Ljava/lang/Object;
        .method public static run(IJ)V
            .registers 3
            invoke-custom {p0, p1, p2}, site("dispatch#()", (IJ)V, 42, -3L, 1.5f, -2.0, "text,)#", Ljava/lang/String;, (I)I, invoke-static@LTarget;->call(I)I)@LBootstrap;->link(Ljava/lang/invoke/MethodHandles$Lookup;Ljava/lang/String;Ljava/lang/invoke/MethodType;)Ljava/lang/invoke/CallSite;
            invoke-custom/range {p0 .. p2}, site("dispatch#()", (IJ)V, 42, -3L, 1.5f, -2.0, "text,)#", Ljava/lang/String;, (I)I, invoke-static@LTarget;->call(I)I)@LBootstrap;->link(Ljava/lang/invoke/MethodHandles$Lookup;Ljava/lang/String;Ljava/lang/invoke/MethodType;)Ljava/lang/invoke/CallSite;
            return-void
        .end method
        """;

    internal const string MultiMethodSource = """
        .class public LExample;
        .super Ljava/lang/Object;
        .method public static z()V
            .registers 0
            invoke-custom {}, shared("shared", ()V)@LBootstrap;->link()Ljava/lang/invoke/CallSite;
            invoke-custom {}, z("last", ()V)@LBootstrap;->link()Ljava/lang/invoke/CallSite;
            return-void
        .end method
        .method public static a()V
            .registers 0
            invoke-custom {}, shared("shared", ()V)@LBootstrap;->link()Ljava/lang/invoke/CallSite;
            invoke-custom {}, a("first", ()V)@LBootstrap;->link()Ljava/lang/invoke/CallSite;
            return-void
        .end method
        """;

    internal const string NestedSource = """
        .class public LExample;
        .super Ljava/lang/Object;
        .method public static run()V
            .registers 0
            invoke-custom {}, site("run", ()V,
                {1, ')', ',', {true, null}},
                .subannotation LMarker;
                    z = "nested"
                    a = 2
                .end subannotation,
                .enum LState;->ON:LState;)@link()Ljava/lang/invoke/CallSite;
            return-void
        .end method
        """;

    [Fact]
    public void Assemble_ReorderedMethods_PreservesSharedAndDistinctCallSites()
    {
        var bytes = SmaliAssembler.Assemble(MultiMethodSource).ToArray();
        var dex = DexFile.Load(bytes);
        Assert.Equal(3, dex.CallSites.Count);
        Assert.Equal(bytes, SmaliAssembler.Assemble(Disassembler.DisassembleAll(dex)).ToArray());
    }

    [Fact]
    public void Assemble_CallSiteReferences_PreservesArgumentsAndSharedIdentity()
    {
        var bytes = SmaliAssembler.Assemble(Source).ToArray();
        var dex = DexFile.Load(bytes);
        Assert.Equal("038", dex.Header.Version);
        var site = Assert.Single(dex.CallSites);
        Assert.Equal(11, site.Count);
        Assert.Equal((ushort)4, dex.MethodHandles[site[0].AsIndex()].Kind);
        Assert.Equal("dispatch#()", dex.Strings[site[1].AsIndex()]);
        Assert.Equal("text,)#", dex.Strings[site[7].AsIndex()]);
        var code = Assert.Single(Assert.Single(dex.Classes).DirectMethods).Code!;
        Assert.Equal((ushort)3, code.OutsSize);
        var instructions = InstructionDecoder.DecodeForSmali(code.RawInsns, dex);
        Assert.Equal(0, Assert.IsType<CallSiteRefOperand>(instructions[0].Operands[1]).Index);
        Assert.Equal(0, Assert.IsType<CallSiteRefOperand>(instructions[1].Operands[1]).Index);
        var text = Disassembler.DisassembleAll(dex);
        Assert.Contains("call_site_0(", text);
        Assert.Equal(bytes, SmaliAssembler.Assemble(text).ToArray());
    }

    [Fact]
    public void Assemble_DifferentCallSiteNames_PreservesSeparateResolutionSlots()
    {
        var source = Source.Replace("invoke-custom/range {p0 .. p2}, site(", "invoke-custom/range {p0 .. p2}, other(", StringComparison.Ordinal);
        var bytes = SmaliAssembler.Assemble(source).ToArray();
        var dex = DexFile.Load(bytes);
        Assert.Equal(2, dex.CallSites.Count);
        var code = dex.Classes[0].DirectMethods[0].Code!;
        var instructions = InstructionDecoder.DecodeForSmali(code.RawInsns, dex);
        Assert.NotEqual(Assert.IsType<CallSiteRefOperand>(instructions[0].Operands[1]).Index,
            Assert.IsType<CallSiteRefOperand>(instructions[1].Operands[1]).Index);
        Assert.Equal(bytes, SmaliAssembler.Assemble(Disassembler.DisassembleAll(dex)).ToArray());
    }

    [Fact]
    public void Assemble_NestedLiteralArguments_PreservesMultilineArraysAnnotationsAndCharacters()
    {
        var bytes = SmaliAssembler.Assemble(NestedSource).ToArray();
        var dex = DexFile.Load(bytes);
        var site = Assert.Single(dex.CallSites);
        Assert.Equal(6, site.Count);
        Assert.Equal(EncodedValueType.Array, site[3].Type);
        Assert.Equal(EncodedValueType.Annotation, site[4].Type);
        var annotation = Assert.IsType<AnnotationItem>(site[4].Value);
        Assert.Equal(new[] { "a", "z" }, annotation.Elements.Select(element => dex.Strings[element.NameIndex]));
        Assert.Equal(EncodedValueType.Enum, site[5].Type);
        Assert.Equal(bytes, SmaliAssembler.Assemble(Disassembler.DisassembleAll(dex)).ToArray());
    }

    [Fact]
    public void Disassemble_InvalidCallSiteIndex_RejectsInsteadOfRenderingRegister()
    {
        var dex = SmaliAssembler.Assemble(Source);
        dex.Classes[0].DirectMethods[0].Code!.RawInsns[1] = ushort.MaxValue;
        Assert.Throws<InvalidDataException>(() => Disassembler.DisassembleAll(dex));
    }

    [Fact]
    public void Assemble_TabSeparatedMultilineCallSite_PreservesArguments()
    {
        var source = NestedSource.Replace("invoke-custom {}", "invoke-custom\t{}", StringComparison.Ordinal);
        Assert.Equal(SmaliAssembler.Assemble(NestedSource).ToArray(), SmaliAssembler.Assemble(source).ToArray());
    }

    [Theory]
    [InlineData("site")]
    [InlineData("site(\"run\")@LBootstrap;->link()V")]
    [InlineData("site(1, ()V)@LBootstrap;->link()V")]
    [InlineData("site(\"run\", V)@LBootstrap;->link()V")]
    [InlineData("site(\"run\", ()V)LBootstrap;->link()V")]
    [InlineData("site(\"run\", ()V)@LBootstrap;->field:I")]
    [InlineData("site(\"run\", ()V, 1 2)@LBootstrap;->link()V")]
    [InlineData("site(\"run\", ()V, {1,})@LBootstrap;->link()V")]
    [InlineData("site(\"run\", ()V")]
    public void Assemble_InvalidCallSite_RejectsInput(string site)
    {
        Assert.Throws<FormatException>(() => SmaliAssembler.Assemble(
            ".class public LExample;\n.method public static run()V\n.registers 0\ninvoke-custom {}, "
            + site + "\nreturn-void\n.end method"));
    }

    [Fact]
    public void ToArray_NonStaticBootstrap_RejectsInvalidHandleKind()
    {
        var dex = SmaliAssembler.Assemble(Source);
        dex.MethodHandles[dex.CallSites[0][0].AsIndex()].Kind = 5;
        Assert.Throws<DexException>(dex.ToArray);
        Assert.Throws<InvalidDataException>(() => Disassembler.DisassembleAll(dex));
    }

    [Fact]
    public void Load_NonStaticBootstrap_RejectsInvalidHandleKind()
    {
        var bytes = SmaliAssembler.Assemble(Source).ToArray();
        var map = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(52));
        var count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(map));
        var handles = 0;
        for (var i = 0; i < count; i++)
        {
            var entry = map + 4 + i * 12;
            if (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(entry)) == 8)
            {
                handles = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(entry + 8));
            }
        }
        Assert.NotEqual(0, handles);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(handles), 5);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), Hash.Adler32.Adler32.Compute(bytes.AsSpan(12)));
        Assert.Throws<DexException>(() => DexFile.Load(bytes));
    }
}