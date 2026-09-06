using System.IO.Compression;
using UotanStudio.Firmware.Arsc;
using UotanStudio.Tests.Infrastructure;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// ARSC parser tests: the parser runs against the real resources.arsc entry of Magisk-v30.6.apk
/// from the firmware corpus when present, validating package/type/entry extraction on a production
/// file; cases are skipped via <see cref="Assert.Skip"/> when the corpus file is missing.
/// <para>ARSC 解析测试：语料库存在 Magisk-v30.6.apk 时解析其 resources.arsc 条目，在生产文件上
/// 校验包/类型/条目提取；语料缺失时以 <see cref="Assert.Skip"/> 跳过。</para>
/// </summary>
public class ArscParseTests
{
    /// <summary>语料库中 Magisk-v30.6.apk 的路径（只定位一次）；不存在时为 null。
    /// <para>The located Magisk-v30.6.apk path (resolved once); null when absent.</para></summary>
    private static readonly string? ApkPath = FirmwareCorpus.FindFile("Magisk-v30.6.apk");

    /// <summary>
    /// 从语料库 Magisk-v30.6.apk 读取 resources.arsc 条目字节；apk 或条目缺失时返回 null。
    /// <para>Reads the resources.arsc entry bytes from the corpus Magisk-v30.6.apk; returns null
    /// when the apk or the entry is missing.</para>
    /// </summary>
    private static byte[]? ReadResourcesArsc()
    {
        var apkPath = ApkPath;
        if (apkPath is null)
        {
            return null;
        }

        using var zip = ZipFile.OpenRead(apkPath);
        var entry = zip.GetEntry("resources.arsc");
        if (entry is null)
        {
            return null;
        }

        using var stream = entry.Open();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    [Fact]
    public void Parse_RealMagiskArsc_ExtractsPackagesAndEntries()
    {
        var bytes = ReadResourcesArsc();
        if (bytes is null)
        {
            Assert.Skip("Magisk-v30.6.apk or its resources.arsc entry not found in firmware corpus");
        }

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
        var bytes = ReadResourcesArsc();
        if (bytes is null)
        {
            Assert.Skip("Magisk-v30.6.apk or its resources.arsc entry not found in firmware corpus");
        }

        var table = ArscParser.Parse(bytes);

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