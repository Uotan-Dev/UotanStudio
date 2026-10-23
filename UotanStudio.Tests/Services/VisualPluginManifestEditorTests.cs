using System.Globalization;
using UotanStudio.Projects;
using UotanStudio.Services.VisualPlugins;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class VisualPluginManifestEditorTests
{
    [Fact]
    public void TryApply_EditableMetadata_UpdatesAuthorDescriptionPlatformsAndApiVersion()
    {
        var manifest = new VisualPluginManifest { Id = "tests.metadata", Name = "Metadata" };

        var applied = VisualPluginManifestEditor.TryApply(manifest, "  New author  ", "  New description  ",
            " windows, linux , windows ", "1");

        Assert.True(applied);
        Assert.Equal("New author", manifest.Author);
        Assert.Equal("New description", manifest.Description);
        Assert.Equal(["windows", "linux"], manifest.Platforms);
        Assert.Equal(VisualPluginManifest.CurrentNodeApiVersion, manifest.NodeApiVersion);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("")]
    [InlineData("abc")]
    public void TryApply_InvalidNodeApiVersion_LeavesEveryEditableFieldUnchanged(string nodeApiVersion)
    {
        var manifest = new VisualPluginManifest
        {
            Id = "tests.metadata",
            Name = "Metadata",
            Author = "Original author",
            Description = "Original description",
            Platforms = ["windows"],
            NodeApiVersion = VisualPluginManifest.CurrentNodeApiVersion
        };

        var applied = VisualPluginManifestEditor.TryApply(manifest, "Changed", "Changed", "linux", nodeApiVersion);

        Assert.False(applied);
        Assert.Equal("Original author", manifest.Author);
        Assert.Equal("Original description", manifest.Description);
        Assert.Equal(["windows"], manifest.Platforms);
        Assert.Equal(VisualPluginManifest.CurrentNodeApiVersion, manifest.NodeApiVersion);
    }

    [Fact]
    public void TryApply_NodeApiVersionAboveHostLimit_IsRejected()
    {
        var manifest = new VisualPluginManifest
        {
            Id = "tests.metadata",
            Name = "Metadata",
            NodeApiVersion = VisualPluginManifest.CurrentNodeApiVersion
        };
        var above = (VisualPluginManifest.CurrentNodeApiVersion + 1).ToString(CultureInfo.InvariantCulture);

        Assert.False(VisualPluginManifestEditor.TryApply(manifest, "Changed", "Changed", "linux", above));
        Assert.Equal(VisualPluginManifest.CurrentNodeApiVersion, manifest.NodeApiVersion);
    }

    [Fact]
    public void TryApply_BlankPlatformsText_ClearsPlatformDeclaration()
    {
        var manifest = new VisualPluginManifest
        {
            Id = "tests.metadata",
            Name = "Metadata",
            Platforms = ["windows"]
        };

        Assert.True(VisualPluginManifestEditor.TryApply(manifest, "", "", " , , ", "1"));
        Assert.Empty(manifest.Platforms);
    }
}