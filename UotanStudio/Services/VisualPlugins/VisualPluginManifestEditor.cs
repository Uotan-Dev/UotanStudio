using System.Globalization;
using UotanStudio.Projects;

namespace UotanStudio.Services.VisualPlugins;

internal static class VisualPluginManifestEditor
{
    internal static bool TryApply(VisualPluginManifest manifest, string? author, string? description,
        string? platforms, string? nodeApiVersion)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!int.TryParse(nodeApiVersion?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var apiVersion)
            || apiVersion is < 1 or > VisualPluginManifest.CurrentNodeApiVersion)
        {
            return false;
        }

        manifest.Author = (author ?? string.Empty).Trim();
        manifest.Description = (description ?? string.Empty).Trim();
        manifest.Platforms =
        [
            .. (platforms ?? string.Empty)
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
        ];
        manifest.NodeApiVersion = apiVersion;
        return true;
    }
}