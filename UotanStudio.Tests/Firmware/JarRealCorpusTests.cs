using System.IO.Compression;
using UotanStudio.Firmware.BoundedContexts.AndroidMeta.Dex;
using UotanStudio.Services.Firmware;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Real-file JAR regression tests over an extracted firmware tree. The corpus root defaults to
/// <c>C:\Users\28319\Desktop\新建文件夹\NewProject1\NewProject1</c> and can be overridden with
/// <c>UOTAN_JAR_CORPUS</c>; tests skip when the corpus is missing.
/// <para>真实固件目录上的 JAR 回归测试。语料根默认
/// <c>C:\Users\28319\Desktop\新建文件夹\NewProject1\NewProject1</c>，可用 <c>UOTAN_JAR_CORPUS</c>
/// 覆盖；语料缺失时跳过。</para>
/// </summary>
public class JarRealCorpusTests
{
    private static string? FindCorpusRoot()
    {
        var root = Environment.GetEnvironmentVariable("UOTAN_JAR_CORPUS");
        if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
        {
            return root;
        }

        const string defaultRoot = @"C:\Users\28319\Desktop\新建文件夹\NewProject1\NewProject1";
        return Directory.Exists(defaultRoot) ? defaultRoot : null;
    }

    public static TheoryData<string> RealJarTheory()
    {
        var data = new TheoryData<string>();
        var root = FindCorpusRoot();
        if (root is null)
        {
            return data;
        }

        foreach (var jar in Directory.EnumerateFiles(root, "*.jar", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal)
                     .Take(24))
        {
            data.Add(jar);
        }

        return data;
    }

    /// <summary>
    /// <para>抽样真实 JAR：容器枚举分类、DEX 条目解析与反汇编必须成功。</para>
    /// Sampled real JARs: container enumeration/classification, DEX entry parsing and
    /// disassembly must succeed.
    /// </summary>
    [Theory]
    [MemberData(nameof(RealJarTheory))]
    public void RealJar_ListParseAndDisassemble(string jar)
    {
        var skipReason = $"JAR corpus unavailable: {jar}";
        if (!File.Exists(jar))
        {
            Assert.Skip(skipReason);
        }

        var entries = JarDexContainer.ListEntries(jar);
        Assert.NotEmpty(entries);
        var dexEntries = entries.Where(e => e.Kind == JarEntryKind.Dex).Select(e => e.Name).ToArray();
        if (dexEntries.Length == 0)
        {
            // 资源型 JAR（无 DEX）合法存在，仅验证枚举不抛错。
            // Resource-only JARs (no DEX) legitimately exist; only the enumeration must not throw.
            return;
        }

        var dex = DexFile.Load(JarDexContainer.LoadEntry(jar, dexEntries[0]));
        Assert.NotEmpty(dex.Classes);

        var smali = Disassembler.DisassembleAll(dex);
        Assert.False(string.IsNullOrWhiteSpace(smali));
        Assert.Contains(".class", smali, StringComparison.Ordinal);
    }

    /// <summary>
    /// <para>抽样真实多 DEX JAR：合并视图聚合数据必须覆盖全部条目。</para>
    /// Sampled real multi-DEX JARs: merged-view aggregation must cover every entry.
    /// </summary>
    [Theory]
    [MemberData(nameof(RealJarTheory))]
    public void RealJar_MergeViewAggregatesAllEntries(string jar)
    {
        if (!File.Exists(jar))
        {
            Assert.Skip($"JAR corpus unavailable: {jar}");
        }

        var dexEntries = JarDexContainer.ListDexEntries(jar);
        if (dexEntries.Count < 2)
        {
            return; // 单 DEX JAR 无合并语义，由 RealJar_ListParseAndDisassemble 覆盖。
        }

        var total = 0;
        var merged = new System.Text.StringBuilder();
        foreach (var entry in dexEntries)
        {
            var dex = DexFile.Load(JarDexContainer.LoadEntry(jar, entry));
            Assert.NotEmpty(dex.Classes);
            total += dex.Classes.Count;
            merged.AppendLine(Disassembler.DisassembleAll(dex));
        }

        Assert.True(total > 0);
        Assert.Contains(".class", merged.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// <para>抽样真实 JAR：在临时副本上执行重建写回，重建后的条目必须仍是合法 DEX
    /// 且其余条目字节保持不变。</para>
    /// Sampled real JARs: rebuild write-back on a temporary copy must leave a valid DEX in the
    /// target entry and keep all other entries byte-identical.
    /// </summary>
    [Theory]
    [MemberData(nameof(RealJarTheory))]
    public void RealJar_RebuildWriteBack_RoundTrips(string jar)
    {
        if (!File.Exists(jar))
        {
            Assert.Skip($"JAR corpus unavailable: {jar}");
        }

        var dexEntries = JarDexContainer.ListDexEntries(jar);
        if (dexEntries.Count == 0)
        {
            return;
        }

        // 副本上验证写回，不触碰语料原件。
        // Verify write-back on a copy; the corpus original is never touched.
        var copy = Path.Combine(Path.GetTempPath(), "JarRealCorpusTests", Guid.NewGuid().ToString("N") + ".jar");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.Copy(jar, copy, overwrite: true);
        try
        {
            var target = dexEntries[0];
            var otherEntries = JarDexContainer.ListEntries(copy)
                .Where(e => e.Name != target)
                .Select(e => (e.Name, Content: JarDexContainer.LoadEntry(copy, e.Name)))
                .ToList();

            var dex = DexFile.Load(JarDexContainer.LoadEntry(copy, target));
            JarDexContainer.WriteBack(copy, target, dex.ToArray());

            var rebuilt = DexFile.Load(JarDexContainer.LoadEntry(copy, target));
            Assert.Equal(dex.Classes.Count, rebuilt.Classes.Count);
            Assert.Equal(dex.Strings.Count, rebuilt.Strings.Count);
            foreach (var (name, content) in otherEntries)
            {
                Assert.Equal(content, JarDexContainer.LoadEntry(copy, name));
            }
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(copy)!, recursive: true); } catch { /* best effort 尽力清理 */ }
        }
    }

    /// <summary>
    /// <para>抽样真实 JAR：临时副本上的资源导出与替换回环。</para>
    /// Sampled real JARs: resource export and replace round-trip on a temporary copy.
    /// </summary>
    [Theory]
    [MemberData(nameof(RealJarTheory))]
    public void RealJar_ResourceExportReplace_RoundTrips(string jar)
    {
        if (!File.Exists(jar))
        {
            Assert.Skip($"JAR corpus unavailable: {jar}");
        }

        var resource = JarDexContainer.ListEntries(jar)
            .FirstOrDefault(e => e.Kind == JarEntryKind.Resource && e.Length > 0 && e.Length < 1024 * 1024);
        if (resource is null)
        {
            return;
        }

        var work = Path.Combine(Path.GetTempPath(), "JarRealCorpusTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var copy = Path.Combine(work, Path.GetFileName(jar));
        File.Copy(jar, copy, overwrite: true);
        try
        {
            var exported = Path.Combine(work, "exported.bin");
            JarDexContainer.ExtractResource(copy, resource.Name, exported);
            Assert.Equal(resource.Length, new FileInfo(exported).Length);

            JarDexContainer.ReplaceResource(copy, resource.Name, exported);
            Assert.Equal(resource.Length, JarDexContainer.LoadEntry(copy, resource.Name).Length);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* best effort 尽力清理 */ }
        }
    }

    /// <summary>
    /// <para>真实裸 DEX 回归：AudioEffectCenter 的 classes.dex 曾触发 encoded_value Float/Double
    /// 位重解释的 checked 溢出，必须能完整解析与反汇编。</para>
    /// Real bare DEX regression: AudioEffectCenter's classes.dex used to trigger the checked
    /// overflow on encoded_value Float/Double bit reinterpretation; it must parse and disassemble.
    /// </summary>
    [Fact]
    public void RealBareDex_AudioEffectCenter_ParsesAndDisassembles()
    {
        const string path = @"C:\Users\28319\Desktop\新建文件夹\NewProject1\NewProject1\my_product_a\app\AudioEffectCenter\AudioEffectCenter\classes.dex";
        if (!File.Exists(path))
        {
            Assert.Skip($"Real DEX unavailable: {path}");
        }

        var dex = DexFile.Load(File.ReadAllBytes(path));
        Assert.NotEmpty(dex.Classes);

        var smali = Disassembler.DisassembleAll(dex);
        Assert.Contains(".class", smali, StringComparison.Ordinal);

        // 重建回环：写回的字节必须能再次解析且类数一致。
        // Rebuild round-trip: the written bytes must parse again with the same class count.
        var rebuilt = DexFile.Load(dex.ToArray());
        Assert.Equal(dex.Classes.Count, rebuilt.Classes.Count);
    }

    /// <summary>
    /// <para>汇总统计：枚举语料内全部 JAR，输出总数、含 DEX 的数量与解析成功率（诊断信息）。</para>
    /// Summary: enumerates every JAR in the corpus and reports the total count, the DEX-bearing
    /// count and the parse success rate (diagnostic information).
    /// </summary>
    [Fact]
    public void RealJar_CorpusSummary()
    {
        var root = FindCorpusRoot();
        if (root is null)
        {
            Assert.Skip("JAR corpus directory not found.");
        }

        var jars = Directory.EnumerateFiles(root, "*.jar", SearchOption.AllDirectories).ToList();
        var withDex = 0;
        var parsed = 0;
        var failures = new List<string>();
        foreach (var jar in jars)
        {
            try
            {
                var dexEntries = JarDexContainer.ListDexEntries(jar);
                if (dexEntries.Count == 0)
                {
                    continue;
                }

                withDex++;
                DexFile.Load(JarDexContainer.LoadEntry(jar, dexEntries[0]));
                parsed++;
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(jar)}: {ex.GetType().Name}: {ex.Message} @ {ex.StackTrace?.Split('\n').FirstOrDefault()}");
            }
        }

        Assert.True(parsed == withDex,
            $"解析失败 {failures.Count}/{withDex}: {string.Join("; ", failures.Take(5))}");
    }
}
