using UotanStudio.Firmware.Arsc;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// ARSC round-trip tests: rebuild a parsed resource table via <see cref="ArscWriter"/> and verify
/// the re-parsed model preserves packages, type tables and entry values; the real Magisk resources.arsc
/// (when present locally) is used to validate value-level rebuild on a production file.
/// <para>ARSC 往返测试：经 <see cref="ArscWriter"/> 重建解析后的资源表，验证重新解析的模型保留
/// 包/类型表/条目值；本机存在真实 Magisk resources.arsc 时在生产文件上校验值级重建。</para>
/// </summary>
public class ArscWriterRoundTripTests
{
    private const string RealArscPath =
        @"C:\Users\28319\Desktop\新建文件夹\test\arsc_test\resources.arsc";

    [Fact]
    public void WriteThenParse_PreservesPackagesAndEntries()
    {
        if (!File.Exists(RealArscPath))
        {
            return; // 非本机环境：跳过真实文件测试。
            // Non-local environment: skip the real-file test.
        }

        var table = ArscParser.Parse(File.ReadAllBytes(RealArscPath));
        var rebuilt = ArscWriter.Write(table);

        // 重建后重新解析：包数、全局池数、类型表数、条目总数应一致。
        // Re-parse after rebuild: package count, global pool size, type-table count and total
        // entry count must all match.
        var doc2 = ArscParser.Parse(rebuilt);
        Assert.Equal(table.Packages.Count, doc2.Packages.Count);
        Assert.Equal(table.GlobalPool.Count, doc2.GlobalPool.Count);

        var entries1 = table.Packages.Sum(p => p.Types.Sum(t => t.Entries.Count(e => e is not null)));
        var entries2 = doc2.Packages.Sum(p => p.Types.Sum(t => t.Entries.Count(e => e is not null)));
        Assert.Equal(entries1, entries2);

        for (var p = 0; p < table.Packages.Count; p++)
        {
            Assert.Equal(table.Packages[p].Id, doc2.Packages[p].Id);
            Assert.Equal(table.Packages[p].Name, doc2.Packages[p].Name);
            Assert.Equal(table.Packages[p].Types.Count, doc2.Packages[p].Types.Count);
        }
    }

    [Fact]
    public void WriteThenParse_StringEntryValuesSurvive()
    {
        if (!File.Exists(RealArscPath))
        {
            return;
        }

        var table = ArscParser.Parse(File.ReadAllBytes(RealArscPath));
        var rebuilt = ArscWriter.Write(table);
        var doc2 = ArscParser.Parse(rebuilt);

        // 抽查各包 string 类型（id=2）前 5 个条目：键名与字符串值在重建后保持一致。
        // Spot-check the first 5 entries of each package's string type (id=2): key names and string
        // values must match after the rebuild.
        for (var p = 0; p < table.Packages.Count; p++)
        {
            var src = table.Packages[p];
            var dst = doc2.Packages[p];
            foreach (var (srcType, dstType) in src.Types.Zip(dst.Types).Where(t => t.First.Id == 2))
            {
                var checkedEntries = 0;
                for (var i = 0; i < Math.Min(srcType.Entries.Count, dstType.Entries.Count) && checkedEntries < 5; i++)
                {
                    var se = srcType.Entries[i];
                    var de = dstType.Entries[i];
                    if (se is null || de is null)
                    {
                        continue;
                    }
                    Assert.Equal(src.KeyStrings.GetString(se.KeyIndex), dst.KeyStrings.GetString(de.KeyIndex));
                    var sv = se.Value?.FormatValue(table.GlobalPool, src.Id);
                    var dv = de.Value?.FormatValue(doc2.GlobalPool, dst.Id);
                    Assert.Equal(sv, dv);
                    checkedEntries++;
                }
            }
        }
    }

    [Fact]
    public void WriteThenParse_EditStringValuePersists()
    {
        if (!File.Exists(RealArscPath))
        {
            return;
        }

        var table = ArscParser.Parse(File.ReadAllBytes(RealArscPath));

        // 定位第一个 String 型条目，Intern 新值并更新 ValueData（模拟编辑器值级编辑）。
        // Locate the first String-typed entry, intern a new value and update ValueData (simulating a
        // value-level editor edit).
        var (pkg, type, entry) = table.Packages
            .SelectMany(p => p.Types.Select(t => (p, t)))
            .Select(x => (x.p, x.t, e: x.t.Entries.FirstOrDefault(e => e?.Value is { IsComplex: false } v && v.Type == ResValueType.String && !string.IsNullOrEmpty(x.p.KeyStrings.GetString(e.KeyIndex)))))
            .FirstOrDefault(x => x.e is not null);

        if (entry is null)
        {
            return;
        }

        var oldIndex = checked((int)entry.Value!.Data);
        var oldValue = table.GlobalPool.GetString(oldIndex);
        var newValue = oldValue + "_edited";
        entry.Value.Data = (uint)table.GlobalPool.Intern(newValue);

        var rebuilt = ArscWriter.Write(table);
        var doc2 = ArscParser.Parse(rebuilt);

        // 重建后该条目的值应变为编辑后的新字符串。
        // After the rebuild, the entry's value must be the edited string.
        var (np, nt, ne) = doc2.Packages
            .SelectMany(p => p.Types.Select(t => (p, t)))
            .Select(x => (x.p, x.t, e: x.t.Entries.FirstOrDefault(e => e?.Value is { IsComplex: false } v && v.Type == ResValueType.String)))
            .FirstOrDefault(x => x.e is not null);
        Assert.NotNull(ne);
        Assert.Equal(newValue, ne!.Value!.FormatValue(doc2.GlobalPool, np.Id));
    }
}
