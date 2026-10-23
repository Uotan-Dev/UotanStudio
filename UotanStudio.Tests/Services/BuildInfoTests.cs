using System.Text.RegularExpressions;
using UotanStudio.Build;
using UotanStudio.Tests.Firmware.Architecture;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class BuildInfoTests
{
    [Fact]
    public void Version_CompiledBuild_IsSemanticVersion()
    {
        Assert.Matches(new Regex(@"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$"), BuildInfo.Version);
    }

    [Fact]
    public void Channel_CompiledBuild_IsOneOfTheFourChannels()
    {
        Assert.True(Enum.IsDefined(BuildInfo.Channel));
        Assert.Contains(BuildInfo.ChannelKey, new[]
        {
            "About_Channel_Release",
            "About_Channel_Beta",
            "About_Channel_Canary",
            "About_Channel_Experimental",
        });
        Assert.Contains(BuildInfo.ChannelDescriptionKey, new[]
        {
            "About_Channel_Release_Description",
            "About_Channel_Beta_Description",
            "About_Channel_Canary_Description",
            "About_Channel_Experimental_Description",
        });
    }

    [Fact]
    public void ChannelKeys_AllFourChannels_AreDistinctAndDescribed()
    {
        var channels = Enum.GetValues<BuildChannel>();

        Assert.Equal(4, channels.Length);
        Assert.Equal(4, channels.Select(BuildInfo.GetChannelKey).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(4, channels.Select(BuildInfo.GetChannelDescriptionKey).Distinct(StringComparer.Ordinal).Count());
        Assert.All(channels, channel =>
        {
            Assert.StartsWith("About_Channel_", BuildInfo.GetChannelKey(channel), StringComparison.Ordinal);
            Assert.StartsWith("About_Channel_", BuildInfo.GetChannelDescriptionKey(channel), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Commit_CompiledBuild_ShortHashIsPrefixOfFullHash()
    {
        if (!BuildInfo.HasCommit)
        {
            Assert.Skip("This build has no Git revision metadata, so there is no commit hash to verify.");
        }

        Assert.Equal(40, BuildInfo.Commit.Length);
        Assert.StartsWith(BuildInfo.CommitShort, BuildInfo.Commit, StringComparison.Ordinal);
        Assert.EndsWith(BuildInfo.Commit, BuildInfo.CommitUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void VersionProps_CheckedOutRepository_DeclaresPrefixAndKnownChannel()
    {
        if (!FirmwareContextDependencyScanner.TryFindRepositoryRoot(AppContext.BaseDirectory, out var root))
        {
            Assert.Skip("The test host is running outside the checked-out repository, so Version.props cannot be read.");
        }

        var path = Path.Combine(root!, "Version.props");
        Assert.True(File.Exists(path), $"Missing {path}.");

        var document = System.Xml.Linq.XDocument.Load(path);
        var properties = document.Root!.Elements("PropertyGroup").SelectMany(group => group.Elements()).ToDictionary(
            element => element.Name.LocalName,
            element => element.Value.Trim(),
            StringComparer.Ordinal);

        Assert.Matches(new Regex(@"^\d+\.\d+\.\d+$"), properties["VersionPrefix"]);
        Assert.Contains(properties["VersionChannel"], new[] { "release", "beta", "canary", "experimental" });
    }
}