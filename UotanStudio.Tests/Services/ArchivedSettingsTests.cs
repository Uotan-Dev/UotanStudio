using System.Text.Json;
using UotanStudio.Services;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>Protects settings when switching between builds with different optional features.</summary>
public sealed class ArchivedSettingsTests
{
    [Fact]
    public void SaveAndClone_PreserveUnavailableFeatureSettings()
    {
        const string json = """
            {"mcpExposeEnabled":true,"mcpExposePort":18321,
             "mcpServers":[{"name":"local","transport":"Stdio","command":"example","enabled":true}]}
            """;
        var settings = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings)!;
        var copy = settings.Copy();
        Assert.NotSame(settings.AdditionalSettings, copy.AdditionalSettings);
        using var saved = JsonDocument.Parse(JsonSerializer.Serialize(copy, AppSettingsJsonContext.Default.AppSettings));
        using var original = JsonDocument.Parse(json);
        foreach (var property in original.RootElement.EnumerateObject())
            Assert.True(JsonElement.DeepEquals(property.Value, saved.RootElement.GetProperty(property.Name)));
        copy.AdditionalSettings!.Remove("mcpServers");
        Assert.True(settings.AdditionalSettings!.ContainsKey("mcpServers"));
    }
}
