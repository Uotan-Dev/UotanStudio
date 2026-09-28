using UotanStudio.Firmware.AndroidMeta.Dex;
using UotanStudio.Firmware.AndroidMeta.Dex.Models;
using UotanStudio.Firmware.AndroidMeta.Dex.Smali;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// DEX disassembler tests: build a minimal DEX with known instructions and verify the smali-style
/// output contains the expected mnemonics, class header and method signature.
/// <para>DEX 反汇编测试：构造含已知指令的最小 DEX，验证 smali 风格输出包含预期助记符、类头与方法签名。</para>
/// </summary>
public class DexDisassemblerTests
{
    [Fact]
    public void DisassembleAll_ContainsClassMethodAndInstructions()
    {
        var dex = BuildMinimalDex();
        var text = Disassembler.DisassembleAll(dex);

        Assert.Contains(".class public Lcom/example/Hello;", text);
        Assert.Contains(".super Ljava/lang/Object;", text);
        Assert.Contains(".method public static main(", text);
        Assert.Contains("const/4", text);
        Assert.Contains("return-void", text);
        Assert.Contains(".end method", text);
    }

    [Fact]
    public void DisassembleAll_UsesSingleRegisterDeclaration()
    {
        var dex = BuildMinimalDex();
        var text = Disassembler.DisassembleAll(dex);
        Assert.Contains(".registers 1", text);
        Assert.DoesNotContain(".locals", text);
    }

    [Fact]
    public void DisassembleAll_ShowsBranchLabel()
    {
        var dex = new DexFile("035");
        dex.Strings.Add("Lcom/example/Branch;");
        dex.Strings.Add("Ljava/lang/Object;");
        dex.Strings.Add("run");
        dex.Strings.Add("V");
        dex.Strings.Add("V");
        dex.Types.Add("Lcom/example/Branch;");
        dex.Types.Add("Ljava/lang/Object;");
        dex.Types.Add("V");
        dex.Protos.Add(new ProtoDefinition { ShortyIndex = 3, ReturnTypeIndex = 2 });
        dex.Methods.Add(new MethodId { ClassIndex = 0, ProtoIndex = 0, NameIndex = 2 });

        var code = new CodeItem
        {
            RegistersSize = 0,
            InsSize = 0,
            OutsSize = 0,
            RawInsns = [0x0428, 0x0000, 0x000E]
        };
        var cls = new ClassDefinition
        {
            TypeIndex = 0,
            AccessFlags = AccessFlags.Public,
            SuperclassIndex = 1
        };
        cls.DirectMethods.Add(new EncodedMethod { MethodIndex = 0, AccessFlags = AccessFlags.Public, Code = code });
        dex.Classes.Add(cls);

        var text = Disassembler.DisassembleAll(dex);
        Assert.Contains("goto", text);
        Assert.Contains(":label_", text);
        Assert.Contains("return-void", text);
    }

    private static DexFile BuildMinimalDex()
    {
        var dex = new DexFile("035");
        dex.Strings.Add("Lcom/example/Hello;");
        dex.Strings.Add("Ljava/lang/Object;");
        dex.Strings.Add("main");
        dex.Strings.Add("V");
        dex.Strings.Add("([Ljava/lang/String;)V");
        dex.Strings.Add("Hello.java");
        dex.Strings.Add("[Ljava/lang/String;");
        dex.Types.Add("Lcom/example/Hello;");
        dex.Types.Add("Ljava/lang/Object;");
        dex.Types.Add("V");
        dex.Types.Add("[Ljava/lang/String;");
        dex.Protos.Add(new ProtoDefinition
        {
            ShortyIndex = 4,
            ReturnTypeIndex = 2,
            ParameterTypeIndices = [3]
        });
        dex.Methods.Add(new MethodId { ClassIndex = 0, ProtoIndex = 0, NameIndex = 2 });
        var code = new CodeItem
        {
            RegistersSize = 1,
            InsSize = 0,
            OutsSize = 0,
            RawInsns = [0x0012, 0x000E]
        };
        var cls = new ClassDefinition
        {
            TypeIndex = 0,
            AccessFlags = AccessFlags.Public,
            SuperclassIndex = 1,
            SourceFileIndex = 5
        };
        cls.DirectMethods.Add(new EncodedMethod
        {
            MethodIndex = 0,
            AccessFlags = AccessFlags.Public | AccessFlags.Static,
            Code = code
        });
        dex.Classes.Add(cls);
        return dex;
    }
}