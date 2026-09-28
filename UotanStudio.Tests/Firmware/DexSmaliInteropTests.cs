using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex.Smali;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class DexSmaliInteropTests
{
    public static bool ToolsAvailable => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("UOTAN_SMALI_CLASSPATH"))
        && File.Exists(Environment.GetEnvironmentVariable("UOTAN_SMALI_JAVA"));

    [Fact(Skip = "Set UOTAN_SMALI_CLASSPATH and UOTAN_SMALI_JAVA to run independent Smali/Baksmali interoperability.", SkipUnless = nameof(ToolsAvailable))]
    public async Task Assemble_StandardSyntax_InteroperatesWithSmaliAndBaksmali()
    {
        var source = """
                .class public abstract LExample;
                .super Ljava/lang/Object;
                .source "Example\"#.java"
                .field public static z:Ljava/lang/String; = "a#b"
                .field public static a:I = 42
                .field public static ratio:F = 1.25f
                .field public static tiny:F = 1.4E-45f
                .field public static precise:D = -2.5
                .field public static negativeZero:D = -0.0
                .field public volatile transient state:I
                .method public static run([Ljava/lang/String;J)I
                    .registers 5
                    :start
                    const-string v0, "a#b" # comment outside the string
                    const/4 v1, -1
                    invoke-static {v0, v1}, LExample;->consume(Ljava/lang/String;I)V
                    :end
                    return v1
                    :handler
                    move-exception v0
                    const/4 v1, 0
                    return v1
                    .catch Ljava/lang/Exception; {:start .. :end} :handler
                    .catchall {:start .. :end} :handler
                .end method
                .method public static native consume(Ljava/lang/String;I)V
                .end method
                .method public bridge varargs abstract bridge([Ljava/lang/Object;)Ljava/lang/Object;
                .end method
                """;
        await AssertInteropAsync(source);
    }

    [Theory(Skip = "Set UOTAN_SMALI_CLASSPATH and UOTAN_SMALI_JAVA to run independent Smali/Baksmali interoperability.", SkipUnless = nameof(ToolsAvailable))]
    [InlineData("""
        packed-switch v0, :data
        packed-switch v0, :data
        return-void
        :case
        return-void
        :data
        .packed-switch 0
            :case
        .end packed-switch
        """)]
    [InlineData("""
        nop
        packed-switch v0, :data
        return-void
        :first
        return-void
        :second
        return-void
        :data
        .packed-switch -2
            :first
            :second
        .end packed-switch
        """)]
    [InlineData("""
        goto :body
        :data
        .sparse-switch
            -2147483648 -> :first
            2147483647 -> :second
        .end sparse-switch
        :body
        sparse-switch v0, :data
        return-void
        :first
        return-void
        :second
        return-void
        """)]
    [InlineData("""
        nop
        fill-array-data v0, :bytes
        fill-array-data v0, :shorts
        fill-array-data v0, :ints
        fill-array-data v0, :longs
        return-void
        :bytes
        .array-data 1
            0xfft
            1
            0x7f
        .end array-data
        :shorts
        .array-data 2
            -1s
            'A'
            32768
        .end array-data
        :ints
        .array-data 4
            0xffffffff
            1.0f
            -2147483648
        .end array-data
        :longs
        .array-data 8
            0xffffffffffffffffL
            -0.0
            -9223372036854775808L
        .end array-data
        """)]
    public async Task Assemble_Payloads_InteroperatesWithSmaliAndBaksmali(string body)
    {
        await AssertInteropAsync(".class public LExample;\n.super Ljava/lang/Object;\n.method public static run()V\n.registers 1\n"
            + body + "\n.end method");
    }

    [Theory(Skip = "Set UOTAN_SMALI_CLASSPATH and UOTAN_SMALI_JAVA to run independent Smali/Baksmali interoperability.", SkipUnless = nameof(ToolsAvailable))]
    [InlineData(1, 300)]
    [InlineData(8, 9000)]
    public async Task Assemble_LargePayloadBranch_MatchesIndependentSourceAssembly(int width, int count)
    {
        var body = "goto :body\n:data\n.array-data " + width + "\n"
            + string.Concat(Enumerable.Repeat("1\n", count))
            + ".end array-data\n:body\nreturn-void";
        await AssertInteropAsync(".class public LExample;\n.super Ljava/lang/Object;\n.method public static run()V\n.registers 1\n"
            + body + "\n.end method", assembleSource: true);
    }

    [Theory(Skip = "Set UOTAN_SMALI_CLASSPATH and UOTAN_SMALI_JAVA to run independent Smali/Baksmali interoperability.", SkipUnless = nameof(ToolsAvailable))]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disassemble_UnreferencedSwitch_PreservesIndependentBaksmaliView(bool sparse)
    {
        var root = Path.Combine(Path.GetTempPath(), "uotan-smali-orphan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var token = TestContext.Current.CancellationToken;
            var dex = SmaliAssembler.Assemble("""
                .class public LExample;
                .super Ljava/lang/Object;
                .method public static run()V
                .registers 1
                return-void
                .end method
                """);
            Assert.Single(Assert.Single(dex.Classes).DirectMethods).Code!.RawInsns = sparse
                ? [0xe, 0, 0x200, 2, 0xffff, 0xffff, 1, 0, 0, 0x8000, 0xffff, 0x7fff]
                : [0xe, 0, 0x100, 2, 0xffff, 0xffff, 0, 0x8000, 0xffff, 0x7fff];
            var original = dex.ToArray();
            var rebuilt = SmaliAssembler.Assemble(Disassembler.DisassembleAll(DexFile.Load(original))).ToArray();
            Assert.Equal(original, rebuilt);
            var views = new List<string>();
            foreach (var (name, bytes) in new[] { ("original", original), ("rebuilt", rebuilt) })
            {
                var path = Path.Combine(root, name + ".dex");
                var output = Path.Combine(root, name);
                await File.WriteAllBytesAsync(path, bytes, token);
                await RunToolAsync(root, "org.jf.baksmali.Main", ["disassemble", path, "-o", output], token);
                views.Add(await File.ReadAllTextAsync(Path.Combine(output, "Example.smali"), token));
            }
            Assert.Contains(sparse ? ".sparse-switch" : ".packed-switch", views[0]);
            Assert.Equal(views[0], views[1]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AssertInteropAsync(string source, bool assembleSource = false, bool reverse = true)
    {
        var root = Path.Combine(Path.GetTempPath(), "uotan-smali-interop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var token = TestContext.Current.CancellationToken;
            var dex = SmaliAssembler.Assemble(source);
            var ownDex = Path.Combine(root, "own.dex");
            var exported = Path.Combine(root, "Example.smali");
            var externalDex = Path.Combine(root, "external.dex");
            var externalSource = Path.Combine(root, "baksmali");
            if (assembleSource)
            {
                var input = Path.Combine(root, "Input.smali");
                var reference = Path.Combine(root, "reference.dex");
                await File.WriteAllTextAsync(input, source, new UTF8Encoding(false), token);
                await RunToolAsync(root, "org.jf.smali.Main", ["assemble", "--api", "28", input, "-o", reference], token);
                var referenceDex = DexFile.Load(reference);
                Assert.Equal(ComparableClassText(dex), ComparableClassText(referenceDex));
            }
            await File.WriteAllBytesAsync(ownDex, dex.ToArray(), token);
            await File.WriteAllTextAsync(exported, Disassembler.DisassembleClass(dex, Assert.Single(dex.Classes)), new UTF8Encoding(false), token);
            await RunToolAsync(root, "org.jf.smali.Main", ["assemble", "--api", "28", exported, "-o", externalDex], token);
            Assert.True(File.Exists(externalDex));
            var loaded = DexFile.Load(externalDex);
            Assert.Equal(ComparableClassText(dex), ComparableClassText(loaded));

            if (!reverse)
            {
                return;
            }
            await RunToolAsync(root, "org.jf.baksmali.Main", ["disassemble", ownDex, "-o", externalSource], token);
            var externalText = await File.ReadAllTextAsync(Path.Combine(externalSource, "Example.smali"), token);
            var rebuilt = SmaliAssembler.Assemble(externalText);
            Assert.Equal(ComparableClassText(dex), ComparableClassText(rebuilt));
            Assert.Equal(dex.ToArray(), rebuilt.ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string ComparableClassText(DexFile dex)
    {
        var aliases = new Dictionary<string, int>(StringComparer.Ordinal);
        return Regex.Replace(Disassembler.DisassembleClass(dex, Assert.Single(dex.Classes)),
            @"(?m)^(\s*invoke-custom(?:/range)?\s+\{[^}\r\n]*\},\s*)call_site_(\d+)(\()",
            match =>
            {
                var original = match.Groups[2].Value;
                if (!aliases.TryGetValue(original, out var alias))
                {
                    aliases.Add(original, alias = aliases.Count);
                }
                return match.Groups[1].Value + "call_site_" + alias + match.Groups[3].Value;
            }, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
    }

    private static async Task RunToolAsync(string directory, string main, string[] arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("UOTAN_SMALI_JAVA")!)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-cp");
        start.ArgumentList.Add(Environment.GetEnvironmentVariable("UOTAN_SMALI_CLASSPATH")!);
        start.ArgumentList.Add(main);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
        var stdout = await output;
        var stderr = await error;
        Assert.True(process.ExitCode == 0, stdout + Environment.NewLine + stderr);
        Assert.True(string.IsNullOrWhiteSpace(stderr), stderr);
    }

    [Fact(Skip = "Set UOTAN_SMALI_CLASSPATH and UOTAN_SMALI_JAVA to run independent Smali/Baksmali interoperability.", SkipUnless = nameof(ToolsAvailable))]
    public async Task Assemble_AnnotationsAndParameters_MatchesIndependentSourceAndRoundTrips()
    {
        await AssertInteropAsync(DexSmaliAnnotationTests.Source, assembleSource: true);
    }

    [Fact(Skip = "Set UOTAN_SMALI_CLASSPATH and UOTAN_SMALI_JAVA to run independent Smali/Baksmali interoperability.", SkipUnless = nameof(ToolsAvailable))]
    public async Task Assemble_MethodHandles_MatchesIndependentSourceAndRoundTrips()
    {
        await AssertInteropAsync(DexSmaliMethodHandleTests.Source, assembleSource: true);
    }

    [Fact(Skip = "Set UOTAN_SMALI_CLASSPATH and UOTAN_SMALI_JAVA to run independent Smali/Baksmali interoperability.", SkipUnless = nameof(ToolsAvailable))]
    public async Task Assemble_DebugEvents_MatchesIndependentSourceAndRoundTrips()
    {
        await AssertInteropAsync(DexSmaliDebugTests.Source.Replace(".epilogue", ".prologue", StringComparison.Ordinal), assembleSource: true);
    }

    [Fact(Skip = "Set UOTAN_SMALI_CLASSPATH and UOTAN_SMALI_JAVA to run independent Smali/Baksmali interoperability.", SkipUnless = nameof(ToolsAvailable))]
    public async Task Assemble_Epilogue_MatchesIndependentSourceAndExportAssembly()
    {
        await AssertInteropAsync(DexSmaliDebugTests.Source, assembleSource: true, reverse: false);
    }

    [Fact(Skip = "Set UOTAN_SMALI_CLASSPATH and UOTAN_SMALI_JAVA to run independent Smali/Baksmali interoperability.", SkipUnless = nameof(ToolsAvailable))]
    public async Task Assemble_DebugBranchRelocation_MatchesIndependentSourceAndRoundTrips()
    {
        var source = DexSmaliDebugTests.Source.Replace(".epilogue", ".prologue", StringComparison.Ordinal)
            .Replace("const/4 v0, 0",
                "goto :far\n" + string.Concat(Enumerable.Repeat("nop\n", 128)) + ":far\nconst/4 v0, 0", StringComparison.Ordinal);
        await AssertInteropAsync(source, assembleSource: true);
    }

    [Fact(Skip = "Set UOTAN_SMALI_CLASSPATH and UOTAN_SMALI_JAVA to run independent Smali/Baksmali interoperability.", SkipUnless = nameof(ToolsAvailable))]
    public async Task Assemble_AnonymousDebugLocalAndSourceReset_MatchesIndependentSourceAndRoundTrips()
    {
        await AssertInteropAsync("""
            .class public LExample;
            .super Ljava/lang/Object;
            .method public static run()V
                .registers 1
                .source
                .local v0
                return-void
                .end local v0
            .end method
            """, assembleSource: true);
    }

    [Fact(Skip = "Set UOTAN_SMALI_CLASSPATH and UOTAN_SMALI_JAVA to run independent Smali/Baksmali interoperability.", SkipUnless = nameof(ToolsAvailable))]
    public async Task Assemble_CallSites_MatchesIndependentSourceAndRoundTrips()
    {
        await AssertInteropAsync(DexSmaliCallSiteTests.Source, assembleSource: true);
    }

    [Theory(Skip = "Set UOTAN_SMALI_CLASSPATH and UOTAN_SMALI_JAVA to run independent Smali/Baksmali interoperability.", SkipUnless = nameof(ToolsAvailable))]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Assemble_DistinctCallSites_PreservesIndependentResolutionSlots(bool differentArguments)
    {
        var source = DexSmaliCallSiteTests.Source.Replace("invoke-custom/range {p0 .. p2}, site(",
            "invoke-custom/range {p0 .. p2}, other(", StringComparison.Ordinal);
        if (differentArguments)
        {
            source = source.Replace("other(\"dispatch#()\"", "other(\"another\"", StringComparison.Ordinal);
        }
        await AssertInteropAsync(source, assembleSource: true);
    }

    [Fact(Skip = "Set UOTAN_SMALI_CLASSPATH and UOTAN_SMALI_JAVA to run independent Smali/Baksmali interoperability.", SkipUnless = nameof(ToolsAvailable))]
    public async Task Assemble_JumboRelocation_MatchesIndependentSourceAndRoundTrips()
    {
        await AssertInteropAsync(DexSmaliJumboTests.CreateSource(65530), assembleSource: true);
    }

    [Fact(Skip = "Set UOTAN_SMALI_CLASSPATH and UOTAN_SMALI_JAVA to run independent Smali/Baksmali interoperability.", SkipUnless = nameof(ToolsAvailable))]
    public async Task Assemble_CallSitesAcrossMethods_MatchesIndependentSourceAndRoundTrips()
    {
        await AssertInteropAsync(DexSmaliCallSiteTests.MultiMethodSource, assembleSource: true);
    }

    [Fact(Skip = "Set UOTAN_SMALI_CLASSPATH and UOTAN_SMALI_JAVA to run independent Smali/Baksmali interoperability.", SkipUnless = nameof(ToolsAvailable))]
    public async Task Assemble_NestedCallSiteLiterals_MatchesIndependentSourceAndRoundTrips()
    {
        await AssertInteropAsync(DexSmaliCallSiteTests.NestedSource, assembleSource: true);
    }

    [Fact(Skip = "Set UOTAN_SMALI_CLASSPATH and UOTAN_SMALI_JAVA to run independent Smali/Baksmali interoperability.", SkipUnless = nameof(ToolsAvailable))]
    public async Task Assemble_NumericLiterals_MatchesIndependentSourceAndRoundTrips()
    {
        await AssertInteropAsync(DexSmaliLiteralTests.Source, assembleSource: true);
    }

    [Fact(Skip = "Set UOTAN_SMALI_CLASSPATH and UOTAN_SMALI_JAVA to run independent Smali/Baksmali interoperability.", SkipUnless = nameof(ToolsAvailable))]
    public async Task Assemble_ExplicitFieldValues_MatchesIndependentSourceAndRoundTrips()
    {
        await AssertInteropAsync(DexSmaliFieldValueTests.Source, assembleSource: true);
    }

    [Fact(Skip = "Set UOTAN_SMALI_CLASSPATH and UOTAN_SMALI_JAVA to run independent Smali/Baksmali interoperability.", SkipUnless = nameof(ToolsAvailable))]
    public async Task Assemble_SignedDebugLines_MatchesIndependentSourceAndRoundTrips()
    {
        await AssertInteropAsync(DexSmaliDebugTests.SignedLineSource, assembleSource: true);
    }

    [Fact(Skip = "Set UOTAN_SMALI_CLASSPATH and UOTAN_SMALI_JAVA to run independent Smali/Baksmali interoperability.", SkipUnless = nameof(ToolsAvailable))]
    public async Task Assemble_HexFloatRoundingCorpus_MatchesIndependentSourceAndRoundTrips()
    {
        await AssertInteropAsync(DexSmaliLiteralTests.HexFloatSource(), assembleSource: true);
    }

    [Fact(Skip = "Set UOTAN_SMALI_CLASSPATH and UOTAN_SMALI_JAVA to run independent Smali/Baksmali interoperability.", SkipUnless = nameof(ToolsAvailable))]
    public async Task Assemble_TypedPayloadLiterals_MatchesIndependentSourceAndRoundTrips()
    {
        await AssertInteropAsync(DexSmaliLiteralTests.PayloadSource, assembleSource: true);
    }
}