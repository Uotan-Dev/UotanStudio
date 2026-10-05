using UotanStudio.Firmware.AndroidMeta.Dex;
using UotanStudio.Firmware.AndroidMeta.Dex.Smali;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class DexSmaliDebugTests
{
    internal const string SignedLineSource = """
        .class public LExample;
        .super Ljava/lang/Object;
        .method public static run()V
            .registers 0
            .line -1
            nop
            .line 2147483647
            nop
            .line -2147483648
            nop
            .line 0
            return-void
        .end method
        """;

    internal const string Source = """
        .class public LExample;
        .super Ljava/lang/Object;
        .source "Original.java"
        .method public static run(I)V
            .registers 3
            .param p0, "argument"
            .prologue
            .line 100
            const/4 v0, 0
            .line 103
            .local v0, "counter":I
            const/4 v1, 0
            .source "Other#File.java"
            .line 20
            .local v1, "items":Ljava/util/List;, "Ljava/util/List<Ljava/lang/String;>;"
            nop
            .line 10000
            .end local v0
            nop
            .epilogue
            .restart local v0
            .end local v1
            return-void
        .end method
        """;

    [Fact]
    public void Assemble_DebugEvents_PreservesLocationsNamesAndLifetimes()
    {
        var dex = SmaliAssembler.Assemble(Source);
        var bytes = dex.ToArray();
        var text = Disassembler.DisassembleAll(DexFile.Load(bytes));
        Assert.Contains(".line 10000", text);
        Assert.Contains(".local v0, \"counter\":I", text);
        Assert.Contains(".restart local v0", text);
        Assert.Contains(".source \"Other#File.java\"", text);
        Assert.Contains(".prologue", text);
        Assert.Contains(".epilogue", text);
        Assert.Equal(bytes, SmaliAssembler.Assemble(text).ToArray());
    }

    [Fact]
    public void Assemble_CoLocatedDebugEvents_PreservesDeclaredOrder()
    {
        var normalizedSource = Source.ReplaceLineEndings("\n");
        var source = normalizedSource.Replace(".line 103\n    .local v0, \"counter\":I",
            ".local v0, \"counter\":I\n    .line 103", StringComparison.Ordinal);
        Assert.NotEqual(normalizedSource, source);
        var bytes = SmaliAssembler.Assemble(source).ToArray();
        var text = Disassembler.DisassembleAll(DexFile.Load(bytes));
        Assert.True(text.IndexOf(".local v0", StringComparison.Ordinal) < text.IndexOf(".line 103", StringComparison.Ordinal));
        Assert.Equal(bytes, SmaliAssembler.Assemble(text).ToArray());
    }

    [Fact]
    public void Assemble_WidenedBranch_RelocatesDebugEventsWithInstructions()
    {
        var source = Source.Replace("const/4 v0, 0",
            "goto :far\n" + string.Concat(Enumerable.Repeat("nop\n", 128)) + ":far\nconst/4 v0, 0", StringComparison.Ordinal);
        var bytes = SmaliAssembler.Assemble(source).ToArray();
        var text = Disassembler.DisassembleAll(DexFile.Load(bytes));
        Assert.Contains("goto/16", text);
        Assert.Contains(".line 103", text);
        Assert.Equal(bytes, SmaliAssembler.Assemble(text).ToArray());
    }

    [Fact]
    public void Assemble_AnonymousLocalAndSourceReset_PreservesNullReferences()
    {
        var dex = SmaliAssembler.Assemble("""
            .class public LExample;
            .method public static run()V
                .registers 1
                .source
                .local v0
                return-void
                .end local v0
            .end method
            """);
        var bytes = dex.ToArray();
        var text = Disassembler.DisassembleAll(DexFile.Load(bytes));
        Assert.Contains(".local v0, null:V", text);
        Assert.Contains(".source" + Environment.NewLine, text);
        Assert.Equal(bytes, SmaliAssembler.Assemble(text).ToArray());
    }

    [Fact]
    public void Assemble_SignedDebugLineRegister_PreservesNegativeAndWrappingDeltas()
    {
        var bytes = SmaliAssembler.Assemble(SignedLineSource).ToArray();
        var text = Disassembler.DisassembleAll(DexFile.Load(bytes));
        Assert.Contains(".line -1", text);
        Assert.Contains(".line 2147483647", text);
        Assert.Contains(".line -2147483648", text);
        Assert.Equal(bytes, SmaliAssembler.Assemble(text).ToArray());
        Assert.Equal(bytes, SmaliAssembler.Assemble(SignedLineSource
            .ReplaceLineEndings("\n")
            .Replace(".line -1\n", ".line 0xffffffffL\n", StringComparison.Ordinal)).ToArray());
    }

    [Theory]
    [InlineData(".local v3, \"x\":I")]
    [InlineData(".end local p1")]
    [InlineData(".restart local v-1")]
    [InlineData(".local")]
    [InlineData(".local v0, \"x\"")]
    [InlineData(".line 4294967296")]
    public void Assemble_InvalidDebugDirective_RejectsInput(string directive)
    {
        Assert.Throws<FormatException>(() => SmaliAssembler.Assemble(
            ".class public LExample;\n.method public static run(I)V\n.registers 3\n"
            + directive + "\nreturn-void\n.end method"));
    }

    [Theory]
    [InlineData(1, 2, 0)]
    [InlineData(5, 3, 0)]
    [InlineData(9, 127, 0)]
    [InlineData(1, 1, 14)]
    public void Disassemble_InvalidDebugState_RejectsWithoutDroppingEvents(byte a, byte b, byte c)
    {
        var dex = SmaliAssembler.Assemble(".class public LExample;\n.method public static run()V\n.registers 1\nreturn-void\n.end method");
        dex.Classes[0].DirectMethods[0].Code!.DebugInfo = [1, 0, a, b, c];
        Assert.Throws<InvalidDataException>(() => Disassembler.DisassembleAll(dex));
    }
}