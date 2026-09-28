using UotanStudio.Firmware.AndroidMeta.Dex;
using UotanStudio.Firmware.AndroidMeta.Dex.Models;
using UotanStudio.Firmware.AndroidMeta.Dex.Smali;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class DexSmaliMethodHandleTests
{
    internal const string Source = """
        .class public LExample;
        .super Ljava/lang/Object;
        .annotation runtime LHandles;
            values = {
                invoke-interface@LTarget;->go()V,
                invoke-direct@LTarget;->go()V,
                invoke-constructor@LTarget;-><init>()V,
                invoke-instance@LTarget;->go()V,
                invoke-static@LTarget;->go()V,
                instance-get@LTarget;->z:I,
                instance-put@LTarget;->z:I,
                static-get@LTarget;->a:I,
                static-put@LTarget;->a:I
            }
        .end annotation
        .method public static run()Ljava/lang/invoke/MethodHandle;
            .registers 1
            const-method-handle v0, invoke-interface@LTarget;->go()V
            return-object v0
        .end method
        """;

    [Fact]
    public void Assemble_AllHandleKinds_RelocatesAnnotationAndInstructionReferences()
    {
        var dex = SmaliAssembler.Assemble(Source);
        Assert.Equal("039", dex.Header.Version);
        Assert.Equal(Enumerable.Range(0, 9).Select(value => (ushort)value), dex.MethodHandles.Select(handle => handle.Kind));
        var loaded = DexFile.Load(dex.ToArray());
        var cls = Assert.Single(loaded.Classes);
        var annotation = Assert.Single(cls.Annotations!.ClassAnnotations!.Annotations);
        var values = Assert.Single(annotation.Elements).Value.AsArray()!;
        Assert.Equal(Enumerable.Range(0, 9).Reverse().Select(value => (ushort)value),
            values.Select(value => loaded.MethodHandles[value.AsIndex()].Kind));
        var code = Assert.Single(cls.DirectMethods).Code!;
        var instructions = InstructionDecoder.Decode(code.RawInsns, loaded);
        var reference = Assert.IsType<MethodHandleRefOperand>(instructions[0].Operands[1]);
        Assert.Equal(8, loaded.MethodHandles[reference.Index].Kind);
        var text = Disassembler.DisassembleAll(loaded);
        Assert.Contains("const-method-handle v0, invoke-interface@LTarget;->go()V", text);
        Assert.Equal(dex.ToArray(), SmaliAssembler.Assemble(text).ToArray());
    }

    [Fact]
    public void Assemble_AnnotationOnlyHandle_SelectsDex038()
    {
        var dex = SmaliAssembler.Assemble("""
            .class public LExample;
            .annotation runtime LMarker;
                handle = static-get@LTarget;->count:I
            .end annotation
            """);
        Assert.Equal("038", dex.Header.Version);
        Assert.Single(DexFile.Load(dex.ToArray()).MethodHandles);
    }

    [Theory]
    [InlineData("unknown@LTarget;->go()V")]
    [InlineData("static-get@LTarget;->go()V")]
    [InlineData("invoke-static@LTarget;->count:I")]
    [InlineData("invoke-static")]
    public void Assemble_InvalidMethodHandle_RejectsInput(string handle)
    {
        Assert.Throws<FormatException>(() => SmaliAssembler.Assemble(
            ".class public LExample;\n.method public static run()V\n.registers 1\nconst-method-handle v0, "
            + handle + "\nreturn-void\n.end method"));
    }
}