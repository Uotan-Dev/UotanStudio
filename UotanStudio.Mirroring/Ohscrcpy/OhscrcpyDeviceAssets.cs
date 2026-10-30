using UotanStudio.Assets;

namespace UotanStudio.Mirroring.Ohscrcpy;

/// <summary>
/// Materializes the bundled OpenHarmony device runtime through <see cref="AssetStore"/> and exposes its cache directory.
/// <para>经 <see cref="AssetStore"/> 物化随包的鸿蒙设备运行时并给出其缓存目录。</para>
/// </summary>
internal static class OhscrcpyDeviceAssets
{
    private static readonly Lazy<string> Materialized =
        new(static () => AssetStore.Materialize(AssetCatalog.OhosDevice), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Cache directory holding the device runtime materialized from <see cref="AssetCatalog.OhosDevice"/>.
    /// <para>保存由 <see cref="AssetCatalog.OhosDevice"/> 物化的设备运行时的缓存目录。</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">The bundle is not shipped in this build.
    /// <para>本次构建未随包该资源包。</para></exception>
    /// <exception cref="InvalidDataException">A frame is missing, truncated, corrupt, or does not match the manifest
    /// length and digest. <para>某个帧缺失、被截断、损坏，或与清单中的长度、摘要不符。</para></exception>
    internal static string RuntimeDirectory => Materialized.Value;
}