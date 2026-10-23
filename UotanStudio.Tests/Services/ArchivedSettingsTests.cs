using System.Text.Json;
using UotanStudio.Services;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>Protects settings when switching between builds with different optional features.</summary>
public sealed class ArchivedSettingsTests
{
    [Fact]
    public void LoadAndSave_DropRemovedSettingsAndPreserveOtherSettings()
    {
        const string json = """
            {"hdcPath":"obsolete-hdc","HdcPath":"legacy-hdc","autoBackupBeforeFlash":true,"autoSave":true,
             "hdcDefaultKeyId":"my-hdc-key","mcpExposeEnabled":true}
            """;
        var settings = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings)!.Copy();
        using var saved = JsonDocument.Parse(JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings));
        Assert.False(saved.RootElement.TryGetProperty("hdcPath", out _));
        Assert.False(saved.RootElement.TryGetProperty("HdcPath", out _));
        Assert.False(saved.RootElement.TryGetProperty("autoBackupBeforeFlash", out _));
        Assert.True(saved.RootElement.GetProperty("autoSave").GetBoolean());
        Assert.Equal("my-hdc-key", saved.RootElement.GetProperty("hdcDefaultKeyId").GetString());
        Assert.True(saved.RootElement.GetProperty("mcpExposeEnabled").GetBoolean());
    }

    [Fact]
    public void SaveAndClone_PreserveUnavailableFeatureSettings()
    {
        const string json = """
            {"legacyExposeEnabled":true,"legacyExposePort":18321,
             "legacyServers":[{"name":"local","transport":"Stdio","command":"example","enabled":true}]}
            """;
        var settings = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings)!;
        var copy = settings.Copy();
        Assert.NotSame(settings.AdditionalSettings, copy.AdditionalSettings);
        using var saved = JsonDocument.Parse(JsonSerializer.Serialize(copy, AppSettingsJsonContext.Default.AppSettings));
        using var original = JsonDocument.Parse(json);
        foreach (var property in original.RootElement.EnumerateObject())
            Assert.True(JsonElement.DeepEquals(property.Value, saved.RootElement.GetProperty(property.Name)));
        copy.AdditionalSettings!.Remove("legacyServers");
        Assert.True(settings.AdditionalSettings!.ContainsKey("legacyServers"));
    }
}