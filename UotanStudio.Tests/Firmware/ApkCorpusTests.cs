using UotanStudio.Compress.Zip;
using UotanStudio.Firmware.AndroidMeta.Apk;
using UotanStudio.Firmware.AndroidMeta.Apk.Signing;
using UotanStudio.Firmware.AndroidMeta.Dex;
using UotanStudio.Firmware.AndroidMeta.Dex.Smali;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Real-corpus acceptance over the APKs in <c>UOTAN_APK_CORPUS</c>: signature verification
/// consistency against the presence detector, dex parse/round-trip, debug strip and repartition
/// on each corpus file's smallest dex.
/// <para>对 <c>UOTAN_APK_CORPUS</c> 目录真实 APK 的验收：验签与存在性检测的一致性、dex 解析往返、
/// 调试剥离与重分区（取每个语料文件最小的 dex）。</para>
/// </summary>
public sealed class ApkCorpusTests
{
    private const string CorpusVariable = "UOTAN_APK_CORPUS";

    private static string RequireCorpusDirectory()
    {
        var directory = Environment.GetEnvironmentVariable(CorpusVariable);
        Assert.SkipUnless(
            !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory),
            $"Set {CorpusVariable} to a directory of real APK files (for example reference/apks) to run the APK corpus acceptance.");
        return directory!;
    }

    [Fact]
    public void Verify_CorpusApks_SchemePresenceAgreesWithVerification()
    {
        var directory = RequireCorpusDirectory();
        foreach (var apkPath in Directory.EnumerateFiles(directory, "*.apk").Order(StringComparer.Ordinal))
        {
            var info = ApkParser.Analyze(apkPath);
            var verification = ApkSignatureVerifier.Verify(apkPath, TestContext.Current.CancellationToken);

            Assert.Equal(info.SigningSchemes.Contains("v1"), verification.V1 != ApkSchemeStatus.NotPresent);
            Assert.Equal(info.SigningSchemes.Contains("v2"), verification.V2 != ApkSchemeStatus.NotPresent);
            Assert.Equal(info.SigningSchemes.Contains("v3"), verification.V3 != ApkSchemeStatus.NotPresent);
            Assert.All(verification.Problems, p => Assert.False(string.IsNullOrWhiteSpace(p)));
        }
    }

    [Fact]
    public async Task RoundTrip_CorpusSmallestDex_ReassemblesByteStable()
    {
        var directory = RequireCorpusDirectory();
        foreach (var apkPath in Directory.EnumerateFiles(directory, "*.apk").Order(StringComparer.Ordinal))
        {
            var (entry, bytes) = LoadSmallestDexEntry(apkPath);
            if (entry is null)
            {
                continue;
            }

            var dex = DexFile.Load(bytes);
            var text = Disassembler.DisassembleAll(dex);
            var reassembled = await Task.Run(
                () => SmaliAssembler.Assemble(text).ToArray(),
                TestContext.Current.CancellationToken);

            var parsed = DexFile.Load(reassembled);
            Assert.Equal(dex.Classes.Count, parsed.Classes.Count);
            Assert.Equal(dex.Methods.Count, parsed.Methods.Count);
        }
    }

    [Fact]
    public async Task PostProcess_CorpusSmallestDex_StripRepartitionAndRepairStayValid()
    {
        var directory = RequireCorpusDirectory();
        foreach (var apkPath in Directory.EnumerateFiles(directory, "*.apk").Order(StringComparer.Ordinal))
        {
            var (entry, bytes) = LoadSmallestDexEntry(apkPath);
            if (entry is null)
            {
                continue;
            }

            var stripped = await Task.Run(
                () => DexPostProcessor.StripDebugInfoRebuilt(bytes, options: null, clearSourceFile: true),
                TestContext.Current.CancellationToken);
            var strippedModel = DexFile.Load(stripped);
            Assert.True(strippedModel.Methods.Count > 0 || strippedModel.Classes.Count == 0);
            Assert.DoesNotContain(strippedModel.Strings, s => s.Contains("DebugInfoMarkerThatCannotExist", StringComparison.Ordinal));

            var originalModel = DexFile.Load(bytes);
            var (repaired, repairReport) = await Task.Run(
                () =>
                {
                    var rebuilt = DexPostProcessor.Repair(bytes, out var report);
                    return (rebuilt, report);
                },
                TestContext.Current.CancellationToken);
            // Repair rebuilds with fresh tables, signature and checksum, so the byte length is free to change: some
            // production DEX files carry large unmapped payloads past the last map item, and rebuilding drops them.
            // The contract is a loadable image with the same declared structure plus an accurate report.
            Assert.Equal(1, repairReport.RepairedImages);
            var repairedModel = DexFile.Load(repaired);
            Assert.Equal(originalModel.Classes.Count, repairedModel.Classes.Count);
            Assert.Equal(originalModel.Methods.Count, repairedModel.Methods.Count);

            var parts = await Task.Run(
                () => DexRepartitioner.Split(bytes, TestContext.Current.CancellationToken),
                TestContext.Current.CancellationToken);
            var descriptorSet = strippedModel.Classes
                .Select(c => strippedModel.GetType(c.TypeIndex)).ToHashSet();
            var seen = new HashSet<string>();
            foreach (var part in parts)
            {
                var parsed = DexFile.Load(part);
                foreach (var cls in parsed.Classes)
                {
                    Assert.True(seen.Add(parsed.GetType(cls.TypeIndex)));
                }
            }
            Assert.Equal(descriptorSet, seen);
        }
    }

    /// <summary>Loads the byte content of the smallest classes*.dex entry of a container.
    /// <para>加载容器内最小 classes*.dex 条目的字节。</para></summary>
    /// <returns>The entry name and bytes; a null entry name when the file holds no dex.
    /// <para>条目名与字节；文件不含 dex 时条目名为 null。</para></returns>
    private static (string? Entry, byte[] Bytes) LoadSmallestDexEntry(string apkPath)
    {
        using var stream = File.OpenRead(apkPath);
        using var archive = ZipContainer.OpenRead(stream, leaveOpen: false);
        var candidates = new List<(string Name, long Size)>();
        foreach (var zipEntry in archive.Entries)
        {
            if (zipEntry.FullName.Contains('/', StringComparison.Ordinal)
                || !zipEntry.FullName.EndsWith(".dex", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            candidates.Add((zipEntry.FullName, zipEntry.Length));
        }
        if (candidates.Count == 0)
        {
            return (null, []);
        }
        var smallest = candidates.MinBy(c => c.Size);
        var entry = archive.Entries.Single(e => e.FullName == smallest.Name);
        var bytes = new byte[entry.Length];
        using var input = entry.Open();
        input.ReadExactly(bytes);
        return (smallest.Name, bytes);
    }
}