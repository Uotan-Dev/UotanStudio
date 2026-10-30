using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Assets;

/// <summary>
/// An immutable set of entries that materialize into one directory, identified by its key and a digest of its rows.
/// <para>一组物化到同一目录的不可变条目，由键与其行的摘要标识。</para>
/// </summary>
public sealed class AssetBundle
{
    private readonly IReadOnlyList<AssetEntry> _entries;

    internal AssetBundle(string key, IEnumerable<AssetEntry> entries)
    {
        Key = key;
        var ordered = entries.OrderBy(static entry => entry.Path, StringComparer.Ordinal).ToArray();
        _entries = new ReadOnlyCollection<AssetEntry>(ordered);
        Digest = ordered.Length == 0 ? string.Empty : ComputeDigest(key, ordered);
    }

    /// <summary>Bundle key, which is also its directory below the asset root.
    /// <para>资源包键，同时是它在资源根下的目录。</para>
    /// </summary>
    public string Key { get; }

    /// <summary>Entries in ordinal path order.
    /// <para>按路径序数排序的条目。</para>
    /// </summary>
    public IReadOnlyList<AssetEntry> Entries => _entries;

    /// <summary>Lowercase hexadecimal SHA-256 over the bundle's canonical rows; empty when the bundle is absent.
    /// <para>资源包规范行的小写十六进制 SHA-256；资源包不存在时为空串。</para>
    /// </summary>
    /// <remarks>The digest ignores unrelated bundles, so adding an asset to one bundle never invalidates another
    /// bundle's cache directory. <para>摘要不含其它资源包，因此给一个资源包增加资源不会让别的资源包缓存目录失效。</para></remarks>
    public string Digest { get; }

    /// <summary>Whether any entry belongs to this build; a bundle that is not shipped stays empty.
    /// <para>本次构建是否包含任何条目；未随包的资源包为空。</para>
    /// </summary>
    public bool IsPresent => _entries.Count > 0;

    /// <summary>Returns the entry with the given file name.
    /// <para>返回指定文件名的条目。</para>
    /// </summary>
    /// <param name="path">File name inside the bundle, for example <c>rockusb.inf</c>.
    /// <para>资源包内的文件名，例如 <c>rockusb.inf</c>。</para></param>
    /// <returns>The matching entry, which callers pass to <see cref="AssetStore.Read(AssetEntry)"/>.
    /// <para>匹配的条目，调用方把它交给 <see cref="AssetStore.Read(AssetEntry)"/>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.
    /// <para><paramref name="path"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="KeyNotFoundException">The bundle contains no such file, which includes every bundle that
    /// is not shipped in this build. <para>资源包中没有该文件，本次构建未随包的资源包同样如此。</para></exception>
    public AssetEntry this[string path]
    {
        get
        {
            ArgumentNullException.ThrowIfNull(path);
            foreach (var entry in _entries)
            {
                if (string.Equals(entry.Path, path, StringComparison.Ordinal))
                {
                    return entry;
                }
            }

            throw new KeyNotFoundException(
                $"The asset bundle '{Key}' has no file '{path}'. 资源包 '{Key}' 中没有文件 '{path}'。");
        }
    }

    internal static AssetBundle Empty(string key) => new(key, []);

    private static string ComputeDigest(string key, AssetEntry[] entries)
    {
        var rows = new StringBuilder();
        foreach (var entry in entries)
        {
            rows.Append(key).Append('\t')
                .Append(entry.Path).Append('\t')
                .Append(entry.Length.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(entry.Sha256).Append('\n');
        }

        return Sha256.ComputeHexLower(Encoding.UTF8.GetBytes(rows.ToString()));
    }
}