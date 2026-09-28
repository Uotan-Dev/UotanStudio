using UotanStudio.Firmware.AndroidMeta.Dex;
using UotanStudio.Firmware.AndroidMeta.Dex.Smali;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class DexSmaliOrderingTests
{
    [Fact]
    public void Assemble_UnorderedDeclarations_SortsIdentifiersAndRetainsReferences()
    {
        var dex = SmaliAssembler.Assemble("""
            .class public La/Child;
            .super Lz/Parent;
            .source "Source.java"
            .field public static z:Ljava/lang/String; = "last"
            .field public static a:Ljava/lang/String; = "first"
            .method public static z()V
                .registers 1
                const-string v0, "last"
                sget-object v0, La/Child;->a:Ljava/lang/String;
                invoke-static {}, La/Child;->a()V
                return-void
            .end method
            .method public static a()V
                .registers 0
                return-void
            .end method
            .class public Lz/Parent;
            .super Ljava/lang/Object;
            """);

        Assert.Equal(dex.Strings.Order(StringComparer.Ordinal), dex.Strings);
        Assert.Equal(dex.Types.Order(StringComparer.Ordinal), dex.Types);
        Assert.Equal(new[] { "Lz/Parent;", "La/Child;" }, dex.Classes.Select(cls => dex.Types[cls.TypeIndex]));
        Assert.Equal(new[] { "a", "z" }, dex.Fields.Select(field => dex.Strings[field.NameIndex]));
        Assert.Equal(new[] { "a", "z" }, dex.Methods.Select(method => dex.Strings[method.NameIndex]));
        var loaded = DexFile.Load(dex.ToArray());
        var child = loaded.Classes.Single(cls => loaded.Types[cls.TypeIndex] == "La/Child;");
        Assert.Equal("Source.java", loaded.Strings[child.SourceFileIndex]);
        Assert.Equal(new[] { "first", "last" }, child.StaticValues!.Select(value => loaded.Strings[value.AsIndex()]));
        var text = Disassembler.DisassembleAll(loaded);
        Assert.Contains("const-string v0, \"last\"", text);
        Assert.Contains("La/Child;->a:Ljava/lang/String;", text);
        Assert.Contains("La/Child;->a()V", text);
        Assert.Equal(dex.ToArray(), SmaliAssembler.Assemble(text).ToArray());
    }

    [Theory]
    [InlineData(".class public LA;\n.super LB;\n.class public LB;\n.super LA;")]
    [InlineData(".class public LA;\n.class public LA;")]
    [InlineData(".class public LA;\n.field public static a:I\n.field public a:I")]
    [InlineData(".class public LA;\n.method public abstract a()V\n.end method\n.method public abstract a()V\n.end method")]
    public void Assemble_DuplicateOrCyclicDeclarations_RejectsInput(string source)
    {
        Assert.Throws<FormatException>(() => SmaliAssembler.Assemble(source));
    }

    [Fact]
    public void Assemble_OverloadedMethods_SortsPrototypesByReturnThenParameterTypes()
    {
        var dex = SmaliAssembler.Assemble("""
            .class public abstract LExample;
            .super Ljava/lang/Object;
            .method public abstract z(J)V
            .end method
            .method public abstract z(II)V
            .end method
            .method public abstract z(I)V
            .end method
            .method public abstract z()V
            .end method
            .method public abstract z()I
            .end method
            """);

        Assert.Equal(new[] { "()I", "()V", "(I)V", "(II)V", "(J)V" },
            dex.Protos.Select(proto => "(" + string.Concat(proto.ParameterTypeIndices.Select(index => dex.Types[index]))
                + ")" + dex.Types[proto.ReturnTypeIndex]));
        Assert.Equal(Enumerable.Range(0, 5), dex.Methods.Select(method => method.ProtoIndex));
        var loaded = DexFile.Load(dex.ToArray());
        Assert.Equal(Enumerable.Range(0, 5), Assert.Single(loaded.Classes).VirtualMethods.Select(method => method.MethodIndex));
    }
}