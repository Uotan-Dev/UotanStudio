using System.Globalization;
using System.Xml.Linq;
using UotanStudio.Locale;
using UotanStudio.Services;
using UotanStudio.Tests.Firmware.Architecture;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class LocaleDictionaryTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo Chinese = CultureInfo.GetCultureInfo("zh-CN");

    private static string? LocaleRoot() =>
        FirmwareContextDependencyScanner.TryFindRepositoryRoot(AppContext.BaseDirectory, out var root)
            ? Path.Combine(root!, "UotanStudio", "Locale")
            : null;

    private static SortedDictionary<string, List<string>> ReadKeys(string directory, bool chineseVariants)
    {
        var keys = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        var files = Directory.EnumerateFiles(directory, "*.resx", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".zh-CN.resx", StringComparison.Ordinal) == chineseVariants)
            .Order(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var document = XDocument.Load(file);
            foreach (var element in document.Root!.Elements("data"))
            {
                var key = element.Attribute("name")?.Value;
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                if (!keys.TryGetValue(key, out var owners))
                {
                    owners = [];
                    keys.Add(key, owners);
                }

                owners.Add(Path.GetFileName(file));
            }
        }

        return keys;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LocaleKeys_WithinOneLanguage_AreDefinedExactlyOnce(bool chineseVariants)
    {
        var root = LocaleRoot();
        if (root is null)
        {
            Assert.Skip("The test host is running outside the checked-out repository, so the locale " +
                "resource files cannot be read.");
        }

        var keys = ReadKeys(root!, chineseVariants);

        Assert.True(keys.Count > 500);
        Assert.Empty(keys.Where(pair => pair.Value.Count > 1)
            .Select(pair => $"{pair.Key} defined in {string.Join(", ", pair.Value)}"));
    }

    [Fact]
    public void LocaleKeys_BothLanguages_DefineTheSameKeySet()
    {
        var root = LocaleRoot();
        if (root is null)
        {
            Assert.Skip("The test host is running outside the checked-out repository, so the locale " +
                "resource files cannot be read.");
        }

        var english = ReadKeys(root!, chineseVariants: false).Keys.ToArray();
        var chinese = ReadKeys(root!, chineseVariants: true).Keys.ToArray();

        Assert.NotEmpty(english);
        Assert.Equal(english, chinese);
    }

    [Fact]
    public void LocaleResourceKeys_MatchTheGeneratedManagerIndex()
    {
        var root = LocaleRoot();
        if (root is null)
        {
            Assert.Skip("The test host is running outside the checked-out repository, so the locale " +
                "resource files cannot be read.");
        }

        var bundled = LocaleKeySets.Common
            .Concat(LocaleKeySets.Menu)
            .Concat(LocaleKeySets.Dock)
            .Concat(LocaleKeySets.Node)
            .Concat(LocaleKeySets.Settings)
            .Concat(LocaleKeySets.Device)
            .Concat(LocaleKeySets.Interaction)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var fromResx = ReadKeys(root!, chineseVariants: false).Keys.Order(StringComparer.Ordinal).ToArray();

        Assert.NotEmpty(bundled);
        Assert.Equal(bundled, fromResx);

        var service = LanguageService.Instance;
        Assert.All(bundled, key => Assert.NotEqual(key, service.GetTranslation(key, English)));
        Assert.All(bundled, key => Assert.NotEqual(key, service.GetTranslation(key, Chinese)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LocaleResourceFiles_ParseWithExactlyOneCulture(bool chineseVariants)
    {
        var root = LocaleRoot();
        if (root is null)
        {
            Assert.Skip("The test host is running outside the checked-out repository, so the locale " +
                "resource files cannot be read.");
        }

        var files = Directory.EnumerateFiles(root!, "*.resx", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".zh-CN.resx", StringComparison.Ordinal) == chineseVariants)
            .ToArray();

        Assert.Equal(7, files.Length);
        Assert.All(files, file =>
        {
            var document = XDocument.Load(file);
            Assert.Equal("root", document.Root!.Name.LocalName);
            Assert.NotEmpty(document.Root.Elements("data"));
        });
    }
}