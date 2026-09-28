using UotanStudio.Firmware.AndroidMeta.Dex;
using UotanStudio.Firmware.AndroidMeta.Dex.Models;
using UotanStudio.Firmware.AndroidMeta.Dex.Smali;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// DEX round-trip tests: programmatically build a minimal DEX model (one class with a static method
/// containing a few instructions), serialize it, parse it back and verify the model is preserved;
/// then re-serialize and assert byte stability.
/// <para>DEX 往返测试：程序化构造最小 DEX 模型（一个含静态方法的类，方法内几条指令），序列化后重新解析，
/// 验证模型保持一致；再次序列化断言字节稳定。</para>
/// </summary>
public class DexRoundTripTests
{
    [Fact]
    public void WriteThenLoad_PreservesClassMethodAndInstructions()
    {
        var dex = BuildMinimalDex();
        var bytes = dex.ToArray();

        Assert.Equal((byte)'d', bytes[0]);
        Assert.Equal((byte)'e', bytes[1]);
        Assert.Equal((byte)'x', bytes[2]);
        Assert.Equal((byte)'\n', bytes[3]);
        Assert.Equal("035", Encoding_Ascii(bytes, 4, 3));

        var loaded = DexFile.Load(bytes);
        Assert.Single(loaded.Classes);

        var cls = loaded.Classes[0];
        Assert.Equal("Lcom/example/Hello;", loaded.Types[cls.TypeIndex]);
        Assert.Equal("Ljava/lang/Object;", loaded.Types[cls.SuperclassIndex]);
        Assert.Contains(cls.DirectMethods, m => loaded.GetString(loaded.GetMethod(m.MethodIndex)!.NameIndex) == "main");

        var method = cls.DirectMethods.First(m => loaded.GetString(loaded.GetMethod(m.MethodIndex)!.NameIndex) == "main");
        Assert.NotNull(method.Code);
        Assert.Equal(2, method.Code!.RawInsns.Length);
        var smali = Disassembler.DisassembleMethod(loaded, method, cls);
        Assert.Contains("const/4", smali);
        Assert.Contains("return-void", smali);
    }

    [Fact]
    public void WriteLoadWrite_IsByteStable()
    {
        var first = BuildMinimalDex().ToArray();
        var loaded = DexFile.Load(first);
        var second = loaded.ToArray();

        Assert.Equal(first, second);
    }

    [Fact]
    public void Load_ThrowsOnInvalidMagic()
    {
        Assert.Throws<DexException>(() => DexFile.Load(new byte[0x70]));
    }

    [Fact]
    public void Describe_ContainsClassCount()
    {
        var dex = BuildMinimalDex();
        var description = dex.Describe();
        Assert.Contains("classes: 1", description);
        Assert.Contains("declared methods: 1", description);
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

    private static string Encoding_Ascii(byte[] bytes, int offset, int length)
        => System.Text.Encoding.ASCII.GetString(bytes, offset, length);
}