using UotanStudio.Models;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class OpenSourceProjectsTests
{
    [Fact]
    public void Catalog_CurrentEntries_AreUniqueAndComplete()
    {
        var entries = OpenSourceProjects.All;

        Assert.True(entries.Count >= 30);
        Assert.Equal(entries.Count, entries.Select(entry => entry.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.All(entries, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Name));
            Assert.False(string.IsNullOrWhiteSpace(entry.Description));
            Assert.False(string.IsNullOrWhiteSpace(entry.LicenseName));
            Assert.True(IsHttps(entry.RepositoryUrl), entry.Name);
            Assert.True(IsHttps(entry.LicenseUrl), entry.Name);
            Assert.True(IsHttps(entry.AuthorGitHubUrl), entry.Name);
            Assert.True(IsHttps(entry.AuthorAvatarUrl), entry.Name);
        });
    }

    [Fact]
    public void Catalog_KnownLicenseCorrections_AreApplied()
    {
        var byName = OpenSourceProjects.All.ToDictionary(entry => entry.Name, StringComparer.Ordinal);

        Assert.Equal("Apache-2.0", byName["ModelContextProtocol C# SDK"].LicenseName);
        Assert.Equal("LGPL-3.0-or-later", byName["LibUsbDotNet"].LicenseName);
        Assert.StartsWith("Proprietary", byName["Semi.Avalonia.AvaloniaEdit"].LicenseName, StringComparison.Ordinal);
        Assert.StartsWith("Proprietary", byName["Semi.Avalonia.Dock"].LicenseName, StringComparison.Ordinal);
        Assert.Equal("MIT", byName["LuaCSharp"].LicenseName);
        Assert.Equal("MIT", byName["QRCoder"].LicenseName);
    }

    private static bool IsHttps(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;
}