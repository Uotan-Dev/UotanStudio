using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Models;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Smali;
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

        Assert.Contains(".class public com.example.Hello", text);
        Assert.Contains(".super java.lang.Object", text);
        Assert.Contains(".method public static main(", text);
        Assert.Contains("const/4", text);
        Assert.Contains("return-void", text);
        Assert.Contains(".end method", text);
    }

    [Fact]
    public void DisassembleAll_ShowsRegistersAndLocals()
    {
        var dex = BuildMinimalDex();
        var text = Disassembler.DisassembleAll(dex);
        Assert.Contains(".registers 1", text);
        Assert.Contains(".locals 1", text);
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
        dex.Types.Add("Lcom/example/Branch;"); // 0
        dex.Types.Add("Ljava/lang/Object;");   // 1
        dex.Types.Add("V");                    // 2
        dex.Protos.Add(new ProtoDefinition { ShortyIndex = 3, ReturnTypeIndex = 2 });
        dex.Methods.Add(new MethodId { ClassIndex = 0, ProtoIndex = 0, NameIndex = 2 });

        // goto +4（跳过一条指令）; return-void
        // goto +4 (skip one instruction); return-void.
        var code = new CodeItem
        {
            RegistersSize = 0,
            InsSize = 0,
            OutsSize = 0,
            RawInsns = [0x2804, 0x0000, 0x0E00] // goto +4 → 0x2804; nop; return-void
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
        dex.Strings.Add("Lcom/example/Hello;");   // 0
        dex.Strings.Add("Ljava/lang/Object;");    // 1
        dex.Strings.Add("main");                  // 2
        dex.Strings.Add("V");                     // 3
        dex.Strings.Add("([Ljava/lang/String;)V"); // 4
        dex.Strings.Add("Hello.java");            // 5
        dex.Strings.Add("[Ljava/lang/String;");   // 6
        dex.Types.Add("Lcom/example/Hello;");     // 0
        dex.Types.Add("Ljava/lang/Object;");      // 1
        dex.Types.Add("V");                       // 2
        dex.Types.Add("[Ljava/lang/String;");     // 3
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
            RawInsns = [0x1200, 0x0E00]
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