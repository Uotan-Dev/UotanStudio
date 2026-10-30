namespace UotanStudio.Assets;

internal static class AssetLimits
{
    internal const int MaximumManifestBytes = 1024 * 1024;

    internal const int MaximumEntries = 4096;

    internal const int MaximumBundleKeyLength = 128;

    internal const int MaximumPathLength = 128;

    internal const long MaximumEntryBytes = 256L * 1024 * 1024;

    internal const long MaximumBundleBytes = 1024L * 1024 * 1024;
}