using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Instructions;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Models;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Smali;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class DexSmaliPayloadTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Disassemble_UnreferencedSwitch_PreservesSignedOffsetsAndBinary(bool sparse)
    {
        var dex = Assemble("return v0");
        Code(dex).RawInsns = sparse
            ? [0xf, 0, 0x200, 3, 0, 0x8000, 0, 0, 0xffff, 0x7fff, 0, 0x8000, 0xffff, 0xffff, 0xffff, 0x7fff]
            : [0xf, 0, 0x100, 3, 0xffff, 0xffff, 0, 0x8000, 0xffff, 0xffff, 0xffff, 0x7fff];
        var original = dex.ToArray();
        var text = Disassembler.DisassembleAll(DexFile.Load(original));

        Assert.Contains(sparse ? ".sparse-switch-raw" : ".packed-switch-raw -1", text);
        Assert.Contains("-2147483648", text);
        Assert.Contains("2147483647", text);
        Assert.Equal(original, SmaliAssembler.Assemble(text).ToArray());
    }

    [Theory]
    [InlineData(".packed-switch-raw 0\n.end packed-switch-raw")]
    [InlineData(".sparse-switch-raw\n.end sparse-switch-raw")]
    public void Assemble_UnreferencedEmptySwitch_PreservesTable(string table)
    {
        var dex = Assemble("return v0\n" + table);
        Assert.Equal(dex.ToArray(), SmaliAssembler.Assemble(Disassembler.DisassembleAll(dex)).ToArray());
    }

    [Theory]
    [InlineData("packed-switch", ".packed-switch-raw 0\n3\n.end packed-switch-raw")]
    [InlineData("sparse-switch", ".sparse-switch-raw\n1 -> 3\n.end sparse-switch-raw")]
    public void Assemble_ReferencedRawSwitchAtZero_ValidatesAndRendersLabels(string instruction, string table)
    {
        var dex = Assemble($"{instruction} v0, :data\nreturn v0\n:data\n{table}");
        var text = Disassembler.DisassembleAll(dex);
        Assert.DoesNotContain("-raw", text);
        Assert.Contains(":label_0006", text);
        Assert.Equal(dex.ToArray(), SmaliAssembler.Assemble(text).ToArray());
    }

    [Fact]
    public void Assemble_ReferencedRawSwitchAtNonzeroAddress_PreservesNegativeDestination()
    {
        var dex = Assemble("""
            :case
            return v0
            packed-switch v0, :data
            return v0
            :data
            .packed-switch-raw 0
                -1
            .end packed-switch-raw
            """);
        Assert.Equal(new[] { 0 }, SwitchDestinations(dex));
        var text = Disassembler.DisassembleAll(dex);
        Assert.DoesNotContain("-raw", text);
        Assert.Equal(dex.ToArray(), SmaliAssembler.Assemble(text).ToArray());
    }

    [Fact]
    public void Assemble_UnsortedRawSparseSwitch_KeepsKeysPairedWithOffsets()
    {
        var dex = Assemble("""
            return v0
            .sparse-switch-raw
                2 -> -7
                -1 -> 17
            .end sparse-switch-raw
            """);
        var payload = InstructionDecoder.Decode(Code(dex).RawInsns, dex)[^1];
        Assert.Equal(new[] { -1, 2 }, Assert.IsType<PayloadOperand>(payload.Operands[0]).Values);
        Assert.Equal(new[] { 17, -7 }, Assert.IsType<PayloadOperand>(payload.Operands[1]).Values);
        Assert.Equal(dex.ToArray(), SmaliAssembler.Assemble(Disassembler.DisassembleAll(dex)).ToArray());
    }

    [Theory]
    [InlineData("return v0\n.packed-switch-raw 0\n2147483648L\n.end packed-switch-raw")]
    [InlineData("return v0\n.packed-switch-raw 0\n-2147483649L\n.end packed-switch-raw")]
    [InlineData("return v0\n.packed-switch-raw 0\n2147483648\n.end packed-switch-raw")]
    [InlineData("return v0\n.packed-switch-raw 0\n-2147483649\n.end packed-switch-raw")]
    [InlineData("return v0\n.packed-switch-raw 0\n1.0f\n.end packed-switch-raw")]
    [InlineData("return v0\n.packed-switch-raw 0\ntrue\n.end packed-switch-raw")]
    [InlineData("return v0\n.packed-switch-raw 0\n:label\n.end packed-switch-raw")]
    [InlineData("return v0\n.packed-switch-raw 0\n0\n.end packed-switch")]
    [InlineData("return v0\n.packed-switch-raw 2147483647\n0\n0\n.end packed-switch-raw")]
    [InlineData("return v0\n.sparse-switch-raw\n1 -> 0\n1 -> 1\n.end sparse-switch-raw")]
    [InlineData("return v0\n.sparse-switch-raw\n1\n.end sparse-switch-raw")]
    [InlineData("return v0\n.packed-switch 0\n:case\n.end packed-switch")]
    [InlineData("packed-switch v0, :data\nreturn v0\n:data\n.packed-switch-raw 0\n1\n.end packed-switch-raw")]
    [InlineData("packed-switch v0, :data\nreturn v0\n:data\n.packed-switch-raw 0\n4\n.end packed-switch-raw")]
    [InlineData("nop\npacked-switch v0, :data\nreturn v0\n:data\n.packed-switch-raw 0\n2147483647\n.end packed-switch-raw")]
    public void Assemble_InvalidRawSwitch_RejectsInput(string body)
    {
        Assert.Throws<FormatException>(() => Assemble(body));
    }

    [Fact]
    public void Assemble_PackedSwitchAtNonzeroAddress_EncodesCasesRelativeToSwitch()
    {
        var dex = Assemble("""
            nop
            packed-switch v0, :data
            const/4 v0, 0
            return v0
            :first
            const/4 v0, 1
            return v0
            :second
            const/4 v0, -1
            return v0
            :data
            .packed-switch -2
                :first
                :second
            .end packed-switch
            """);
        Assert.Equal<ushort>(
            [0, 0x002b, 9, 0, 0x0012, 0x000f, 0x1012, 0x000f, 0xf012, 0x000f, 0x0100, 2, 0xfffe, 0xffff, 5, 0, 7, 0],
            Code(dex).RawInsns);
        var text = Disassembler.DisassembleAll(dex);
        Assert.Contains(".packed-switch -2", text);
        Assert.Contains(":label_000C", text);
        Assert.Contains(":label_0010", text);
        Assert.Equal(dex.ToArray(), SmaliAssembler.Assemble(text).ToArray());
    }

    [Fact]
    public void Assemble_BackwardSparsePayload_SortsKeysAndEncodesSignedReference()
    {
        var dex = Assemble("""
            goto :body
            :data
            .sparse-switch
                30 -> :high
                -20 -> :low
            .end sparse-switch
            :body
            sparse-switch v0, :data
            return v0
            :low
            return v0
            :high
            return v0
            """);
        var raw = Code(dex).RawInsns;
        Assert.Equal(0, raw[1]);
        Assert.Equal(0x0200, raw[2]);
        Assert.Equal<ushort>([0xffec, 0xffff, 30, 0, 4, 0, 5, 0], raw[4..12]);
        Assert.Equal<ushort>([0x002c, 0xfff6, 0xffff], raw[12..15]);
        Assert.Equal(dex.ToArray(), SmaliAssembler.Assemble(Disassembler.DisassembleAll(dex)).ToArray());
    }

    [Theory]
    [InlineData(1, "0xfft\n1\n0x7f", new ushort[] { 0x01ff, 0x007f })]
    [InlineData(2, "-1s\n'A'\n32768", new ushort[] { 0xffff, 65, 0x8000 })]
    [InlineData(4, "0xffffffff\n1.0f\n-2147483648", new ushort[] { 0xffff, 0xffff, 0, 0x3f80, 0, 0x8000 })]
    [InlineData(8, "0xffffffffffffffffL\n-0.0\n-9223372036854775808L", new ushort[] { 0xffff, 0xffff, 0xffff, 0xffff, 0, 0, 0, 0x8000, 0, 0, 0, 0x8000 })]
    public void Assemble_ArrayPayload_PreservesElementWidthCountAndBitPatterns(int width, string values, ushort[] expected)
    {
        var dex = Assemble($$"""
            nop
            fill-array-data v0, :alias
            return v0
            :data
            :alias
            .array-data {{width}}
                {{values}}
            .end array-data
            """);
        var raw = Code(dex).RawInsns;
        Assert.Equal(0, raw[5]);
        Assert.Equal(0x0300, raw[6]);
        Assert.Equal(width, raw[7]);
        Assert.Equal(3, raw[8]);
        Assert.Equal(expected, raw[10..]);
        var instructions = InstructionDecoder.Decode(raw, dex);
        var payload = Assert.IsType<PayloadOperand>(instructions[^1].Operands[0]);
        Assert.Equal(3u, payload.ElementCount);
        var loaded = DexFile.Load(dex.ToArray());
        var text = Disassembler.DisassembleAll(loaded);
        Assert.Contains($".array-data {width}", text);
        Assert.DoesNotContain("units=", text);
        Assert.Equal(dex.ToArray(), SmaliAssembler.Assemble(text).ToArray());
    }

    [Theory]
    [InlineData("packed-switch", ".packed-switch 0", ".end packed-switch")]
    [InlineData("sparse-switch", ".sparse-switch", ".end sparse-switch")]
    [InlineData("fill-array-data", ".array-data 1", ".end array-data")]
    public void Assemble_EmptyPayload_PreservesEmptyTable(string instruction, string start, string end)
    {
        var dex = Assemble($"{instruction} v0, :data\nreturn v0\n:data\n{start}\n{end}");
        Assert.Equal(dex.ToArray(), SmaliAssembler.Assemble(Disassembler.DisassembleAll(dex)).ToArray());
    }

    [Theory]
    [InlineData("fill-array-data v0, :data\nreturn v0\n:data\n.array-data 3\n.end array-data")]
    [InlineData("fill-array-data v0, :data\nreturn v0\n:data\n.array-data 1\n256\n.end array-data")]
    [InlineData("fill-array-data v0, :data\nreturn v0\n:data\n.array-data 1\n-129\n.end array-data")]
    [InlineData("fill-array-data v0, :data\nreturn v0\n:data\n.array-data 4\n1")]
    [InlineData("packed-switch v0, :data\nreturn v0\n:data\n.sparse-switch\n.end sparse-switch")]
    [InlineData("packed-switch v0, :data\nreturn v0\n:data\n.packed-switch 0\n:missing\n.end packed-switch")]
    [InlineData("packed-switch v0, :data\nreturn v0\n:data\n.packed-switch 0\n:data\n.end packed-switch")]
    [InlineData("packed-switch v0, :data\n:case\nreturn v0\n:data\n.packed-switch 2147483647\n:case\n:case\n.end packed-switch")]
    [InlineData("sparse-switch v0, :data\n:case\nreturn v0\n:data\n.sparse-switch\n1 -> :case\n1 -> :case\n.end sparse-switch")]
    [InlineData("goto :data\nreturn v0\n:data\n.array-data 1\n1\n.end array-data")]
    [InlineData("goto :terminal\nreturn v0\n:terminal")]
    [InlineData("goto 2\nconst v0, 1\nreturn v0")]
    public void Assemble_InvalidPayload_RejectsMalformedLayout(string body)
    {
        Assert.Throws<FormatException>(() => Assemble(body));
    }

    [Fact]
    public void Assemble_SharedSwitchTable_ClonesRelativeOffsetsForEachReferrer()
    {
        var dex = Assemble("""
            packed-switch v0, :data
            packed-switch v0, :data
            return v0
            :case
            return v0
            :data
            .packed-switch 0
                :case
            .end packed-switch
            """);
        var instructions = InstructionDecoder.Decode(Code(dex).RawInsns, dex);
        var payloads = instructions.Where(instruction => instruction.Mnemonic == "packed-switch-payload").ToArray();
        Assert.Equal(2, payloads.Length);
        Assert.Equal(7, Assert.IsType<PayloadOperand>(payloads[0].Operands[0]).Values[0]);
        Assert.Equal(4, Assert.IsType<PayloadOperand>(payloads[1].Operands[0]).Values[0]);
        Assert.Equal(new[] { 7, 7 }, SwitchDestinations(dex));
        Assert.Equal(dex.ToArray(), SmaliAssembler.Assemble(Disassembler.DisassembleAll(dex)).ToArray());
    }

    [Fact]
    public void Disassemble_SharedRawPayload_NormalizesWithoutChangingCaseDestinations()
    {
        var dex = Assemble("return v0");
        Code(dex).RawInsns = [0x002b, 8, 0, 0x002b, 5, 0, 0x000f, 0x000f, 0x0100, 1, 0, 0, 3, 0];
        var text = Disassembler.DisassembleAll(dex);
        Assert.Contains(":payload_for_0006", text);
        var normalized = SmaliAssembler.Assemble(text);
        Assert.Equal(new[] { 3, 6 }, SwitchDestinations(dex));
        Assert.Equal(SwitchDestinations(dex), SwitchDestinations(normalized));
        Assert.True(Code(normalized).RawInsns.Length > Code(dex).RawInsns.Length);
    }

    private static int[] SwitchDestinations(DexFile dex)
    {
        var instructions = InstructionDecoder.Decode(Code(dex).RawInsns, dex);
        return instructions.Where(instruction => instruction.OpCode == 0x2b).Select(instruction =>
        {
            var target = Assert.IsType<BranchTargetOperand>(instruction.Operands[1]).Offset;
            var payload = Assert.IsType<PayloadOperand>(instructions.Single(candidate => candidate.Offset == target).Operands[0]);
            return instruction.Offset / 2 + payload.Values[0];
        }).ToArray();
    }

    [Theory]
    [InlineData(1, 300, 0x29)]
    [InlineData(8, 9000, 0x2a)]
    public void Assemble_GotoAcrossLargePayload_WidensWithoutBreakingAlignment(int width, int count, int opcode)
    {
        var body = "goto :body\n:data\n.array-data " + width + "\n"
            + string.Concat(Enumerable.Repeat("1\n", count))
            + ".end array-data\n:body\nreturn v0";
        var dex = Assemble(body);
        var raw = Code(dex).RawInsns;
        Assert.Equal(opcode, raw[0] & 255);
        var instructions = InstructionDecoder.Decode(raw, dex);
        var target = Assert.IsType<BranchTargetOperand>(instructions[0].Operands[0]);
        Assert.Equal(instructions[^1].Offset, target.Offset);
        var payload = instructions.Single(instruction => instruction.Mnemonic == "fill-array-data-payload");
        Assert.Equal(0, payload.Offset % 4);
        Assert.Equal(dex.ToArray(), SmaliAssembler.Assemble(Disassembler.DisassembleAll(dex)).ToArray());
    }

    [Fact]
    public void Decode_PackedKeysOverflow_RejectsMalformedTable()
    {
        Assert.Throws<InvalidDataException>(() => InstructionDecoder.Decode(
            [0x0100, 2, 0xffff, 0x7fff, 0, 0, 0, 0], new DexFile("035")));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    public void Decode_NonIncreasingSparseKeys_RejectsMalformedTable(int first, int second)
    {
        Assert.Throws<InvalidDataException>(() => InstructionDecoder.Decode(
            [0x0200, 2, (ushort)first, 0, (ushort)second, 0, 0, 0, 0, 0], new DexFile("035")));
    }

    private static DexFile Assemble(string body) => SmaliAssembler.Assemble(
        ".class public LExample;\n.method public static run()I\n.registers 1\n" + body + "\n.end method");

    private static CodeItem Code(DexFile dex) => Assert.Single(Assert.Single(dex.Classes).DirectMethods).Code!;
}