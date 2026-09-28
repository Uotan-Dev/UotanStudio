using System.Globalization;
using System.Text;
using UotanStudio.Firmware.AndroidMeta.Dex;
using UotanStudio.Firmware.AndroidMeta.Dex.Models;
using UotanStudio.Firmware.AndroidMeta.Dex.Smali;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class DexSmaliLiteralTests
{
    internal static string HexFloatSource()
    {
        var random = new Random(90210);
        const string digits = "0123456789abcdef";
        var source = new StringBuilder(".class public LExample;\n.super Ljava/lang/Object;\n");
        var method = new StringBuilder(".method public static run()V\n.registers 2\n");
        for (var i = 0; i < 128; i++)
        {
            var single = i % 2 == 0;
            var count = random.Next(1, 90);
            var mantissa = new string(Enumerable.Range(0, count).Select(_ => digits[random.Next(digits.Length)]).ToArray());
            var literal = (random.Next(2) == 0 ? "-" : string.Empty) + "0x" + mantissa[0] + "." + mantissa[1..]
                + "p" + random.Next(single ? -200 : -1200, single ? 200 : 1200).ToString(CultureInfo.InvariantCulture)
                + (single ? "f" : string.Empty);
            source.Append(".field public static value").Append(i.ToString("D3", CultureInfo.InvariantCulture))
                .Append(single ? ":F = " : ":D = ").AppendLine(literal);
            method.Append(single ? "const v0, " : "const-wide v0, ").AppendLine(literal);
        }
        return source.Append(method).AppendLine("return-void\n.end method").ToString();
    }

    internal const string Source = """
        .class public LExample;
        .super Ljava/lang/Object;
        .method public static run()V
            .registers 4
            const/4 v0, 0xfft
            const/4 v0, '\uffff'
            const/16 v0, 0xffff
            add-int/lit8 v0, v0, 0xffff
            const v0, 037777777777
            const v0, 4294967295
            const v0, 'A'
            const v0, true
            const v0, -1.5f
            const v0, NaNf
            const v0, -Infinityf
            const v0, 0x1.000001p0f
            const v0, 0x1.000003p0f
            const v0, 0x1.000000000000000000000001p-150f
            const/high16 v0, 0x1.8p2f
            const-wide v0, 0xffffffff
            const-wide v0, 0xffffffffL
            const-wide v0, -0.0f
            const-wide v0, NaN
            const-wide v0, 0x1.00000000000008p0
            const-wide v0, 0x1.0000000000000800000000000001p0
            const-wide v0, 0x1p-1074
            const-wide/high16 v0, 0x4020000000000000L
            return-void
        .end method
        """;

    internal const string PayloadSource = """
        .class public LExample;
        .super Ljava/lang/Object;
        .method public static run([J)V
            .registers 1
            fill-array-data p0, :data
            return-void
            :data
            .array-data 8
                0xffffffff
                0xffffffffL
                -0.0f
                0x1.0p-1074
            .end array-data
        .end method
        """;

    [Fact]
    public void Assemble_WidePayloadLiterals_SignExtendsAccordingToLiteralType()
    {
        var bytes = SmaliAssembler.Assemble(PayloadSource).ToArray();
        var dex = DexFile.Load(bytes);
        var code = dex.Classes[0].DirectMethods[0].Code!;
        var instructions = InstructionDecoder.DecodeForSmali(code.RawInsns, dex);
        var payload = Assert.IsType<PayloadOperand>(instructions[^1].Operands[0]);
        Assert.Equal(new[] { 0xffff, 0xffff, 0xffff, 0xffff, 0xffff, 0xffff, 0, 0, 0, 0x8000, 0xffff, 0xffff, 1, 0, 0, 0 }, payload.Values);
        var text = Disassembler.DisassembleAll(dex);
        Assert.Equal(bytes, SmaliAssembler.Assemble(text).ToArray());
    }

    [Theory]
    [InlineData("const/4", "0xfft", -1)]
    [InlineData("const/4", "'\\uffff'", -1)]
    [InlineData("const/16", "0xffff", -1)]
    [InlineData("const", "037777777777", -1)]
    [InlineData("const", "0xffffffffL", -1)]
    [InlineData("const", "true", 1)]
    [InlineData("const-wide", "0xffffffff", -1)]
    [InlineData("const-wide", "0xffffffffL", 4294967295L)]
    [InlineData("const-wide", "-0.0f", -2147483648L)]
    [InlineData("const", "NaNf", 0x7fc00000L)]
    [InlineData("const-wide", "NaN", 0x7ff8000000000000L)]
    [InlineData("const", "0x1.000001p0f", 0x3f800000L)]
    [InlineData("const", "0x1.000003p0f", 0x3f800002L)]
    [InlineData("const", "0x1.00000100000000000001p0f", 0x3f800001L)]
    [InlineData("const", "0x1p-149f", 1)]
    [InlineData("const", "0x1p-150f", 0)]
    [InlineData("const", "0x1.000000000000000000000001p-150f", 1)]
    [InlineData("const", "0x1.fffffep-127f", 0x00800000L)]
    [InlineData("const", "0x1.ffffffp127f", 0x7f800000L)]
    [InlineData("const", "-0x0p999999999999999999999f", -2147483648L)]
    [InlineData("const-wide", "0x1.00000000000008p0", 0x3ff0000000000000L)]
    [InlineData("const-wide", "0x1.0000000000000800000000000001p0", 0x3ff0000000000001L)]
    [InlineData("const-wide", "0x1p-1074", 1)]
    [InlineData("const-wide", "0x1p-1075", 0)]
    [InlineData("const-wide", "0x1.000000000000000000001p-1075", 1)]
    [InlineData("const-wide", "0x1.fffffffffffffp1023", 0x7fefffffffffffffL)]
    [InlineData("const-wide", "0x1.fffffffffffff8p1023", 0x7ff0000000000000L)]
    [InlineData("const-wide", "-0x1p-999999999999999999999", long.MinValue)]
    [InlineData("const-wide/high16", "0x1.0p3", 0x4020000000000000L)]
    public void Assemble_NumericLiteral_PreservesTypedBits(string opcode, string literal, long expected)
    {
        var dex = SmaliAssembler.Assemble(
            ".class public LExample;\n.method public static run()V\n.registers 2\n"
            + opcode + " v0, " + literal + "\nreturn-void\n.end method");
        var code = dex.Classes[0].DirectMethods[0].Code!;
        var instruction = InstructionDecoder.DecodeForSmali(code.RawInsns, dex)[0];
        Assert.Equal(expected, Assert.IsType<LiteralOperand>(instruction.Operands[1]).Value);
        Assert.Equal(dex.ToArray(), SmaliAssembler.Assemble(Disassembler.DisassembleAll(dex)).ToArray());
    }

    [Theory]
    [InlineData("0x")]
    [InlineData("08")]
    [InlineData("256t")]
    [InlineData("-129t")]
    [InlineData("65536s")]
    [InlineData("-32769s")]
    [InlineData("4294967296")]
    [InlineData("-2147483649")]
    [InlineData("18446744073709551616L")]
    [InlineData("0x1.0")]
    [InlineData("0x1p")]
    [InlineData("0x.p0")]
    [InlineData("0x1..0p0f")]
    [InlineData("0x1p1.0")]
    public void Assemble_InvalidScalar_RejectsInput(string literal)
    {
        Assert.Throws<FormatException>(() => SmaliAssembler.Assemble(".class public LExample;\n.field static value:I = " + literal));
    }

    [Theory]
    [InlineData("const/4 v0, true")]
    [InlineData("const/16 v0, 1.0f")]
    [InlineData("const v0, 1.0")]
    [InlineData("const/high16 v0, 1.1f")]
    [InlineData("const-wide/high16 v0, 1.1")]
    public void Assemble_IncompatibleLiteralForm_RejectsInput(string instruction)
    {
        Assert.Throws<FormatException>(() => SmaliAssembler.Assemble(
            ".class public LExample;\n.method public static run()V\n.registers 2\n" + instruction + "\nreturn-void\n.end method"));
    }

    [Fact]
    public void Disassemble_PositiveFloatingExponent_UsesSmaliCompatibleNotation()
    {
        var dex = SmaliAssembler.Assemble(".class public LExample;\n.field static a:F = 1e20f\n.field static b:D = 1e100");
        var text = Disassembler.DisassembleAll(dex);
        Assert.DoesNotContain("E+", text);
        Assert.Contains("1E20f", text);
        Assert.Contains("1E100", text);
        Assert.Equal(dex.ToArray(), SmaliAssembler.Assemble(text).ToArray());
    }
}