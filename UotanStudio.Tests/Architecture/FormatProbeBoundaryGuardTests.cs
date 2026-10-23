using System.Text.RegularExpressions;
using Xunit;

namespace UotanStudio.Tests.Architecture;

public partial class FormatProbeBoundaryGuardTests
{
    private static readonly HashSet<string> AllowedPaths = new(StringComparer.Ordinal)
    {
    };

    [Fact]
    public void HostSources_DoNotCarryFormatMagicConstants()
    {
        var offenders = new List<string>();
        foreach (var (path, content) in RepositoryLayout.SourceFiles("UotanStudio"))
        {
            if (AllowedPaths.Contains(path) || path.StartsWith("UotanStudio/Locale/", StringComparison.Ordinal))
            {
                continue;
            }

            offenders.AddRange(RepositoryLayout.Matches(path, content, MagicPattern()));
        }

        Assert.True(offenders.Count == 0, "Host code must delegate format detection to UotanStudio.Firmware:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void MagicPattern_Synthetic_ReportsSignatureLiterals()
    {
        const string content = "var gpt = \"EFI PART\"u8;\nvar mbr = 0x55AA;\nvar aml = \"AMLPART!\"u8;";
        Assert.Equal(3, RepositoryLayout.Matches("sample.cs", content, MagicPattern()).Count);
    }

    [GeneratedRegex(@"EFI PART|AMLPART!|0x55AA|0xEF53|0xE0F5E1E2|0xF2F52010|0xED26FF3A|0xD00DFEED|0x27B51956|0xD7B7AB1E", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MagicPattern();
}