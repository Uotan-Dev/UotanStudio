using System.Text;
using UotanStudio.Firmware.AndroidMeta.Dex;
using UotanStudio.Firmware.AndroidMeta.Dex.Smali;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class DexSmaliInternalizationTests
{
    [Fact]
    public void Assemble_RepeatedStructuralKeys_InternsOneEntryPerDistinctKey()
    {
        var text = new StringBuilder();
        for (var index = 0; index < 24; index++)
        {
            text.Append(".class public final Lexample/Unit").Append(index).Append(";\n")
                .Append(".super Ljava/lang/Object;\n")
                .Append(".method public static run(I)V\n")
                .Append("    .registers 2\n")
                .Append("    sget v0, Lexample/Shared;->value:I\n")
                .Append("    invoke-static {v0}, Lexample/Shared;->accept(I)V\n")
                .Append("    return-void\n")
                .Append(".end method\n")
                .Append(".method public static other(int)void\n")
                .Append("    .registers 2\n")
                .Append("    sget v0, Lexample/Shared;->value:I\n")
                .Append("    invoke-static {v0}, Lexample/Shared;->accept(I)V\n")
                .Append("    return-void\n")
                .Append(".end method\n");
        }

        var dex = SmaliAssembler.Assemble(text.ToString());

        Assert.Single(dex.Fields);
        Assert.Single(dex.Protos);
        Assert.Equal(1, dex.Strings.Count(name => name == "value"));
        Assert.Equal(1, dex.Types.Count(descriptor => descriptor == "Lexample/Shared;"));
        Assert.Equal(49, dex.Methods.Count);
        Assert.Equal(dex.Types.Count, dex.Types.Distinct().Count());
        Assert.Equal(dex.Fields.Count, dex.Fields.Select(field => (field.ClassIndex, field.NameIndex, field.TypeIndex)).Distinct().Count());
        Assert.Equal(dex.Methods.Count, dex.Methods.Select(method => (method.ClassIndex, method.NameIndex, method.ProtoIndex)).Distinct().Count());
        Assert.Equal(dex.Protos.Count, dex.Protos.Select(proto => (proto.ReturnTypeIndex, string.Join(',', proto.ParameterTypeIndices))).Distinct().Count());
    }

    [Fact]
    public void Assemble_RelocatedTables_EncodeLinearFirstHitIndexOfEachReference()
    {
        var dex = SmaliAssembler.Assemble("""
            .class public Lz/Zulu;
            .super Ljava/lang/Object;
            .field public static zulu:Lz/Alpha;
            .method public static run(Lz/Alpha;)V
                .registers 2
                sget-object v0, Lz/Zulu;->zulu:Lz/Alpha;
                const-class v0, Lz/Zulu;
                const-string v0, "zulu text"
                invoke-static {p0}, Lz/Zulu;->take(Lz/Alpha;)V
                return-void
            .end method
            .method public static take(Lz/Alpha;)V
                .registers 1
                return-void
            .end method
            """);

        var cls = Assert.Single(dex.Classes);
        var run = cls.DirectMethods.Single(method => dex.Strings[dex.Methods[method.MethodIndex].NameIndex] == "run");
        var raw = run.Code!.RawInsns;

        Assert.Equal(0x62, raw[0] & 0xff);
        Assert.Equal(LinearFieldIndex(dex, "Lz/Zulu;", "zulu", "Lz/Alpha;"), raw[1]);
        Assert.Equal("Lz/Zulu;", dex.Types[dex.Fields[raw[1]].ClassIndex]);
        Assert.Equal("zulu", dex.Strings[dex.Fields[raw[1]].NameIndex]);
        Assert.Equal("Lz/Alpha;", dex.Types[dex.Fields[raw[1]].TypeIndex]);

        Assert.Equal(0x1c, raw[2] & 0xff);
        Assert.Equal(LinearTypeIndex(dex, "Lz/Zulu;"), raw[3]);
        Assert.Equal("Lz/Zulu;", dex.Types[raw[3]]);

        Assert.Equal(0x1a, raw[4] & 0xff);
        Assert.Equal(dex.Strings.ToList().IndexOf("zulu text"), raw[5]);

        Assert.Equal(0x71, raw[6] & 0xff);
        Assert.Equal(LinearMethodIndex(dex, "Lz/Zulu;", "take", "Lz/Alpha;"), raw[7]);
        Assert.Equal("take", dex.Strings[dex.Methods[raw[7]].NameIndex]);
    }

    [Theory]
    [InlineData("nop")]
    [InlineData("NOP")]
    [InlineData("Move/From16")]
    [InlineData("const-string/jumbo")]
    [InlineData("invoke-custom/range")]
    [InlineData("const-method-type")]
    public void Find_Mnemonic_MatchesLowestOpcodeRegisteredWithThatName(string mnemonic)
    {
        var expected = -1;
        for (var index = 0; index < 256; index++)
        {
            if (OpCodes.Get((byte)index) is { } info && info.Mnemonic.Equals(mnemonic, StringComparison.OrdinalIgnoreCase))
            {
                expected = index;
                break;
            }
        }

        Assert.NotNull(OpCodes.Find(mnemonic));
        Assert.Equal(expected, OpCodes.Find(mnemonic)!.Value.Opcode);
        Assert.Equal(expected, OpCodes.Find(mnemonic.ToLowerInvariant())!.Value.Opcode);
        Assert.Equal(expected, OpCodes.Find(mnemonic.ToUpperInvariant())!.Value.Opcode);
        Assert.Null(OpCodes.Find("not-an-opcode"));
        Assert.Null(OpCodes.Find(string.Empty));
    }

    private static int LinearTypeIndex(DexFile dex, string descriptor)
    {
        for (var index = 0; index < dex.Types.Count; index++)
        {
            if (dex.Types[index] == descriptor)
            {
                return index;
            }
        }
        return -1;
    }

    private static int LinearFieldIndex(DexFile dex, string owner, string name, string type)
    {
        for (var index = 0; index < dex.Fields.Count; index++)
        {
            var field = dex.Fields[index];
            if (dex.Types[field.ClassIndex] == owner && dex.Strings[field.NameIndex] == name && dex.Types[field.TypeIndex] == type)
            {
                return index;
            }
        }
        return -1;
    }

    private static int LinearMethodIndex(DexFile dex, string owner, string name, string parameter)
    {
        for (var index = 0; index < dex.Methods.Count; index++)
        {
            var method = dex.Methods[index];
            var proto = dex.Protos[method.ProtoIndex];
            if (dex.Types[method.ClassIndex] == owner && dex.Strings[method.NameIndex] == name
                && proto.ParameterTypeIndices.Length == 1 && dex.Types[proto.ParameterTypeIndices[0]] == parameter)
            {
                return index;
            }
        }
        return -1;
    }
}