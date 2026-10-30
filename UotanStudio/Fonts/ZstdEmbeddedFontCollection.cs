using Avalonia.Media.Fonts;
using UotanStudio.Assets;

namespace UotanStudio.Fonts;

/// <summary>
/// A font collection that decodes the bundled MiSans faces from the <see cref="AssetCatalog.Fonts"/> bundle into
/// memory once at startup and registers them under a single collection key.
/// <para>启动时把随包的 MiSans 字形从 <see cref="AssetCatalog.Fonts"/> 资源包一次性解码到内存，并以同一个集合键注册它们的字体集合。</para>
/// </summary>
internal sealed class ZstdEmbeddedFontCollection : FontCollectionBase
{
    private static readonly Uri _key = new("fonts:UotanStudio/MiSans");
    private static readonly (string Label, AssetEntry Entry)[] _faces =
    [
        ("Regular", AssetCatalog.Fonts[AssetNames.Fonts.Regular]),
        ("Bold", AssetCatalog.Fonts[AssetNames.Fonts.Bold]),
    ];

    /// <summary>
    /// Registers the already-decoded faces supplied by <see cref="DecompressFaces"/>.
    /// <para>注册由 <see cref="DecompressFaces"/> 预先解码好的字形。</para>
    /// </summary>
    /// <param name="decompressedFaces">Decoded face bytes, aligned index-by-index with <see cref="_faces"/>.
    /// <para>解码后的字形字节，按顺序与 <see cref="_faces"/> 一一对应。</para></param>
    /// <exception cref="InvalidDataException">A decoded buffer is not a usable glyph typeface.
    /// <para>某个解码缓冲区不是可用的字形字面。</para></exception>
    public ZstdEmbeddedFontCollection(byte[][] decompressedFaces)
    {
        for (var i = 0; i < decompressedFaces.Length; i++)
        {
            RegisterFace(_faces[i], decompressedFaces[i]);
        }
    }

    /// <summary>
    /// The key that <c>fonts:</c> family URIs use to reach this collection.
    /// <para><c>fonts:</c> 字体系列 URI 用来访问本集合的键。</para>
    /// </summary>
    /// <value>The <c>fonts:UotanStudio/MiSans</c> key.<para><c>fonts:UotanStudio/MiSans</c> 键。</para></value>
    public override Uri Key => _key;

    /// <summary>
    /// Reads and decodes every bundled face into freshly allocated buffers. The work is pure and carries no
    /// UI-thread state, so callers may run it on a background thread and hand the result to the constructor.
    /// <para>读取并解码每个随包字形到独立分配的缓冲区。该过程是纯计算、不触碰 UI 线程状态，调用方可在后台线程
    /// 运行它，再把结果交给构造函数。</para>
    /// </summary>
    /// <returns>Decoded face bytes, aligned index-by-index with <see cref="_faces"/>.
    /// <para>解码后的字形字节，按顺序与 <see cref="_faces"/> 一一对应。</para></returns>
    /// <exception cref="TypeInitializationException">The <c>fonts</c> bundle of this build does not declare one of
    /// the listed faces; the inner <see cref="KeyNotFoundException"/> names the missing file.
    /// <para>本次构建的 <c>fonts</c> 资源包缺少所列字形时类型初始化失败，内部的 <see cref="KeyNotFoundException"/>
    /// 会指明缺失的文件。</para></exception>
    /// <exception cref="InvalidDataException">A face frame is missing, truncated, corrupt, or does not match the
    /// manifest length and digest. <para>某个字形帧缺失、被截断、损坏，或与清单中的长度、摘要不符。</para></exception>
    public static byte[][] DecompressFaces()
    {
        var faces = new byte[_faces.Length][];
        for (var i = 0; i < _faces.Length; i++)
        {
            faces[i] = AssetStore.Read(_faces[i].Entry);
        }

        return faces;
    }

    /// <summary>
    /// Adds one decoded face to the collection and then releases the decoded buffer.
    /// <para>将一个已解码字形加入集合，随后释放解码缓冲区。</para>
    /// </summary>
    /// <param name="face">Entry of one declared face; its path appears in failure messages.
    /// <para>某个已声明字形的条目；其路径会出现在失败消息中。</para></param>
    /// <param name="decompressed">The decoded face bytes to register; cleared on completion.
    /// <para>要注册的解码字形字节；完成后会被清零。</para></param>
    /// <exception cref="InvalidDataException">The decoded buffer is not a usable font face.
    /// <para>解码缓冲区不是可用的字体字形。</para></exception>
    private void RegisterFace((string Label, AssetEntry Entry) face, byte[] decompressed)
    {
        try
        {
            using var stream = new MemoryStream(decompressed, writable: false);
            if (!TryAddGlyphTypeface(stream, out _))
            {
                throw new InvalidDataException(
                    $"Bundled {face.Label} font asset '{face.Entry.Path}' does not contain a usable font face. 随包 {face.Label} 字体资产 '{face.Entry.Path}' 不包含可用的字体字形。");
            }
        }
        finally
        {
            Array.Clear(decompressed);
        }
    }
}