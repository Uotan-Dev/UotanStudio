using UotanStudio.Firmware.AndroidMeta.Dex;
using UotanStudio.Firmware.AndroidMeta.Dex.Smali;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class DexSmaliExceptionTests
{
    [Fact]
    public void Assemble_TypedAndCatchAllHandlers_PreservesRangesAndTypeReferences()
    {
        var dex = SmaliAssembler.Assemble("""
            .class public LExample;
            .super Ljava/lang/Object;
            .method public static run()V
                .registers 1
                :start
                const/4 v0, 0
                :end
                return-void
                :typed
                move-exception v0
                return-void
                :all
                move-exception v0
                throw v0
                .catch Ljava/lang/Exception; {:start .. :end} :typed
                .catchall {:start .. :end} :all
            .end method
            """);

        var loaded = DexFile.Load(dex.ToArray());
        var code = Assert.Single(Assert.Single(loaded.Classes).DirectMethods).Code!;
        var range = Assert.Single(code.Tries);
        Assert.Equal(0, range.StartAddress);
        Assert.Equal(1, range.InsnCount);
        var handler = Assert.Single(code.Handlers);
        var entry = Assert.Single(handler.Entries);
        Assert.Equal("Ljava/lang/Exception;", loaded.Types[entry.TypeIndex]);
        Assert.Equal(2, entry.Address);
        Assert.Equal(4, handler.CatchAllAddress);
        var rebuilt = SmaliAssembler.Assemble(Disassembler.DisassembleAll(loaded));
        Assert.Equal(dex.ToArray(), rebuilt.ToArray());
    }

    [Fact]
    public void Assemble_OverlappingRanges_SplitsIntoOrderedNonOverlappingIntervals()
    {
        var dex = SmaliAssembler.Assemble("""
            .class public LExample;
            .method public static run()V
                .registers 1
                :start
                nop
                :middle
                nop
                :end
                return-void
                :handler
                move-exception v0
                return-void
                .catch Ljava/lang/Exception; {:start .. :end} :handler
                .catchall {:middle .. :end} :handler
            .end method
            """);

        var code = Assert.Single(Assert.Single(dex.Classes).DirectMethods).Code!;
        Assert.Equal(new[] { (0, 1), (1, 2) }, code.Tries.Select(range => (range.StartAddress, range.EndAddress)));
        Assert.False(code.Handlers[code.Tries[0].HandlerIndex].HasCatchAll);
        Assert.True(code.Handlers[code.Tries[1].HandlerIndex].HasCatchAll);
        Assert.Equal(dex.ToArray(), SmaliAssembler.Assemble(Disassembler.DisassembleAll(dex)).ToArray());
    }

    [Fact]
    public void Assemble_TryEndingAtCodeEnd_EmitsTerminalLabelAndRoundTrips()
    {
        var dex = SmaliAssembler.Assemble("""
            .class public LExample;
            .method public static run()V
                .registers 1
                goto :start
                :handler
                move-exception v0
                throw v0
                :start
                const/4 v0, 0
                throw v0
                :end
                .catchall {:start .. :end} :handler
            .end method
            """);

        var text = Disassembler.DisassembleAll(dex);
        Assert.Contains(":try_end_0", text);
        Assert.Equal(dex.ToArray(), SmaliAssembler.Assemble(text).ToArray());
    }

    [Theory]
    [InlineData(".catch I {:start .. :end} :handler")]
    [InlineData(".catch Ljava/lang/Exception; {:end .. :start} :handler")]
    [InlineData(".catchall {:start .. :start} :handler")]
    [InlineData(".catchall {:start .. :end} :missing")]
    [InlineData(".catchall {:start .. :end} :terminal")]
    [InlineData(".catchall {:start .. :end} :handler\n.catchall {:start .. :end} :start")]
    public void Assemble_InvalidCatchDirective_RejectsInput(string directive)
    {
        var source = $$"""
            .class public LExample;
            .method public static run()V
                .registers 1
                :start
                nop
                :end
                return-void
                :handler
                move-exception v0
                return-void
                :terminal
                {{directive}}
            .end method
            """;
        Assert.Throws<FormatException>(() => SmaliAssembler.Assemble(source));
    }

    [Fact]
    public void Assemble_LargeTryRange_SplitsAtInstructionBoundary()
    {
        var source = ".class public LExample;\n.method public static run()V\n.registers 1\n:start\n"
            + string.Concat(Enumerable.Repeat("const v0, 1\n", 22000))
            + ":end\nreturn-void\n:handler\nmove-exception v0\nreturn-void\n.catchall {:start .. :end} :handler\n.end method";
        var dex = SmaliAssembler.Assemble(source);
        var code = Assert.Single(Assert.Single(dex.Classes).DirectMethods).Code!;
        Assert.Equal(new[] { (0, 65535), (65535, 66000) }, code.Tries.Select(range => (range.StartAddress, range.EndAddress)));
        Assert.Single(code.Handlers);
        var loaded = DexFile.Load(dex.ToArray());
        Assert.Equal(code.RawInsns, Assert.Single(Assert.Single(loaded.Classes).DirectMethods).Code!.RawInsns);
    }

    [Fact]
    public void Assemble_OverlappingTypedCatches_PreservesFirstMatchingHandler()
    {
        var dex = SmaliAssembler.Assemble("""
            .class public LExample;
            .method public static run()V
                .registers 1
                :start
                nop
                :end
                return-void
                :first
                move-exception v0
                return-void
                :second
                move-exception v0
                throw v0
                .catch Ljava/lang/Exception; {:start .. :end} :first
                .catch Ljava/lang/Exception; {:start .. :end} :second
            .end method
            """);
        var code = Assert.Single(Assert.Single(dex.Classes).DirectMethods).Code!;
        Assert.Equal(2, Assert.Single(Assert.Single(code.Handlers).Entries).Address);
    }
}