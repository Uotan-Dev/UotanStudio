using System.Text;
using System.Text.RegularExpressions;
using UotanStudio.Firmware.BootSecurity.Patcher.Kernel;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class KernelVersionExtractorTests
{
    private static readonly Regex LegacyBanner = new(@"Linux version\s+([^\x00\s]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex LegacyUts = new(
        "(?:UTS_RELEASE|CONFIG_LOCALVERSION)[^\\x00\\r\\n]{0,32}[=:]\\s*[\\\"']?([0-9][^\\x00\\r\\n\\\"']+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    public void ExtractRelease_SyntheticBuffers_MatchesLegacyRegex()
    {
        var random = new Random(20261018);
        string[] fragments =
        [
            "Linux version 5.15.1-android13-5.15 (builder)",
            "linux VERSION\t6.1\r\n",
            "\u0085Linux version\u00a05.10.2",
            "UTS_RELEASE=\"5.10.1-android12-5.10\"",
            "CONFIG_LOCALVERSION='6.6.1'",
            "UTS_RELEASE=x y: 7.0.1",
            "Linux version\u000b",
            "UTS_RELEASE=",
            "garbage"
        ];
        for (var iteration = 0; iteration < 96; iteration++)
        {
            var buffer = new byte[random.Next(256, 1024)];
            random.NextBytes(buffer);
            var fragment = Encoding.Latin1.GetBytes(fragments[iteration % fragments.Length]);
            var position = random.Next(0, buffer.Length - fragment.Length);
            fragment.CopyTo(buffer, position);

            Assert.Equal(LegacyExtract(buffer), KernelVersionExtractor.ExtractRelease(buffer));
        }
    }

    private static string? LegacyExtract(byte[] data)
    {
        var text = Encoding.Latin1.GetString(data);
        var match = LegacyBanner.Match(text);
        if (match.Success) return match.Groups[1].Value.Trim();
        match = LegacyUts.Match(text);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }
}