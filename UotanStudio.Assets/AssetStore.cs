namespace UotanStudio.Assets;

/// <summary>
/// Reads, materializes and extracts the bundled binary assets described by the embedded manifest.
/// <para>按内嵌清单读取、物化与抽取随包的二进制资源。</para>
/// </summary>
/// <remarks>
/// Every read decodes one zstd frame, checks the decoded length and SHA-256 against the manifest, and never returns
/// bytes that failed either check. Materialized bundles live in
/// <c>%TEMP%/UotanStudio/assets/&lt;bundle&gt;/&lt;bundle digest prefix&gt;</c>, so a changed bundle lands in a new
/// directory while unrelated bundles keep their cache.
/// <para>每次读取都会解码一个 zstd 帧，并按清单校验解码长度与 SHA-256，校验失败不会返回字节。物化后的资源包位于
/// <c>%TEMP%/UotanStudio/assets/&lt;bundle&gt;/&lt;bundle 摘要前缀&gt;</c>，因此改变某个资源包只会落到新目录，
/// 不影响其它资源包的缓存。</para>
/// </remarks>
public static class AssetStore
{
    private static readonly Lazy<AssetStoreInstance> Shared =
        new(CreateShared, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Decodes one asset into a fresh array after checking its length and SHA-256.
    /// <para>校验长度与 SHA-256 后把一个资源解码到新数组。</para>
    /// </summary>
    /// <param name="entry">Entry taken from <see cref="AssetCatalog"/>.
    /// <para>取自 <see cref="AssetCatalog"/> 的条目。</para></param>
    /// <returns>The verified payload; callers own the array. <para>已校验的负载；数组由调用方持有。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is <see langword="null"/>.
    /// <para><paramref name="entry"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">The frame is missing, truncated, corrupt, or does not match the
    /// manifest length and digest. <para>帧缺失、被截断、损坏，或与清单中的长度、摘要不符。</para></exception>
    public static byte[] Read(AssetEntry entry) => Shared.Value.Read(entry);

    /// <summary>Decodes one named file of a bundle into a fresh array.
    /// <para>把一个资源包中的指定文件解码到新数组。</para>
    /// </summary>
    /// <param name="bundle">Bundle taken from <see cref="AssetCatalog"/>.
    /// <para>取自 <see cref="AssetCatalog"/> 的资源包。</para></param>
    /// <param name="path">File name inside the bundle. <para>资源包内的文件名。</para></param>
    /// <returns>The verified payload; callers own the array. <para>已校验的负载；数组由调用方持有。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="bundle"/> is <see langword="null"/>.
    /// <para><paramref name="bundle"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="KeyNotFoundException">The bundle has no such file.
    /// <para>资源包中没有该文件。</para></exception>
    /// <exception cref="InvalidDataException">The frame is missing, truncated, corrupt, or does not match the
    /// manifest length and digest. <para>帧缺失、被截断、损坏，或与清单中的长度、摘要不符。</para></exception>
    public static byte[] Read(AssetBundle bundle, string path)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        return Shared.Value.Read(bundle[path]);
    }

    /// <summary>Writes every file of a bundle into its cache directory and returns that directory.
    /// <para>把资源包的每个文件写入其缓存目录并返回该目录。</para>
    /// </summary>
    /// <param name="bundle">Bundle taken from <see cref="AssetCatalog"/>.
    /// <para>取自 <see cref="AssetCatalog"/> 的资源包。</para></param>
    /// <param name="cancellationToken">Cancels the loop between entries.
    /// <para>在条目之间取消循环。</para></param>
    /// <returns>Absolute cache directory holding the materialized files; repeated calls reuse it and only rewrite
    /// files whose length no longer matches the manifest.
    /// <para>保存已物化文件的绝对缓存目录；重复调用会复用它，只重写长度与清单不符的文件。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="bundle"/> is <see langword="null"/>.
    /// <para><paramref name="bundle"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidOperationException">The bundle is not shipped in this build.
    /// <para>本次构建未随包该资源。</para></exception>
    /// <exception cref="InvalidDataException">A frame is missing, truncated, corrupt, or does not match the
    /// manifest length and digest. <para>某个帧缺失、被截断、损坏，或与清单中的长度、摘要不符。</para></exception>
    public static string Materialize(AssetBundle bundle, CancellationToken cancellationToken = default) =>
        Shared.Value.Materialize(bundle, cancellationToken);

    /// <summary>Writes every file of a bundle into an existing directory and returns the file names in manifest order.
    /// <para>把资源包的每个文件写入必须已存在的目录，并按清单顺序返回文件名。</para>
    /// </summary>
    /// <param name="bundle">Bundle taken from <see cref="AssetCatalog"/>.
    /// <para>取自 <see cref="AssetCatalog"/> 的资源包。</para></param>
    /// <param name="destinationDirectory">Directory that receives the files; it must already exist.
    /// <para>接收文件的目录；必须已存在。</para></param>
    /// <param name="cancellationToken">Cancels the loop between entries.
    /// <para>在条目之间取消循环。</para></param>
    /// <returns>The written file names, for example <c>rockusb.inf</c>.
    /// <para>已写入的文件名，例如 <c>rockusb.inf</c>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="bundle"/> is <see langword="null"/>.
    /// <para><paramref name="bundle"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="destinationDirectory"/> is empty or white space.
    /// <para><paramref name="destinationDirectory"/> 为空或空白。</para></exception>
    /// <exception cref="DirectoryNotFoundException"><paramref name="destinationDirectory"/> does not exist.
    /// <para><paramref name="destinationDirectory"/> 不存在。</para></exception>
    /// <exception cref="InvalidOperationException">The bundle is not shipped in this build.
    /// <para>本次构建未随包该资源。</para></exception>
    /// <exception cref="InvalidDataException">A frame is missing, truncated, corrupt, or does not match the
    /// manifest length and digest. <para>某个帧缺失、被截断、损坏，或与清单中的长度、摘要不符。</para></exception>
    public static IReadOnlyList<string> Extract(AssetBundle bundle, string destinationDirectory,
        CancellationToken cancellationToken = default) =>
        Shared.Value.Extract(bundle, destinationDirectory, cancellationToken);

    private static AssetStoreInstance CreateShared()
    {
        var assembly = typeof(AssetStore).Assembly;
        return new AssetStoreInstance(
            BundledManifest.Bundles,
            new EmbeddedAssetPayloadSource(assembly),
            Path.Combine(Path.GetTempPath(), "UotanStudio", "assets"));
    }
}