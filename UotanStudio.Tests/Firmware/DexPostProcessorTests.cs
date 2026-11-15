using System.Buffers.Binary;
using UotanStudio.Firmware.AndroidMeta.Dex;
using UotanStudio.Firmware.AndroidMeta.Dex.Smali;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class DexPostProcessorTests
{
    private const string DebugSource = """
        .class public LExample;
        .super Ljava/lang/Object;
        .source "Original.java"
        .method public static run(I)V
            .registers 3
            .param p0, "argument"
            .prologue
            .line 100
            const/4 v0, 0
            .line 103
            .local v0, "counter":I
            const/4 v1, 0
            .source "Other#File.java"
            .line 20
            nop
            .line 10000
            .end local v0
            nop
            .epilogue
            .restart local v0
            .end local v1
            return-void
        .end method
        """;

    private const string PlainSource = """
        .class public LExample;
        .method public static run()V
            .registers 1
            return-void
        .end method
        """;

    [Fact]
    public void StripDebugInfo_FullRemoval_RemovesEveryDebugDirective()
    {
        var original = SmaliAssembler.Assemble(DebugSource).ToArray();
        var bytes = DexPostProcessor.StripDebugInfoRebuilt(original, options: null, clearSourceFile: true);
        var stripped = DexFile.Load(bytes);
        Assert.DoesNotContain(stripped.Strings, s => s == "Original.java");
        Assert.DoesNotContain(stripped.Strings, s => s == "argument");
        var text = Disassembler.DisassembleAll(stripped);
        Assert.DoesNotContain(".line", text, StringComparison.Ordinal);
        Assert.DoesNotContain(".local", text, StringComparison.Ordinal);
        Assert.DoesNotContain(".param", text, StringComparison.Ordinal);
        Assert.DoesNotContain(".prologue", text, StringComparison.Ordinal);
        Assert.DoesNotContain(".epilogue", text, StringComparison.Ordinal);
        Assert.DoesNotContain(".source", text, StringComparison.Ordinal);
        Assert.Contains("const/4 v0, 0", text, StringComparison.Ordinal);
        Assert.True(bytes.Length < original.Length, "the rebuilt image must shrink when debug content is removed");
        Assert.Equal(bytes, SmaliAssembler.Assemble(text).ToArray());
    }

    [Theory]
    [InlineData(true, true, true, true, false, ".source \"Other#File.java\"")]
    [InlineData(false, true, true, true, true, ".line 100")]
    [InlineData(true, false, true, true, true, ".local v0")]
    [InlineData(true, true, false, true, true, ".param p0")]
    [InlineData(true, true, true, false, true, ".prologue")]
    public void StripDebugInfo_GranularOption_RemovesOnlyThatContent(
        bool keepLine, bool keepLocal, bool keepParam, bool keepPrologue, bool keepSource, string marker)
    {
        var dex = SmaliAssembler.Assemble(DebugSource);
        DexPostProcessor.StripDebugInfo(dex, new StripDebugOptions
        {
            KeepLineInfo = keepLine,
            KeepLocalInfo = keepLocal,
            KeepParamNames = keepParam,
            KeepPrologueMarkers = keepPrologue,
            KeepSourceFile = keepSource
        });
        var text = Disassembler.DisassembleAll(DexFile.Load(dex.ToArray()));
        Assert.DoesNotContain(marker, text, StringComparison.Ordinal);
        Assert.Contains("const/4 v1, 0", text, StringComparison.Ordinal);
    }

    [Fact]
    public void HiddenApiSection_RoundTripsVerbatimAndReportsFlagDistribution()
    {
        var dex = SmaliAssembler.Assemble(PlainSource);
        Assert.Null(dex.HiddenApiClassData);
        var section = new byte[] { 0x09, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x02 };
        dex.HiddenApiClassData = section;
        var bytes = dex.ToArray();

        var parsed = DexFile.Load(bytes);
        Assert.NotNull(parsed.HiddenApiClassData);
        Assert.Equal(section, parsed.HiddenApiClassData);

        var report = DexPostProcessor.AnalyzeHiddenApiFlags(parsed);
        Assert.NotNull(report);
        Assert.False(report!.ParseFailed);
        Assert.Equal(1, report.ClassesWithFlags);
        Assert.Equal(1, report.FlaggedMembers);
        Assert.Equal(1, report.DomainCounts[2]);
    }

    [Fact]
    public void ClearHiddenApiFlags_WritesAllZeroOffsetsSection()
    {
        var dex = BuildDexWithHiddenApiSection();
        Assert.True(DexPostProcessor.ClearHiddenApiFlags(dex));
        Assert.False(DexPostProcessor.ClearHiddenApiFlags(dex));
        var parsed = DexFile.Load(dex.ToArray());
        var section = parsed.HiddenApiClassData!;
        Assert.Equal((uint)section.Length, BinaryPrimitives.ReadUInt32LittleEndian(section));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(section.AsSpan(4)));
        var report = DexPostProcessor.AnalyzeHiddenApiFlags(parsed);
        Assert.Equal(0, report!.ClassesWithFlags);
        Assert.Equal(0, report.FlaggedMembers);
    }

    [Fact]
    public void StripHiddenApiSection_OmitsTheMapEntryOnRebuild()
    {
        var dex = BuildDexWithHiddenApiSection();
        Assert.True(DexPostProcessor.StripHiddenApiSection(dex));
        Assert.False(DexPostProcessor.StripHiddenApiSection(dex));
        Assert.Null(DexFile.Load(dex.ToArray()).HiddenApiClassData);
    }

    [Fact]
    public void Repair_CorruptChecksum_RebuildsVerifiableImage()
    {
        var bytes = SmaliAssembler.Assemble(DebugSource).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 0xDEADBEEF);
        var repaired = DexPostProcessor.Repair(bytes, out var report);
        Assert.True(report.ChecksumMismatched);
        Assert.False(report.TruncatedTrailingBytes);
        DexFile.Load(repaired);
    }

    [Fact]
    public void Repair_AppendedTrailingBytes_CutsThemAway()
    {
        var bytes = SmaliAssembler.Assemble(DebugSource).ToArray();
        var damaged = new byte[bytes.Length + 7];
        bytes.CopyTo(damaged, 0);
        var repaired = DexPostProcessor.Repair(damaged, out var report);
        Assert.True(report.TruncatedTrailingBytes);
        Assert.Equal(7, report.TruncatedByteCount);
        Assert.Equal(bytes.Length, repaired.Length);
        DexFile.Load(repaired);
    }

    [Fact]
    public void Repair_CorruptDataByte_ReportsChecksumAndSignatureMismatch()
    {
        var bytes = SmaliAssembler.Assemble(DebugSource).ToArray();
        bytes[^1] ^= 0xFF;
        var repaired = DexPostProcessor.Repair(bytes, out var report);
        Assert.True(report.ChecksumMismatched);
        Assert.True(report.SignatureMismatched);
        Assert.Equal(bytes.Length, repaired.Length);
        DexFile.Load(repaired);
    }

    private static DexFile BuildDexWithHiddenApiSection()
    {
        var dex = SmaliAssembler.Assemble(PlainSource);
        dex.HiddenApiClassData = [0x09, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x02];
        return dex;
    }
}