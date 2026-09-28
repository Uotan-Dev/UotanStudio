using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Models;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Smali;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class DexSmaliAssemblerTests
{
    [Theory]
    [InlineData("ILjava/lang/String;[J", "D", "DILL", 3)]
    [InlineData("JD", "V", "VJD", 4)]
    [InlineData("[[I[Ljava/lang/String;", "[B", "LLL", 2)]
    [InlineData("int, java.lang.String, long[]", "double", "DILL", 3)]
    public void Assemble_MethodSignature_PreservesDescriptorsShortyAndParameterWords(
        string parameters, string result, string shorty, int parameterWords)
    {
        var dex = SmaliAssembler.Assemble($"""
            .class public Lexample/Main;
            .super Ljava/lang/Object;
            .method public static calculate({parameters}){result}
                .locals 2
            .end method
            """);

        var prototype = Assert.Single(dex.Protos);
        var method = Assert.Single(Assert.Single(dex.Classes).DirectMethods);
        Assert.Equal(shorty, dex.Strings[prototype.ShortyIndex]);
        Assert.Equal(shorty, prototype.ComputeShorty(dex));
        Assert.Equal(parameterWords, method.Code!.InsSize);
        Assert.Equal(parameterWords + 2, method.Code.RegistersSize);
    }

    [Fact]
    public void Assemble_InstanceMethod_CountsReceiverAndWideParameters()
    {
        var dex = SmaliAssembler.Assemble("""
            .class public Lexample/Main;
            .super Ljava/lang/Object;
            .method public calculate(JD[I)V
                .registers 8
                .locals 2
            .end method
            """);

        var method = Assert.Single(Assert.Single(dex.Classes).VirtualMethods);
        Assert.Equal(6, method.Code!.InsSize);
        Assert.Equal(8, method.Code.RegistersSize);
    }

    [Theory]
    [InlineData("abstract")]
    [InlineData("native")]
    public void Assemble_CodeFreeMethod_PreservesDeclarationThroughSerialization(string modifier)
    {
        var dex = SmaliAssembler.Assemble($"""
            .class public abstract Lexample/Main;
            .super Ljava/lang/Object;
            .method public {modifier} calculate(J)I
            .end method
            """);

        var loaded = DexFile.Load(dex.ToArray());
        var method = Assert.Single(Assert.Single(loaded.Classes).VirtualMethods);
        Assert.Null(method.Code);
        var prototype = Assert.Single(loaded.Protos);
        Assert.Equal("IJ", loaded.Strings[prototype.ShortyIndex]);
        Assert.Equal("J", loaded.Types[Assert.Single(prototype.ParameterTypeIndices)]);
    }

    [Theory]
    [InlineData("V", "V")]
    [InlineData("[V", "I")]
    [InlineData("Ljava/lang/String", "V")]
    [InlineData("L;", "V")]
    [InlineData("I,,J", "V")]
    [InlineData("", "II")]
    [InlineData("", "[V")]
    public void Assemble_InvalidSignature_RejectsMalformedDescriptor(string parameters, string result)
    {
        Assert.Throws<FormatException>(() => SmaliAssembler.Assemble($"""
            .class public Lexample/Main;
            .super Ljava/lang/Object;
            .method public abstract calculate({parameters}){result}
            .end method
            """));
    }

    [Theory]
    [InlineData(".registers 1")]
    [InlineData(".locals -1")]
    [InlineData(".registers 65536")]
    [InlineData(".registers 4\n.locals 2")]
    public void Assemble_InvalidRegisterDeclaration_RejectsInput(string registers)
    {
        Assert.Throws<FormatException>(() => SmaliAssembler.Assemble($"""
            .class public Lexample/Main;
            .super Ljava/lang/Object;
            .method public calculate(J)V
                {registers}
            .end method
            """));
    }

    [Theory]
    [InlineData(".registers 1")]
    [InlineData("return-void")]
    public void Assemble_AbstractMethodWithBody_RejectsInput(string body)
    {
        Assert.Throws<FormatException>(() => SmaliAssembler.Assemble($"""
            .class public abstract Lexample/Main;
            .super Ljava/lang/Object;
            .method public abstract calculate()V
                {body}
            .end method
            """));
    }

    [Fact]
    public void Assemble_SharedMethodSignature_ReusesPrototype()
    {
        var dex = SmaliAssembler.Assemble("""
            .class public abstract Lexample/Main;
            .super Ljava/lang/Object;
            .method public abstract first(I)V
            .end method
            .method public abstract second(I)V
            .end method
            """);

        Assert.Single(dex.Protos);
        Assert.Equal(2, Assert.Single(dex.Classes).VirtualMethods.Count);
        Assert.All(dex.Methods, method => Assert.Equal(0, method.ProtoIndex));
    }

    [Fact]
    public void Assemble_StaticInitializers_PreservesValuesThroughSerializationAndDisassembly()
    {
        var dex = SmaliAssembler.Assemble("""
            .class public Lexample/Main;
            .super Ljava/lang/Object;
            .field public static enabled:Z = true
            .field public static small:B = -1t
            .field public static medium:S = -123s
            .field public static letter:C = '\u0041'
            .field public static count:I = 42
            .field public static wide:J = -4294967296L
            .field public static ratio:F = 1.25f
            .field public static precise:D = -2.5
            .field public static text:Ljava/lang/String; = "left = right, \"quoted\""
            .field public static absent:Ljava/lang/Object; = null
            .field public static zero:I
            """);

        var loaded = DexFile.Load(dex.ToArray());
        var values = Assert.Single(loaded.Classes).StaticValues!;
        Assert.Equal(10, values.Count);
        var staticFields = Assert.Single(loaded.Classes).StaticFields;
        Assert.Equal(11, staticFields.Count);
        Assert.Equal("zero", loaded.Strings[loaded.Fields[staticFields[^1].FieldIndex].NameIndex]);
        var byName = staticFields.Take(values.Count).Select((field, index) =>
            (Name: loaded.Strings[loaded.Fields[field.FieldIndex].NameIndex], Value: values[index]))
            .ToDictionary(item => item.Name, item => item.Value);
        Assert.True(byName["enabled"].AsBoolean());
        Assert.Equal(255, byName["small"].AsInt());
        Assert.Equal(-123, byName["medium"].AsInt());
        Assert.Equal('A', (char)byName["letter"].AsInt());
        Assert.Equal(42, byName["count"].AsInt());
        Assert.Equal(-4294967296L, byName["wide"].AsLong());
        Assert.Equal(1.25f, Assert.IsType<float>(byName["ratio"].Value));
        Assert.Equal(-2.5, Assert.IsType<double>(byName["precise"].Value));
        Assert.Equal("left = right, \"quoted\"", loaded.Strings[byName["text"].AsIndex()]);
        Assert.Equal(EncodedValueType.Null, byName["absent"].Type);
        Assert.DoesNotContain("zero", byName.Keys);

        var rebuilt = SmaliAssembler.Assemble(Disassembler.DisassembleAll(loaded));
        var rebuiltValues = Assert.Single(rebuilt.Classes).StaticValues!;
        Assert.Equal(values.Select(v => v.Type), rebuiltValues.Select(v => v.Type));
        Assert.Equal(values.Select(v => v.Type == EncodedValueType.String ? loaded.Strings[v.AsIndex()] : v.Value),
            rebuiltValues.Select(v => v.Type == EncodedValueType.String ? rebuilt.Strings[v.AsIndex()] : v.Value));
    }

    [Theory]
    [InlineData(".field public count:I = 1")]
    [InlineData(".field public static count:V = 0")]
    [InlineData(".field public static count:I =")]
    [InlineData(".field public static count:B = 256t")]
    [InlineData(".field public static count:C = 'ab'")]
    [InlineData(".field public static count:Z = tru")]
    [InlineData(".field public static count:[I = {1,}")]
    public void Assemble_InvalidFieldInitializer_RejectsInput(string field)
    {
        Assert.Throws<FormatException>(() => SmaliAssembler.Assemble($"""
            .class public Lexample/Main;
            .super Ljava/lang/Object;
            {field}
            """));
    }

    [Theory]
    [InlineData(".catch Ljava/lang/Exception; {:start .. :end} :handler")]
    [InlineData(".unknown-debug 42")]
    [InlineData(".annotation runtime Lexample/Mark;")]
    [InlineData(".method public nested()V")]
    public void Assemble_UnsupportedMethodDirective_RejectsRatherThanDiscarding(string directive)
    {
        Assert.Throws<FormatException>(() => SmaliAssembler.Assemble($"""
            .class public Lexample/Main;
            .super Ljava/lang/Object;
            .method public static run()V
                .registers 1
                {directive}
                return-void
            .end method
            """));
    }

    [Theory]
    [InlineData(".annotation runtime Lexample/Mark;")]
    [InlineData(".end method")]
    [InlineData(".method public static run()V")]
    public void Assemble_InvalidOrUnsupportedClassStructure_RejectsInput(string declaration)
    {
        Assert.Throws<FormatException>(() => SmaliAssembler.Assemble($"""
            .class public Lexample/Main;
            .super Ljava/lang/Object;
            {declaration}
            """));
    }

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
            Code = new CodeItem { RegistersSize = 1, RawInsns = [0x0012, 0x000E] }
        });
        dex.Classes.Add(cls);

        var text = Disassembler.DisassembleAll(dex).Replace("const/4 v0, 0", "const/4 v0, 1", StringComparison.Ordinal);
        var rebuilt = SmaliAssembler.Assemble(text).ToArray();
        var loaded = DexFile.Load(rebuilt);
        var code = Assert.Single(Assert.Single(loaded.Classes).DirectMethods).Code!;
        Assert.Equal((ushort)0x1012, code.RawInsns[0]);
    }
}