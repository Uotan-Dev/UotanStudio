using System.Buffers.Binary;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Instructions;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Models;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Smali;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class DexInstructionEncodingTests
{
    [Theory]
    [InlineData(0x23, "new-array", InstructionFormat.Format22c)]
    [InlineData(0x44, "aget", InstructionFormat.Format23x)]
    [InlineData(0x52, "iget", InstructionFormat.Format22c)]
    [InlineData(0x6e, "invoke-virtual", InstructionFormat.Format35c)]
    [InlineData(0x74, "invoke-virtual/range", InstructionFormat.Format3rc)]
    [InlineData(0x7b, "neg-int", InstructionFormat.Format12x)]
    [InlineData(0x90, "add-int", InstructionFormat.Format23x)]
    [InlineData(0xd0, "add-int/lit16", InstructionFormat.Format22s)]
    [InlineData(0xe2, "ushr-int/lit8", InstructionFormat.Format22b)]
    [InlineData(0xfa, "invoke-polymorphic", InstructionFormat.Format45cc)]
    [InlineData(0xfb, "invoke-polymorphic/range", InstructionFormat.Format4rcc)]
    [InlineData(0xfc, "invoke-custom", InstructionFormat.Format35c)]
    [InlineData(0xfd, "invoke-custom/range", InstructionFormat.Format3rc)]
    [InlineData(0xfe, "const-method-handle", InstructionFormat.Format21c)]
    [InlineData(0xff, "const-method-type", InstructionFormat.Format21c)]
    public void Get_StandardOpcode_UsesDexNumberAndFormat(int opcode, string name, InstructionFormat format)
    {
        var info = Assert.IsType<OpCodeInfo>(OpCodes.Get((byte)opcode));
        Assert.Equal(name, info.Mnemonic);
        Assert.Equal(format, info.Format);
        Assert.Equal((byte)opcode, OpCodes.Find(name)!.Value.Opcode);
    }

    [Theory]
    [InlineData(0x3e)]
    [InlineData(0x43)]
    [InlineData(0x73)]
    [InlineData(0x79)]
    [InlineData(0x7a)]
    [InlineData(0xe3)]
    [InlineData(0xf9)]
    public void Get_ReservedDexOpcode_DoesNotInterpretOptimizedInstruction(int opcode)
    {
        Assert.Null(OpCodes.Get((byte)opcode));
        Assert.Equal(1, OpCodes.GetSize((byte)opcode));
    }

    [Fact]
    public void Decode_LittleEndianDexBytes_UsesLowByteOpcodeAndHighByteOperands()
    {
        ReadOnlySpan<byte> bytes = [0x12, 0xf3, 0x0f, 0x03];
        ushort[] units =
        [
            BinaryPrimitives.ReadUInt16LittleEndian(bytes),
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..])
        ];

        var instructions = InstructionDecoder.Decode(units, new DexFile("035"));

        Assert.Equal("const/4", instructions[0].Mnemonic);
        Assert.Equal(3, instructions[0].Operands[0]);
        Assert.Equal(-1, Assert.IsType<LiteralOperand>(instructions[0].Operands[1]).Value);
        Assert.Equal("return", instructions[1].Mnemonic);
        Assert.Equal(3, instructions[1].Operands[0]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Decode_InvokeRegisterList_PreservesCThroughGOrder(int count)
    {
        ushort[] units = [(ushort)(count << 12 | 5 << 8 | 0x6e), 0, 0x4321];

        var instruction = Assert.Single(InstructionDecoder.Decode(units, new DexFile("035")));

        Assert.Equal("invoke-virtual", instruction.Mnemonic);
        Assert.Equal(Enumerable.Range(1, count), Assert.IsType<int[]>(instruction.Operands[0]));
    }

    [Fact]
    public void Decode_InvokeRegisterListExceedsFive_RejectsInput()
    {
        Assert.Throws<InvalidDataException>(() => InstructionDecoder.Decode([0x606e, 0, 0], new DexFile("035")));
    }

    [Theory]
    [InlineData(0x0019, 0xffff, -281474976710656L)]
    [InlineData(0x0019, 0x1234, 1311673391471656960L)]
    [InlineData(0x0015, 0xffff, -65536L)]
    public void Decode_High16Literal_ShiftsAccordingToInstructionWidth(int first, int second, long expected)
    {
        var instruction = Assert.Single(InstructionDecoder.Decode([(ushort)first, (ushort)second], new DexFile("035")));

        Assert.Equal(expected, Assert.IsType<LiteralOperand>(instruction.Operands[1]).Value);
    }

    [Theory]
    [InlineData(0x0100, 0xffff, 0, 0)]
    [InlineData(0x0200, 0xffff, 0, 0)]
    [InlineData(0x0300, 8, 0xffff, 0xffff)]
    [InlineData(0x0300, 3, 0, 0)]
    public void Decode_MalformedPayload_RejectsBeforeAllocatingDeclaredLength(int ident, int width, int low, int high)
    {
        Assert.Throws<InvalidDataException>(() =>
            InstructionDecoder.Decode([(ushort)ident, (ushort)width, (ushort)low, (ushort)high], new DexFile("035")));
    }

    [Fact]
    public void Assemble_BranchesAndParameterAlias_EmitsStandardCodeUnits()
    {
        var dex = SmaliAssembler.Assemble("""
            .class public Lexample/Main;
            .super Ljava/lang/Object;
            .method public static choose(I)I
                .locals 1
                if-eqz p0, :zero
                const/4 v0, 1
                goto :done
                :zero
                const/4 v0, 0
                :done
                return v0
            .end method
            """);

        var code = Assert.Single(Assert.Single(dex.Classes).DirectMethods).Code!;
        Assert.Equal<ushort>([0x0138, 4, 0x1012, 0x0228, 0x0012, 0x000f], code.RawInsns);
    }

    [Theory]
    [InlineData("goto :missing")]
    [InlineData(":same\n:same\nreturn-void")]
    [InlineData("const/4 v0, 8")]
    [InlineData("const/4 v16, 1")]
    [InlineData("return v32")]
    [InlineData("return p0")]
    [InlineData("return-void v0")]
    public void Assemble_InvalidInstruction_RejectsRatherThanTruncating(string body)
    {
        Assert.Throws<FormatException>(() => SmaliAssembler.Assemble($"""
            .class public Lexample/Main;
            .super Ljava/lang/Object;
            .method public static run()V
                .registers 32
                {body}
            .end method
            """));
    }

    [Fact]
    public void Assemble_StringFieldAndMethodReferences_PreservesPoolsAndOperands()
    {
        var dex = SmaliAssembler.Assemble("""
            .class public Lexample/Main;
            .super Ljava/lang/Object;
            .method public static run()V
                .registers 3
                const-string v0, "hello, \"DEX\"\u0021"
                new-instance v1, Ljava/lang/StringBuilder;
                invoke-direct {v1, v0}, Ljava/lang/StringBuilder;-><init>(Ljava/lang/String;)V
                sget v2, Lexample/Main;->count:I
                return-void
            .end method
            """);

        var loaded = DexFile.Load(dex.ToArray());
        var code = Assert.Single(Assert.Single(loaded.Classes).DirectMethods).Code!;
        var instructions = InstructionDecoder.Decode(code.RawInsns, loaded);
        Assert.Equal("hello, \"DEX\"!", instructions[0].Operands[1]);
        Assert.Equal("Ljava/lang/StringBuilder;", Assert.IsType<TypeRefOperand>(instructions[1].Operands[1]).Descriptor);
        Assert.Equal([1, 0], Assert.IsType<int[]>(instructions[2].Operands[0]));
        var target = Assert.IsType<MethodRefOperand>(instructions[2].Operands[1]);
        Assert.Equal("<init>", target.Name);
        Assert.Equal(["java.lang.String"], target.Parameters);
        Assert.Equal("count", Assert.IsType<FieldRefOperand>(instructions[3].Operands[1]).Name);
        Assert.Equal(2, code.OutsSize);
    }

    [Fact]
    public void Assemble_RangeInvocation_PreservesDisassemblyAndWordCount()
    {
        var dex = SmaliAssembler.Assemble("""
            .class public Lexample/Main;
            .super Ljava/lang/Object;
            .method public static run()V
                .registers 20
                invoke-static/range {v16 .. v18}, Lexample/Main;->call(III)V
                return-void
            .end method
            """);

        var code = Assert.Single(Assert.Single(dex.Classes).DirectMethods).Code!;
        var target = dex.Methods.FindIndex(method => dex.Strings[method.NameIndex] == "call");
        Assert.Equal<ushort>([0x0377, checked((ushort)target), 16, 0x000e], code.RawInsns);
        Assert.Equal(3, code.OutsSize);
        var text = Disassembler.DisassembleAll(dex);
        Assert.Contains("{v16 .. v18}", text);
        var rebuilt = SmaliAssembler.Assemble(text);
        Assert.Equal(code.RawInsns, Assert.Single(Assert.Single(rebuilt.Classes).DirectMethods).Code!.RawInsns);
    }

    [Fact]
    public void Assemble_PolymorphicInvocation_StoresBothReferencesAndDexVersion()
    {
        var dex = SmaliAssembler.Assemble("""
            .class public Lexample/Main;
            .super Ljava/lang/Object;
            .method public static run()V
                .registers 3
                invoke-polymorphic {v0, v1}, Ljava/lang/invoke/MethodHandle;->invokeExact([Ljava/lang/Object;)Ljava/lang/Object;, (I)Ljava/lang/Object;
                return-void
            .end method
            """);

        var loaded = DexFile.Load(dex.ToArray());
        Assert.Equal("038", loaded.Header.Version);
        var code = Assert.Single(Assert.Single(loaded.Classes).DirectMethods).Code!;
        var instruction = InstructionDecoder.Decode(code.RawInsns, loaded)[0];
        Assert.Equal([0, 1], Assert.IsType<int[]>(instruction.Operands[0]));
        var proto = Assert.IsType<ProtoRefOperand>(instruction.Operands[2]);
        Assert.Equal(["I"], proto.Parameters);
        Assert.Equal("Ljava/lang/Object;", proto.ReturnType);
        Assert.Contains(", (I)Ljava/lang/Object;", Disassembler.DisassembleAll(loaded));
    }

    [Fact]
    public void Assemble_MethodTypeConstant_StoresTypedReferenceAndDexVersion()
    {
        var dex = SmaliAssembler.Assemble("""
            .class public Lexample/Main;
            .super Ljava/lang/Object;
            .method public static run()V
                .registers 1
                const-method-type v0, (Ljava/lang/String;)I
                return-void
            .end method
            """);

        var loaded = DexFile.Load(dex.ToArray());
        Assert.Equal("039", loaded.Header.Version);
        var code = Assert.Single(Assert.Single(loaded.Classes).DirectMethods).Code!;
        Assert.Equal(0x00ff, code.RawInsns[0]);
        var prototype = Assert.IsType<ProtoRefOperand>(InstructionDecoder.Decode(code.RawInsns, loaded)[0].Operands[1]);
        Assert.Equal("I", prototype.ReturnType);
        Assert.Equal(["Ljava/lang/String;"], prototype.Parameters);
    }

    [Theory]
    [InlineData("invoke-static {v0, v1, v2, v3, v4, v5}, Lexample/Main;->call()V")]
    [InlineData("invoke-static {v0}, call()V")]
    [InlineData("invoke-static/range {v3 .. v1}, Lexample/Main;->call()V")]
    [InlineData("const-string v0, \"unterminated")]
    [InlineData("const-string v0, \"\\u123\"")]
    [InlineData("const-string v0, \"\\x20\"")]
    [InlineData("const-class v0, [V")]
    public void Assemble_MalformedReferenceOperands_RejectsInput(string instruction)
    {
        Assert.Throws<FormatException>(() => SmaliAssembler.Assemble($"""
            .class public Lexample/Main;
            .super Ljava/lang/Object;
            .method public static run()V
                .registers 20
                {instruction}
            .end method
            """));
    }
}