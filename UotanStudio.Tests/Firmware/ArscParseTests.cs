using UotanStudio.Firmware.Arsc;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// ARSC parser tests: the parser reads a real resources.arsc when present on this developer machine
/// (skipped otherwise), validating package/type/entry extraction on a 1.9 MB production file.
/// <para>ARSC 解析测试：本机存在真实 resources.arsc 时解析验证（否则跳过），在 1.9 MB 生产文件上
/// 校验包/类型/条目提取。</para>
/// </summary>
public class ArscParseTests
{
    private const string RealArscPath =
        @"C:\Users\28319\Desktop\新建文件夹\test\arsc_test\resources.arsc";

    [Fact]
    public void Parse_RealMagiskArsc_ExtractsPackagesAndEntries()
    {
        if (!File.Exists(RealArscPath))
        {
            return; // 非本机环境：跳过真实文件测试。
            // Non-local environment: skip the real-file test.
        }

        var bytes = File.ReadAllBytes(RealArscPath);
        Assert.True(ArscParser.IsArsc(bytes));

        var table = ArscParser.Parse(bytes);

        // 全局字符串池应非空，包至少一个。
        // The global string pool must be non-empty, with at least one package.
        Assert.True(table.GlobalPool.Count > 0);
        Assert.True(table.Packages.Count > 0);

        foreach (var pkg in table.Packages)
        {
            Assert.True(pkg.Id > 0);
            Assert.True(pkg.TypeStrings.Count > 0, $"package 0x{pkg.Id:x2} has no type strings");
            Assert.True(pkg.KeyStrings.Count > 0, $"package 0x{pkg.Id:x2} has no key strings");
        }

        // 描述摘要应包含包统计信息。
        // The description summary must include package statistics.
        var describe = table.Describe();
        Assert.Contains("ARSC", describe);
        Assert.Contains("packages:", describe);
    }

    [Fact]
    public void Parse_RealMagiskArsc_ContainsStringResources()
    {
        if (!File.Exists(RealArscPath))
        {
            return;
        }

        var table = ArscParser.Parse(File.ReadAllBytes(RealArscPath));

        // 全表至少存在 string 类型（type id 2）的条目。
        // The table must contain at least one string-type (type id 2) entry somewhere.
        var hasStringType = table.Packages.Any(p =>
            p.Types.Any(t => t.Id == 2 && t.Entries.Any(e => e is not null)));
        Assert.True(hasStringType);

        // 抽查首个 string 条目的值格式化不抛异常。
        // Spot-check: formatting the first string entry's value must not throw.
        var firstString = table.Packages
            .SelectMany(p => p.Types)
            .FirstOrDefault(t => t.Id == 2 && t.Entries.Any(e => e is not null));
        if (firstString is not null)
        {
            var entry = firstString.Entries.First(e => e is not null)!;
            var pkg = table.Packages.First(p => p.Types.Contains(firstString));
            _ = entry.Value?.FormatValue(table.GlobalPool, pkg.Id);
        }
    }
}