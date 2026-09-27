using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Comm.Protocols.Mtk.XmlProfile;
using Xunit;

namespace UotanStudio.Tests.Comm.Protocols.Mtk;

public static class MtkXmlProfileTests
{
    [Theory]
    [InlineData("1.0", true)]
    [InlineData("1.1", true)]
    [InlineData("1.2", false)]
    [InlineData("2.0", false)]
    [InlineData("", false)]
    public static void Recognize_ReportedVersion_ResolvesOnlyReviewedProfiles(string version, bool known) =>
        Assert.Equal(known, MtkXmlProfile.Recognize(version) is not null);

    [Theory]
    [InlineData(MtkXmlFieldPolicy.Strict)]
    [InlineData(MtkXmlFieldPolicy.TolerantUnknown)]
    public static void Parse_UnknownVersion_RejectsAndReportsVersion(MtkXmlFieldPolicy policy)
    {
        var error = Assert.Throws<InvalidDataException>(() => MtkXmlMessage.Parse(
            "<host><version>2.0</version><command>CMD:END</command><result>OK</result></host>", policy));
        Assert.Contains("2.0", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public static void Parse_Strict_UnknownField_Rejects()
    {
        Assert.Throws<InvalidDataException>(() => MtkXmlMessage.Parse(
            "<host><version>1.0</version><command>CMD:END</command><result>OK</result><status>busy</status></host>"));
    }

    [Fact]
    public static void Parse_Tolerant_UnknownField_IgnoredWithoutChangingKnownFields()
    {
        var strict = MtkXmlMessage.Parse(
            "<host><version>1.0</version><command>CMD:END</command><result>OK</result><arg><code>0</code></arg></host>");
        var tolerant = MtkXmlMessage.Parse(
            "<host><version>1.0</version><status>busy</status><command>CMD:END</command><result>OK</result><arg><code>0</code></arg></host>",
            MtkXmlFieldPolicy.TolerantUnknown);

        Assert.Empty(strict.IgnoredFields);
        Assert.Equal(["status"], tolerant.IgnoredFields);
        Assert.Equal(strict.Command, tolerant.Command);
        Assert.Equal(strict.Result, tolerant.Result);
        Assert.Equal(strict.Arguments, tolerant.Arguments);
    }

    [Fact]
    public static void Parse_Tolerant_DuplicateKnownField_StillRejects() =>
        Assert.Throws<InvalidDataException>(() => MtkXmlMessage.Parse(
            "<host><version>1.0</version><command>CMD:END</command><command>CMD:END</command></host>",
            MtkXmlFieldPolicy.TolerantUnknown));

    [Fact]
    public static void Parse_Tolerant_DuplicateIgnorableField_StillRejects() =>
        Assert.Throws<InvalidDataException>(() => MtkXmlMessage.Parse(
            "<host><version>1.0</version><command>CMD:END</command><status>a</status><status>b</status></host>",
            MtkXmlFieldPolicy.TolerantUnknown));

    [Fact]
    public static void Parse_Tolerant_IgnorableFieldFlood_StillRejects()
    {
        var extras = string.Concat(Enumerable.Range(0, 9).Select(index => $"<f{index}>x</f{index}>"));
        Assert.Throws<InvalidDataException>(() => MtkXmlMessage.Parse(
            $"<host><version>1.0</version><command>CMD:END</command>{extras}</host>",
            MtkXmlFieldPolicy.TolerantUnknown));
    }

    [Theory]
    [InlineData(MtkXmlFieldPolicy.Strict)]
    [InlineData(MtkXmlFieldPolicy.TolerantUnknown)]
    public static void Parse_CommandWithoutPrefix_Rejects(MtkXmlFieldPolicy policy) =>
        Assert.Throws<InvalidDataException>(() => MtkXmlMessage.Parse(
            "<host><version>1.0</version><command>END</command></host>", policy));

    [Theory]
    [InlineData(MtkXmlFieldPolicy.Strict)]
    [InlineData(MtkXmlFieldPolicy.TolerantUnknown)]
    public static void Parse_NamespacedUnknownField_Rejects(MtkXmlFieldPolicy policy) =>
        Assert.Throws<InvalidDataException>(() => MtkXmlMessage.Parse(
            "<host><version>1.0</version><command>CMD:END</command><n:status xmlns:n='evil'>x</n:status></host>", policy));

    [Theory]
    [InlineData(MtkXmlFieldPolicy.Strict)]
    [InlineData(MtkXmlFieldPolicy.TolerantUnknown)]
    public static void Parse_DepthViolation_Rejects(MtkXmlFieldPolicy policy) =>
        Assert.Throws<InvalidDataException>(() => MtkXmlMessage.Parse(
            "<host><version>1.0</version><command>CMD:END</command><status><a><b><c/></b></a></status></host>", policy));

    [Fact]
    public static void Version10_Whitelist_CoversReviewedCommandsAndGradesUnknownFields()
    {
        var profile = MtkXmlProfile.Version10;
        Assert.True(profile.IsKnownField("command"));
        Assert.True(profile.IsIgnorableField("status"));
        Assert.False(profile.IsIgnorableField("command"));
        Assert.True(profile.Commands.Contains("CMD:READ-FLASH"));
        Assert.True(profile.Commands.Contains("CMD:REBOOT"));
        Assert.False(profile.Commands.Contains("CMD:ROOT-SHELL"));
        Assert.True(profile.RebootModes.Contains("FASTBOOT"));
        Assert.Equal("CHK_NO", profile.FileRequests.ChecksumToken);
        Assert.Equal(1024 * 1024, profile.FileRequests.MaximumPacketBytes);
        Assert.Equal(2, profile.FileRequests.AllowedDirections.Count);
    }
}