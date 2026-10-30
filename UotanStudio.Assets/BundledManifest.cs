namespace UotanStudio.Assets;

internal static class BundledManifest
{
    private static readonly Lazy<IReadOnlyDictionary<string, AssetBundle>> Parsed =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static IReadOnlyDictionary<string, AssetBundle> Bundles => Parsed.Value;

    internal static AssetBundle Get(string key) =>
        Bundles.TryGetValue(key, out var bundle) ? bundle : AssetBundle.Empty(key);

    private static IReadOnlyDictionary<string, AssetBundle> Load()
    {
        var assembly = typeof(BundledManifest).Assembly;
        var manifest = new EmbeddedAssetPayloadSource(assembly).Read(AssetManifest.ManifestResourceName);
        return AssetManifest.Parse(manifest);
    }
}