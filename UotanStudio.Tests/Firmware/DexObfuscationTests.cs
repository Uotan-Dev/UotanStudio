using System.Text;
using UotanStudio.Firmware.AndroidMeta.Dex;
using UotanStudio.Firmware.AndroidMeta.Dex.Obfuscation;
using UotanStudio.Firmware.AndroidMeta.Dex.Smali;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class DexObfuscationTests
{
    private const string ObfuscatedSource = """
        .class public LOo0;
        .super Ljava/lang/Object;
        .method public constructor <init>()V
            .registers 1
            invoke-direct {p0}, Ljava/lang/Object;-><init>()V
            return-void
        .end method
        .method public static Oo0(I)I
            .registers 2
            return p0
        .end method
        .field public static Oo0l:I
        """;

    [Fact]
    public void Rename_LookalikeNames_RewritesDescriptorsMembersAndWritesMapping()
    {
        var dex = SmaliAssembler.Assemble(ObfuscatedSource);
        var report = DexRenameEngine.Rename(dex);

        Assert.Equal(1, report.RenamedClasses);
        Assert.Equal(1, report.RenamedMethods);
        Assert.Equal(1, report.RenamedFields);
        Assert.Contains("Oo0 -> C0000:", report.Mapping, StringComparison.Ordinal);

        var descriptor = dex.GetType(dex.Classes[0].TypeIndex);
        Assert.Equal("LC0000;", descriptor);

        var methodNames = dex.Classes[0].AllMethods
            .Select(m => dex.GetString(dex.Methods[m.MethodIndex].NameIndex))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["<init>", "m0000"], methodNames);
        var field = dex.Classes[0].StaticFields.Single();
        Assert.Equal("f0000", dex.GetString(dex.Fields[field.FieldIndex].NameIndex));

        var text = Disassembler.DisassembleAll(dex);
        Assert.Contains(".class public LC0000;", text, StringComparison.Ordinal);
        var reassembled = DexFile.Load(SmaliAssembler.Assemble(text).ToArray());
        Assert.Equal(dex.Types, reassembled.Types);
        Assert.Equal(
            DexFile.Load(dex.ToArray()).Classes.Select(c => dex.GetType(c.TypeIndex)),
            reassembled.Classes.Select(c => reassembled.GetType(c.TypeIndex)));
    }

    [Fact]
    public void Rename_UserRuleWithTemplate_OverridesHeuristics()
    {
        var dex = SmaliAssembler.Assemble("""
            .class public Lcom/foo/aaa;
            .super Ljava/lang/Object;
            .method public static run()V
                .registers 1
                return-void
            .end method
            """);
        var report = DexRenameEngine.Rename(dex, new DexRenameOptions
        {
            Rules = [new RenameRule(@"^com\.foo\.aaa$", "Renamed{counter}")],
            RenameLookalikeNames = false,
            RenameWhitespaceNames = false
        });
        Assert.Equal(1, report.RenamedClasses);
        Assert.Contains(dex.Types, t => string.Equals(t, "Lcom/foo/Renamed0;", StringComparison.Ordinal));
        Assert.Contains("com.foo.aaa -> com.foo.Renamed0:", report.Mapping, StringComparison.Ordinal);
    }

    [Fact]
    public void Rename_RejectionTemplate_LeavesTargetAlone()
    {
        var dex = SmaliAssembler.Assemble(ObfuscatedSource);
        var report = DexRenameEngine.Rename(dex, new DexRenameOptions
        {
            Rules = [new RenameRule(@"^Oo0->Oo0$", "")]
        });
        Assert.Equal(0, report.RenamedMethods);
        Assert.Equal(1, report.RenamedClasses);
        Assert.Equal(1, report.RenamedFields);
    }

    [Fact]
    public void Analyze_DetectsSingleByteXorAndBase64()
    {
        var (xorCipher, xorPlain) = BuildXorPair();
        var base64Plain = "https://example.com/api/v2/data"u8.ToArray();
        var base64Cipher = Convert.ToBase64String(base64Plain);
        var source = $"""
            .class public LExample;
            .super Ljava/lang/Object;
            .method public static run()V
                .registers 3
                const-string v0, "{SmaliString(xorCipher)}"
                const-string v1, "{SmaliString(base64Cipher)}"
                return-void
            .end method
            """;
        var dex = SmaliAssembler.Assemble(source);

        var candidates = DexStringDecryptAnalyzer.Analyze(dex);
        Assert.Contains(candidates, c => c.Scheme == "xor1" && c.Decrypted == xorPlain);
        Assert.Contains(candidates, c => c.Scheme == "base64" && c.Decrypted == Encoding.ASCII.GetString(base64Plain));
        Assert.All(candidates, c => Assert.Equal(1, c.InstructionSites));
    }

    [Fact]
    public void Rewrite_ReplacesConstStringOperandsWithDecryptedText()
    {
        var (xorCipher, xorPlain) = BuildXorPair();
        var source = $"""
            .class public LExample;
            .super Ljava/lang/Object;
            .method public static run()V
                .registers 2
                const-string v0, "{SmaliString(xorCipher)}"
                return-void
            .end method
            """;
        var dex = SmaliAssembler.Assemble(source);
        var report = DexStringDecryptAnalyzer.Rewrite(dex);

        Assert.True(report.Rewritten);
        Assert.Equal(1, report.RewrittenSites);
        Assert.Contains(dex.Strings, s => string.Equals(s, xorPlain, StringComparison.Ordinal));
        var line = Disassembler.DisassembleAll(dex)
            .Split('\n')
            .Single(l => l.Contains("const-string", StringComparison.Ordinal));
        Assert.Contains(SmaliString(xorPlain), line, StringComparison.Ordinal);
        Assert.Contains(SmaliAssembler.Assemble(Disassembler.DisassembleAll(dex)).Strings,
            s => string.Equals(s, xorPlain, StringComparison.Ordinal));
    }

    [Fact]
    public void Rewrite_NoCandidates_LeavesPoolUntouched()
    {
        var dex = SmaliAssembler.Assemble("""
            .class public LExample;
            .method public static run()V
                .registers 2
                const-string v0, "plain text value"
                return-void
            .end method
            """);
        var before = dex.Strings.ToArray();
        var report = DexStringDecryptAnalyzer.Rewrite(dex);
        Assert.False(report.Rewritten);
        Assert.Equal(before, dex.Strings);
    }

    /// <summary>Escapes a pool string so it can be embedded verbatim in a quoted smali literal.
    /// <para>转义池字符串，使其可原样嵌入引号包裹的 smali 字面量。</para></summary>
    /// <param name="value">Raw string whose UTF-16 code units are emitted as smali escapes.
    /// <para>原始字符串，其 UTF-16 代码单元按 smali 转义输出。</para></param>
    /// <returns>The escaped body without surrounding quotes, using the escapes the assembler accepts.
    /// <para>不含外层引号的转义正文，使用汇编器接受的转义形式。</para></returns>
    private static string SmaliString(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (c < 0x20)
                    {
                        builder.Append("\\u").Append(((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        return builder.ToString();
    }

    private static (string Cipher, string Plain) BuildXorPair()
    {
        var plain = "https://cdn.example.com/a";
        var cipherBytes = new byte[plain.Length];
        const byte key = 0x5A;
        for (var i = 0; i < plain.Length; i++)
        {
            cipherBytes[i] = (byte)(plain[i] ^ key);
        }
        var cipher = Encoding.Latin1.GetString(cipherBytes);
        return (cipher, plain);
    }
}