using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Models;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Smali;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class DexSmaliAssemblerTests
{
    [Fact]
    public void DisassemblyCanBeEditedAndRebuilt()
    {
        var dex = new DexFile("035");
        dex.Strings.AddRange(["Lexample/Main;", "Ljava/lang/Object;", "main", "V", "Main.java"]);
        dex.Types.AddRange(["Lexample/Main;", "Ljava/lang/Object;", "V"]);
        dex.Protos.Add(new ProtoDefinition { ShortyIndex = 3, ReturnTypeIndex = 2 });
        dex.Methods.Add(new MethodId { ClassIndex = 0, ProtoIndex = 0, NameIndex = 2 });
        var cls = new ClassDefinition { TypeIndex = 0, SuperclassIndex = 1, SourceFileIndex = 4 };
        cls.DirectMethods.Add(new EncodedMethod
        {
            MethodIndex = 0,
            AccessFlags = AccessFlags.Public | AccessFlags.Static,
            Code = new CodeItem { RegistersSize = 1, RawInsns = [0x1200, 0x0E00] }
        });
        dex.Classes.Add(cls);

        var text = Disassembler.DisassembleAll(dex).Replace("#0x0", "#0x1", StringComparison.Ordinal);
        var rebuilt = SmaliAssembler.Assemble(text).ToArray();
        var loaded = DexFile.Load(rebuilt);
        var code = Assert.Single(Assert.Single(loaded.Classes).DirectMethods).Code!;
        Assert.Equal((ushort)0x1210, code.RawInsns[0]);
    }
}