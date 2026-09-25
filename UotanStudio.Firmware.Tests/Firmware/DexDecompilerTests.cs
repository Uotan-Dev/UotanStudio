using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Decompile;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Models;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// 轻量化反编译引擎测试：程序化构造 DEX，断言伪 Java 输出中的表达式还原、
/// 结构化控制流（if-else/while/switch）、重载解析与字节码指导模式。
/// <para>Lightweight decompiler engine tests: programmatically built DEX files asserting the
/// pseudo-Java output for expression recovery, structured control flow (if-else/while/switch),
/// overload resolution and the bytecode-guide mode.</para>
/// </summary>
public class DexDecompilerTests
{
    [Fact]
    public void ConstAndReturn_ProducesLiteralReturn()
    {
        var dex = BuildSimpleDex([0x1250, 0x0F00]);
        var output = LightweightDecompiler.DecompileAll(dex);
        Assert.Contains("return 5;", output);
    }

    [Fact]
    public void FieldRead_OnThis_RendersFieldAccess()
    {
        var dex = BuildFieldDex([0x4C10, 0x0000, 0x0F00]);
        var output = LightweightDecompiler.DecompileAll(dex);
        Assert.Contains("return this.count;", output);
    }

    [Fact]
    public void IfElse_WithMerge_IsStructured()
    {
        var dex = BuildFieldDex(
        [
            0x3800, 0x0006,
            0x1221, 0x5321, 0x0000,
            0x2804,
            0x1231, 0x5321, 0x0000,
            0x0E00
        ], registersSize: 3);
        var output = LightweightDecompiler.DecompileAll(dex);
        Assert.Contains("if (v0 == 0)", output);
        Assert.Contains("this.count = 2;", output);
        Assert.Contains("this.count = 3;", output);
        Assert.Contains("else {", output);
    }

    [Fact]
    public void WhileLoop_IsStructured()
    {
        var dex = BuildSimpleDex([0x3800, 0x0005, 0xCF01, 0x0101, 0x28FC, 0x0E00]);
        var output = LightweightDecompiler.DecompileAll(dex);
        Assert.Contains("while (v0 != 0)", output);
        Assert.Contains("return;", output);
    }

    [Fact]
    public void PackedSwitch_IsStructured()
    {
        var dex = BuildSimpleDex(
        [
            0x2B00, 0x000A, 0x0000,
            0x121F, 0x2805,
            0x1211, 0x2803,
            0x1212, 0x2801,
            0x0F01,
            0x0100, 0x0002, 0x0000, 0x0000, 0xFFFB, 0xFFFF, 0xFFFD, 0xFFFF
        ]);
        var output = LightweightDecompiler.DecompileAll(dex);
        Assert.Contains("switch (v0)", output);
        Assert.Contains("case 0: {", output);
        Assert.Contains("case 1: {", output);
    }

    [Fact]
    public void OverloadResolution_SelectsNonBridgeAndCasts()
    {
        var dex = BuildOverloadDex();
        var output = LightweightDecompiler.DecompileAll(dex);
        Assert.Contains("foo((java.lang.String) \"hi\"", output);
    }

    [Fact]
    public void ConstructorCall_FoldsNewInstanceAndInit()
    {
        var dex = BuildConstructorDex();
        var output = LightweightDecompiler.DecompileAll(dex);
        Assert.Contains("new java.lang.StringBuilder(\"hi\"", output);
    }

    [Fact]
    public void BytecodeGuideMode_AddsOffsetsAndRawComments()
    {
        var dex = BuildSimpleDex([0x1250, 0x0F00]);
        var output = LightweightDecompiler.DecompileAll(dex, new DecompileOptions { Mode = DecompileOutputMode.BytecodeGuide });
        Assert.Contains("// 0x0002", output);
        Assert.Contains("const/4 v0, #5", output);
        Assert.Contains("return 5;", output);
    }

    /// <summary>最小类 Lcom/example/Main;，含一个 run()V 方法（实例方法，this=p0）。</summary>
    private static DexFile BuildSimpleDex(ushort[] rawInsns)
    {
        var dex = new DexFile("035");
        dex.Strings.AddRange([
            "Lcom/example/Main;",
            "Ljava/lang/Object;",
            "run",
            "()V",
            "V"
        ]);
        dex.Types.AddRange([
            "Lcom/example/Main;",
            "Ljava/lang/Object;",
            "V"
        ]);
        dex.Protos.Add(new ProtoDefinition { ShortyIndex = 3, ReturnTypeIndex = 2 });
        dex.Methods.Add(new MethodId { ClassIndex = 0, ProtoIndex = 0, NameIndex = 2 });

        var cls = new ClassDefinition
        {
            TypeIndex = 0,
            AccessFlags = AccessFlags.Public,
            SuperclassIndex = 1
        };
        cls.DirectMethods.Add(new EncodedMethod
        {
            MethodIndex = 0,
            AccessFlags = AccessFlags.Public,
            Code = new CodeItem { RegistersSize = 1, InsSize = 0, OutsSize = 0, RawInsns = rawInsns }
        });
        dex.Classes.Add(cls);
        return dex;
    }

    /// <summary>带实例字段 count:I 与 setField()V 方法的类（this=p0，v1 等局部）。</summary>
    private static DexFile BuildFieldDex(ushort[] rawInsns, ushort registersSize = 2)
    {
        var dex = new DexFile("035");
        dex.Strings.AddRange([
            "Lcom/example/Main;",
            "Ljava/lang/Object;",
            "count",
            "setField",
            "()V",
            "I",
            "V"
        ]);
        dex.Types.AddRange([
            "Lcom/example/Main;",
            "Ljava/lang/Object;",
            "I",
            "V"
        ]);
        dex.Protos.Add(new ProtoDefinition { ShortyIndex = 4, ReturnTypeIndex = 3 });
        dex.Fields.Add(new FieldId { ClassIndex = 0, TypeIndex = 2, NameIndex = 2 });
        dex.Methods.Add(new MethodId { ClassIndex = 0, ProtoIndex = 0, NameIndex = 3 });

        var cls = new ClassDefinition
        {
            TypeIndex = 0,
            AccessFlags = AccessFlags.Public,
            SuperclassIndex = 1
        };
        cls.InstanceFields.Add(new EncodedField { FieldIndex = 0, AccessFlags = AccessFlags.Public });
        cls.DirectMethods.Add(new EncodedMethod
        {
            MethodIndex = 0,
            AccessFlags = AccessFlags.Public,
            Code = new CodeItem { RegistersSize = registersSize, InsSize = 1, OutsSize = 0, RawInsns = rawInsns }
        });
        dex.Classes.Add(cls);
        return dex;
    }

    /// <summary>带桥接重载的类：foo(Object)（bridge+synthetic）与 foo(String)，call() 引用 foo(Object)。</summary>
    private static DexFile BuildOverloadDex()
    {
        var dex = new DexFile("035");
        dex.Strings.AddRange([
            "Lcom/example/Main;",
            "Ljava/lang/Object;",
            "Ljava/lang/String;",
            "foo",
            "call",
            "hi",
            "()V",
            "(Ljava/lang/Object;)V",
            "(Ljava/lang/String;)V",
            "V"
        ]);
        dex.Types.AddRange([
            "Lcom/example/Main;",
            "Ljava/lang/Object;",
            "Ljava/lang/String;",
            "V"
        ]);
        dex.Protos.AddRange([
            new ProtoDefinition { ShortyIndex = 7, ReturnTypeIndex = 3, ParameterTypeIndices = [1] },
            new ProtoDefinition { ShortyIndex = 8, ReturnTypeIndex = 3, ParameterTypeIndices = [2] },
            new ProtoDefinition { ShortyIndex = 6, ReturnTypeIndex = 3 }
        ]);
        dex.Methods.AddRange([
            new MethodId { ClassIndex = 0, ProtoIndex = 0, NameIndex = 3 },
            new MethodId { ClassIndex = 0, ProtoIndex = 1, NameIndex = 3 },
            new MethodId { ClassIndex = 0, ProtoIndex = 2, NameIndex = 4 }
        ]);

        var cls = new ClassDefinition
        {
            TypeIndex = 0,
            AccessFlags = AccessFlags.Public,
            SuperclassIndex = 1
        };
        cls.DirectMethods.Add(new EncodedMethod
        {
            MethodIndex = 0,
            AccessFlags = AccessFlags.Public | AccessFlags.Bridge | AccessFlags.Synthetic,
            Code = null
        });
        cls.DirectMethods.Add(new EncodedMethod
        {
            MethodIndex = 1,
            AccessFlags = AccessFlags.Public,
            Code = null
        });
        cls.DirectMethods.Add(new EncodedMethod
        {
            MethodIndex = 2,
            AccessFlags = AccessFlags.Public,
            Code = new CodeItem
            {
                RegistersSize = 3,
                InsSize = 1,
                OutsSize = 0,
                RawInsns = [0x1A01, 0x0005, 0x6801, 0x0000, 0x1111, 0x0E00]
            }
        });
        dex.Classes.Add(cls);
        return dex;
    }

    /// <summary>new-instance + invoke-direct &lt;init&gt; 折叠：build()Ljava/lang/StringBuilder;。</summary>
    private static DexFile BuildConstructorDex()
    {
        var dex = new DexFile("035");
        dex.Strings.AddRange([
            "Lcom/example/Main;",
            "Ljava/lang/Object;",
            "Ljava/lang/StringBuilder;",
            "Ljava/lang/String;",
            "build",
            "hi",
            "<init>",
            "(Ljava/lang/String;)V",
            "()Ljava/lang/StringBuilder;",
            "V"
        ]);
        dex.Types.AddRange([
            "Lcom/example/Main;",
            "Ljava/lang/Object;",
            "Ljava/lang/StringBuilder;",
            "Ljava/lang/String;",
            "V"
        ]);
        dex.Protos.AddRange([
            new ProtoDefinition { ShortyIndex = 7, ReturnTypeIndex = 4 },
            new ProtoDefinition { ShortyIndex = 8, ReturnTypeIndex = 2 }
        ]);
        dex.Methods.AddRange([
            new MethodId { ClassIndex = 2, ProtoIndex = 0, NameIndex = 6 },
            new MethodId { ClassIndex = 0, ProtoIndex = 1, NameIndex = 4 }
        ]);

        var cls = new ClassDefinition
        {
            TypeIndex = 0,
            AccessFlags = AccessFlags.Public,
            SuperclassIndex = 1
        };
        cls.DirectMethods.Add(new EncodedMethod
        {
            MethodIndex = 1,
            AccessFlags = AccessFlags.Public,
            Code = new CodeItem
            {
                RegistersSize = 3,
                InsSize = 1,
                OutsSize = 0,
                RawInsns = [0x2200, 0x0002, 0x1A01, 0x0005, 0x6A01, 0x0000, 0x1111, 0x1100]
            }
        });
        dex.Classes.Add(cls);
        return dex;
    }
}