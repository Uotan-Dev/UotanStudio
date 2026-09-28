using System.Globalization;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Instructions;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Smali;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class DexSmaliJumboTests
{
    internal static string CreateSource(int count)
    {
        var source = new StringBuilder("""
            .class public LExample;
            .super Ljava/lang/Object;
            .method public static run()V
                .registers 1
                :start
                .line 3
                goto :target
                const-string v0, "zzzz"
            """ + "\n");
        for (var i = 0; i < 124; i++)
        {
            source.AppendLine("nop");
        }
        source.AppendLine("""
                :target
                .line 10
                const-string v0, "zzzz"
                :end
                return-void
                :handler
                move-exception v0
                return-void
                .catchall {:start .. :end} :handler
            .end method
            .annotation runtime LMarker;
                values = {
            """);
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                source.AppendLine(",");
            }
            source.Append('"').Append('a').Append(i.ToString("D5", CultureInfo.InvariantCulture)).Append('"');
        }
        source.AppendLine("\n}\n.end annotation");
        return source.ToString();
    }

    [Theory]
    [InlineData(65529, false)]
    [InlineData(65530, true)]
    public void Assemble_StringIndexBoundary_RelocatesJumboBranchesCatchesAndDebug(int count, bool jumbo)
    {
        var dex = SmaliAssembler.Assemble(CreateSource(count));
        Assert.Equal(jumbo ? 65536 : 65535, dex.Strings.IndexOf("zzzz"));
        var code = dex.Classes[0].DirectMethods[0].Code!;
        var instructions = InstructionDecoder.DecodeForSmali(code.RawInsns, dex);
        Assert.Equal(jumbo ? "goto/16" : "goto", instructions[0].Mnemonic);
        var strings = instructions.Where(instruction => instruction.Mnemonic.StartsWith("const-string", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, strings.Length);
        Assert.All(strings, instruction => Assert.Equal(jumbo ? "const-string/jumbo" : "const-string", instruction.Mnemonic));
        Assert.All(strings, instruction => Assert.Equal("zzzz", instruction.Operands[1]));
        var text = Disassembler.DisassembleAll(DexFile.Load(dex.ToArray()));
        Assert.Contains(".line 10", text);
        Assert.Contains(".catchall", text);
        Assert.Equal(dex.ToArray(), SmaliAssembler.Assemble(text).ToArray());
    }

    [Fact]
    public void Assemble_LargePoolBeforeMethod_ExpandsDuringInitialEncoding()
    {
        var source = CreateSource(65530);
        var annotation = source.IndexOf(".annotation", StringComparison.Ordinal);
        var method = source.IndexOf(".method", StringComparison.Ordinal);
        var reordered = source[..method] + source[annotation..] + "\n" + source[method..annotation];
        Assert.Equal(SmaliAssembler.Assemble(source).ToArray(), SmaliAssembler.Assemble(reordered).ToArray());
    }
}