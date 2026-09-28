using UotanStudio.Firmware.AndroidMeta.Dex;
using UotanStudio.Firmware.AndroidMeta.Dex.Models;
using UotanStudio.Firmware.AndroidMeta.Dex.Smali;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class DexAccessFlagsTests
{
    [Theory]
    [InlineData(AccessFlags.Volatile, 0x0040u)]
    [InlineData(AccessFlags.Transient, 0x0080u)]
    [InlineData(AccessFlags.Native, 0x0100u)]
    [InlineData(AccessFlags.Interface, 0x0200u)]
    [InlineData(AccessFlags.Abstract, 0x0400u)]
    [InlineData(AccessFlags.Strict, 0x0800u)]
    [InlineData(AccessFlags.Synthetic, 0x1000u)]
    [InlineData(AccessFlags.Annotation, 0x2000u)]
    [InlineData(AccessFlags.Enum, 0x4000u)]
    [InlineData(AccessFlags.Constructor, 0x10000u)]
    [InlineData(AccessFlags.DeclaredSynchronized, 0x20000u)]
    public void Constants_StandardDexModifiers_MatchWireValues(uint actual, uint expected)
    {
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Format_SharedBits_UsesDeclarationContext()
    {
        Assert.Equal(0x40u, AccessFlags.Bridge);
        Assert.Equal(0x80u, AccessFlags.Varargs);
        Assert.Equal("public volatile transient", AccessFlags.FormatField(0xc1));
        Assert.Equal("public bridge varargs", AccessFlags.FormatMethod(0xc1));
        Assert.Equal("public interface abstract annotation", AccessFlags.FormatClass(0x2601));
        Assert.Equal("public declared-synchronized", AccessFlags.FormatMethod(0x20001));
    }

    [Theory]
    [InlineData("[Ljava/lang/Object;", "java.lang.Object[]")]
    [InlineData("[[I", "int[][]")]
    public void FormatDescriptor_ArrayType_UsesCompleteBracketPairs(string descriptor, string expected)
    {
        Assert.Equal(expected, DexFile.FormatDescriptor(descriptor));
    }

    [Fact]
    public void Assemble_UnknownModifier_RejectsInsteadOfDiscarding()
    {
        Assert.Throws<FormatException>(() => SmaliAssembler.Assemble(".class public typo LExample;"));
    }
}