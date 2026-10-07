using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace UotanStudio.Tests.Firmware.Architecture;

/// <summary>
/// Guards the byte semantics of every <c>u8</c> string literal in the repository.
/// <para>守护全仓库 <c>u8</c> 字面量的字节语义。</para>
/// A <c>u8</c> literal encodes code points as UTF-8, so it cannot express a raw byte at or above 0x80,
/// and the variable-length <c>\x</c> escape silently swallows the hexadecimal characters that follow it.
/// Both mechanisms turned real magic constants into wrong byte sequences, so this scan fails the build when
/// either pattern reappears.
/// </summary>
public partial class U8LiteralByteSemanticsTests
{
    private static readonly string[] ExcludedDirectoryNames =
    [
        "bin", "obj", "artifacts", "TestResults", ".git", ".vs", ".scratch", ".modernization", ".zcode", "node_modules"
    ];

    [Fact]
    public void U8Literals_RepositorySources_UseNoEscapeWiderThanOneByte()
    {
        var repositoryRoot = LocateRepositoryRoot();
        var offenders = new List<string>();
        var inspected = 0;
        foreach (var file in EnumerateSources(repositoryRoot))
        {
            var lineNumber = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;
                foreach (Match literal in U8LiteralPattern().Matches(line))
                {
                    inspected++;
                    foreach (var escape in ReadWideEscapes(literal.Groups[1].Value))
                    {
                        offenders.Add($"{Path.GetRelativePath(repositoryRoot, file)}:{lineNumber}: {escape} inside {literal.Value}");
                    }
                }
            }
        }

        Assert.True(inspected > 0, "The scan found no u8 literals, so it never reached the repository sources.");
        Assert.True(
            offenders.Count == 0,
            "A u8 literal must not use an escape wider than one byte. High bytes belong in a byte collection "
            + "expression such as [0xD0, 0x0D, 0xFE, 0xED], and 0x7F belongs in the fixed-width \\u form. "
            + "Offenders:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void ReadWideEscapes_KnownWideningAndSafeForms_ClassifyAsDocumented()
    {
        Assert.Equal([@"\x7fE"], ReadWideEscapes(@"\x7fELF"));
        Assert.Equal([@"\xD0", @"\xFE", @"\xED"], ReadWideEscapes(@"\xD0\x0D\xFE\xED"));
        Assert.Equal([@"\u00D0"], ReadWideEscapes(@"\u00D0"));
        Assert.Empty(ReadWideEscapes(@"\u007fELF"));
        Assert.Empty(ReadWideEscapes(@"ARM\x64"));
        Assert.Empty(ReadWideEscapes(@"PK\x03\x04"));
        Assert.Empty(ReadWideEscapes(@"adb pair client\0"));
        Assert.Empty(ReadWideEscapes(@"\0.text\0.strtab"));
        Assert.Empty(ReadWideEscapes(@"C:\\x7fELF"));
    }

    private static IEnumerable<string> ReadWideEscapes(string content)
    {
        for (var index = 0; index < content.Length - 1; index++)
        {
            if (content[index] != '\\')
            {
                continue;
            }

            var kind = content[index + 1];
            if (kind == 'x')
            {
                var digits = 0;
                while (digits < 4 && index + 2 + digits < content.Length && Uri.IsHexDigit(content[index + 2 + digits]))
                {
                    digits++;
                }

                if (digits == 0)
                {
                    continue;
                }

                var value = int.Parse(content.AsSpan(index + 2, digits), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                if (digits > 2 || value >= 0x80)
                {
                    yield return content.Substring(index, digits + 2);
                }

                index += digits + 1;
            }
            else if ((kind == 'u' || kind == 'U') && index + 2 < content.Length)
            {
                var width = kind == 'u' ? 4 : 8;
                if (index + 2 + width > content.Length)
                {
                    continue;
                }

                var digits = content.AsSpan(index + 2, width);
                var hexadecimal = true;
                foreach (var character in digits)
                {
                    hexadecimal &= Uri.IsHexDigit(character);
                }

                if (!hexadecimal)
                {
                    continue;
                }

                if (uint.Parse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture) >= 0x80)
                {
                    yield return content.Substring(index, width + 2);
                }

                index += width + 1;
            }
            else
            {
                index++;
            }
        }
    }

    private static IEnumerable<string> EnumerateSources(string repositoryRoot)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var file in Directory.EnumerateFiles(repositoryRoot, "*.cs", options))
        {
            if (file.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var segments = Path.GetRelativePath(repositoryRoot, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(segment => ExcludedDirectoryNames.Contains(segment, StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }

            yield return file;
        }
    }

    private static string LocateRepositoryRoot()
    {
        if (!FirmwareContextDependencyScanner.TryFindRepositoryRoot(AppContext.BaseDirectory, out var repositoryRoot))
        {
            Assert.Skip($"No UotanStudio.slnx found above AppContext.BaseDirectory '{AppContext.BaseDirectory}'; the repository sources are unavailable to this test host.");
        }

        return repositoryRoot!;
    }

    [GeneratedRegex("(?<!\")\"((?:\\\\.|[^\"\\\\])*)\"u8")]
    private static partial Regex U8LiteralPattern();
}