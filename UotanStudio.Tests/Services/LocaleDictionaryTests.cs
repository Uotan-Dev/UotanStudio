using UotanStudio.Tests.Firmware.Architecture;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class LocaleDictionaryTests
{
    private static readonly SearchOption Recursive = SearchOption.AllDirectories;

    private static SortedDictionary<string, List<string>> ReadKeys(string directory)
    {
        var keys = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(directory, "*.axaml", Recursive).Order(StringComparer.Ordinal))
        {
            var text = File.ReadAllText(file);
            foreach (System.Text.RegularExpressions.Match match in
                     System.Text.RegularExpressions.Regex.Matches(text, "x:Key=\"(?<key>[^\"]+)\""))
            {
                if (!keys.TryGetValue(match.Groups["key"].Value, out var owners))
                {
                    owners = [];
                    keys.Add(match.Groups["key"].Value, owners);
                }

                owners.Add(Path.GetFileName(file));
            }
        }

        return keys;
    }

    private static string? LocaleRoot() =>
        FirmwareContextDependencyScanner.TryFindRepositoryRoot(AppContext.BaseDirectory, out var root)
            ? Path.Combine(root!, "UotanStudio", "Locale")
            : null;

    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-CN")]
    public void LocaleKeys_WithinOneLanguage_AreDefinedExactlyOnce(string language)
    {
        var root = LocaleRoot();
        if (root is null)
        {
            Assert.Skip("The test host is running outside the checked-out repository, so the locale " +
                "dictionaries cannot be read.");
        }

        var keys = ReadKeys(Path.Combine(root!, language));

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
                "dictionaries cannot be read.");
        }

        var english = ReadKeys(Path.Combine(root!, "en-US")).Keys.ToArray();
        var chinese = ReadKeys(Path.Combine(root!, "zh-CN")).Keys.ToArray();

        Assert.Equal(english, chinese);
    }
}