using System.Reflection;
using Avalonia.Media.Fonts;
using UotanStudio.Compress;
using UotanStudio.Compress.Codecs.Zstd;

namespace UotanStudio.Fonts;

/// <summary>
/// A font collection that decompresses the bundled MiSans faces from manifest resources into memory
/// once at startup and registers them under a single collection key.
/// <para>在启动时把随包的 MiSans 字形从清单资源一次性解压到内存，并以同一个集合键注册它们的字体集合。</para>
/// </summary>
internal sealed class ZstdEmbeddedFontCollection : FontCollectionBase
{
    private static readonly Uri _key = new("fonts:UotanStudio/MiSans");
    private static readonly (string ResourceName, int UncompressedLength)[] _faces =
    [
        (MiSansFontData.RegularResourceName, MiSansFontData.RegularUncompressedLength),
        (MiSansFontData.BoldResourceName, MiSansFontData.BoldUncompressedLength),
    ];

    /// <summary>
    /// Loads both bundled faces and registers them in this collection.
    /// <para>载入随包的两个字形并注册到本集合。</para>
    /// </summary>
    /// <exception cref="InvalidDataException">A face resource is missing, does not decompress to its expected
    /// length, or does not contain a usable glyph typeface.
    /// <para>某个字形资源缺失、没有解压为预期长度，或不包含可用的字形字面。</para></exception>
    public ZstdEmbeddedFontCollection()
    {
        var assembly = typeof(ZstdEmbeddedFontCollection).Assembly;
        foreach (var (resourceName, uncompressedLength) in _faces)
        {
            LoadFace(assembly, resourceName, uncompressedLength);
        }
    }

    /// <summary>
    /// The key that <c>fonts:</c> family URIs use to reach this collection.
    /// <para><c>fonts:</c> 字体系列 URI 用来访问本集合的键。</para>
    /// </summary>
    /// <value>The <c>fonts:UotanStudio/MiSans</c> key.<para><c>fonts:UotanStudio/MiSans</c> 键。</para></value>
    public override Uri Key => _key;

    /// <summary>
    /// Decompresses one face, adds it to the collection, and then releases the decoded buffer.
    /// <para>解压一个字形、将其加入集合，随后释放解码缓冲区。</para>
    /// </summary>
    /// <param name="assembly">The assembly carrying the manifest resource.<para>承载清单资源的程序集。</para></param>
    /// <param name="resourceName">The logical resource name of one zstd frame.<para>一个 zstd 帧的逻辑资源名。</para></param>
    /// <param name="uncompressedLength">The exact decoded length the frame must produce.<para>该帧必须产出的精确解码长度。</para></param>
    /// <exception cref="InvalidDataException">The resource is missing, malformed, or not a usable font face.
    /// <para>资源缺失、格式无效，或不包含可用的字体字形。</para></exception>
    private void LoadFace(Assembly assembly, string resourceName, int uncompressedLength)
    {
        using var resource = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidDataException(
                $"Embedded font resource '{resourceName}' was not found. 未找到内嵌字体资源 '{resourceName}'。");
        using var compressed = new MemoryStream();
        resource.CopyTo(compressed);
        var decompressed = DecompressFace(
            resourceName, compressed.GetBuffer().AsSpan(0, (int)compressed.Length), uncompressedLength);
        try
        {
            using var face = new MemoryStream(decompressed, writable: false);
            if (!TryAddGlyphTypeface(face, out _))
            {
                throw new InvalidDataException(
                    $"Embedded font resource '{resourceName}' does not contain a usable font face. 内嵌字体资源 '{resourceName}' 不包含可用的字体字形。");
            }
        }
        finally
        {
            // Avalonia copies the face into unmanaged memory, so the decoded 8 MB buffer can be cleared.
            // Avalonia 已把字形复制到非托管内存，因此解码得到的 8 MB 缓冲区可以清除。
            Array.Clear(decompressed);
        }
    }

    /// <summary>
    /// Decodes one zstd frame and rejects a frame that does not produce the declared length.
    /// <para>解码一个 zstd 帧，并拒绝未产出声明长度的帧。</para>
    /// </summary>
    /// <param name="resourceName">The logical resource name named by the failure message.<para>失败消息中点名的逻辑资源名。</para></param>
    /// <param name="compressed">The complete zstd frame bytes.<para>完整的 zstd 帧字节。</para></param>
    /// <param name="uncompressedLength">The exact decoded length the frame must produce.<para>该帧必须产出的精确解码长度。</para></param>
    /// <returns>The decoded face bytes.<para>解码后的字形字节。</para></returns>
    /// <exception cref="InvalidDataException">The frame is malformed or decodes to a different length.
    /// <para>帧无效或解码长度不符。</para></exception>
    private static byte[] DecompressFace(string resourceName, ReadOnlySpan<byte> compressed, int uncompressedLength)
    {
        try
        {
            return ZstdBlockCodec.Decompress(compressed, uncompressedLength);
        }
        catch (CompressionException ex)
        {
            throw new InvalidDataException(
                $"Embedded font resource '{resourceName}' could not be decompressed to {uncompressedLength} bytes. 内嵌字体资源 '{resourceName}' 无法解压为 {uncompressedLength} 字节。",
                ex);
        }
    }
}