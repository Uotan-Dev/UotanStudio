using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using UotanStudio.Assets;

namespace UotanStudio.Fonts;

/// <summary>
/// A font collection that decodes bundled variable faces from the <see cref="AssetCatalog.Fonts"/> bundle
/// into memory once at startup and serves every requested weight by pinning the variable <c>wght</c> axis.
/// <para>启动时把随包可变字形从 <see cref="AssetCatalog.Fonts"/> 资源包一次性解码到内存，
/// 并按请求的字重固定可变字形的 <c>wght</c> 轴来提供全部字重。</para>
/// </summary>
/// <remarks>
/// Variable faces declare a standard 400 default. The override below maps each requested weight onto
/// <c>wght</c>, and preserves upright vs. italic selection when both faces are registered.
/// <para>可变字形的默认字重为标准的 400。下面的重写把每个请求字重映射到 <c>wght</c>，
/// 并在同时注册正体与斜体字形时保留样式选择。</para>
/// </remarks>
internal sealed class ZstdEmbeddedFontCollection : FontCollectionBase
{
    private static readonly Uri _uiKey = new(BundledFont.CollectionKey);
    private static readonly Uri _monospaceKey = new(BundledFont.MonospaceCollectionKey);
    private static readonly AssetEntry _uiFace = AssetCatalog.Fonts[AssetNames.Fonts.Variable];
    private static readonly AssetEntry _monospaceFace = AssetCatalog.Fonts[AssetNames.Fonts.Monospace];
    private static readonly AssetEntry _monospaceItalicFace = AssetCatalog.Fonts[AssetNames.Fonts.MonospaceItalic];

    private readonly Uri _key;
    private readonly int _minimumWeight;
    private readonly int _maximumWeight;
    private readonly int _faceCount;

    /// <summary>
    /// Registers the already-decoded MiSans variable face supplied by <see cref="DecompressFaces"/>.
    /// <para>注册由 <see cref="DecompressFaces"/> 预先解码好的 MiSans 可变字形。</para>
    /// </summary>
    /// <param name="decompressedFace">Decoded variable face bytes; cleared once registered.
    /// <para>解码后的可变字形字节；注册完成后会被清零。</para></param>
    /// <exception cref="InvalidDataException">The decoded buffer is not a usable glyph typeface.
    /// <para>解码缓冲区不是可用的字形字面。</para></exception>
    public ZstdEmbeddedFontCollection(byte[] decompressedFace)
        : this(_uiKey, BundledFont.MinimumWeight, BundledFont.MaximumWeight, [(_uiFace.Path, decompressedFace)])
    {
    }

    private ZstdEmbeddedFontCollection(
        Uri key,
        int minimumWeight,
        int maximumWeight,
        IReadOnlyList<(string AssetPath, byte[] Bytes)> decompressedFaces)
    {
        _key = key;
        _minimumWeight = minimumWeight;
        _maximumWeight = maximumWeight;
        var loaded = 0;
        try
        {
            foreach (var (assetPath, bytes) in decompressedFaces)
            {
                using var stream = new MemoryStream(bytes, writable: false);
                if (!TryAddGlyphTypeface(stream, out var glyphTypeface) || glyphTypeface is null)
                {
                    throw new InvalidDataException(
                        $"Bundled variable font asset '{assetPath}' does not contain a usable font face. 随包可变字体资产 '{assetPath}' 不包含可用的字体字形。");
                }

                loaded++;
            }
        }
        finally
        {
            foreach (var (_, bytes) in decompressedFaces)
            {
                Array.Clear(bytes);
            }
        }

        _faceCount = loaded;
    }

    /// <summary>
    /// The key that <c>fonts:</c> family URIs use to reach this collection.
    /// <para><c>fonts:</c> 字体系列 URI 用来访问本集合的键。</para>
    /// </summary>
    /// <value>The registered <c>fonts:</c> collection URI.<para>已注册的 <c>fonts:</c> 集合 URI。</para></value>
    public override Uri Key => _key;

    /// <summary>
    /// Reads and decodes the bundled MiSans variable face into a freshly allocated buffer. The work is pure and carries
    /// no UI-thread state, so callers may run it on a background thread and hand the result to the constructor.
    /// <para>读取并解码随包 MiSans 可变字形到一个独立分配的缓冲区。该过程是纯计算、不触碰 UI 线程状态，
    /// 调用方可在后台线程运行它，再把结果交给构造函数。</para>
    /// </summary>
    /// <returns>The decoded variable face bytes.<para>解码后的可变字形字节。</para></returns>
    /// <exception cref="TypeInitializationException">The <c>fonts</c> bundle of this build does not declare the
    /// variable face; the inner <see cref="KeyNotFoundException"/> names the missing file.
    /// <para>本次构建的 <c>fonts</c> 资源包缺少可变字形时类型初始化失败，内部的 <see cref="KeyNotFoundException"/> 会指明缺失的文件。</para></exception>
    /// <exception cref="InvalidDataException">The face frame is missing, truncated, corrupt, or does not match the
    /// manifest length and digest. <para>字形帧缺失、被截断、损坏，或与清单中的长度、摘要不符。</para></exception>
    public static byte[] DecompressFaces() => AssetStore.Read(_uiFace);

    /// <summary>
    /// Reads and decodes the bundled JetBrains Mono upright and italic variable faces and constructs their collection.
    /// <para>读取并解码随包 JetBrains Mono 正体与斜体可变字形并构造其字体集合。</para>
    /// </summary>
    /// <returns>The monospace font collection registered under <see cref="BundledFont.MonospaceCollectionKey"/>.
    /// <para>注册在 <see cref="BundledFont.MonospaceCollectionKey"/> 下的等宽字体集合。</para></returns>
    /// <exception cref="InvalidDataException">A monospace face frame is missing, corrupt, or not a usable font face.
    /// <para>等宽字形帧缺失、损坏或不是可用的字体字形。</para></exception>
    public static ZstdEmbeddedFontCollection CreateMonospaceCollection() =>
        new(
            _monospaceKey,
            BundledFont.MonospaceMinimumWeight,
            BundledFont.MonospaceMaximumWeight,
            [
                (_monospaceFace.Path, AssetStore.Read(_monospaceFace)),
                (_monospaceItalicFace.Path, AssetStore.Read(_monospaceItalicFace)),
            ]);

    /// <summary>
    /// Resolves a family request by cloning the variable face at the requested weight's <c>wght</c> coordinate,
    /// clamped to the axis range the bundled face declares.
    /// <para>通过把可变字形克隆到请求字重对应的 <c>wght</c> 坐标来解析字族请求，并夹取到随包字形声明的轴范围内。</para>
    /// </summary>
    /// <param name="familyName">Requested family name; the bundled faces answer for their own family only.
    /// <para>请求的字族名；随包字形只应答其自身字族。</para></param>
    /// <param name="style">Requested style. <para>请求的字形样式。</para></param>
    /// <param name="weight">Requested weight, mapped onto <c>wght</c>. <para>请求的字重，将映射到 <c>wght</c>。</para></param>
    /// <param name="stretch">Requested stretch. <para>请求的字形拉伸。</para></param>
    /// <param name="glyphTypeface">The variable face pinned to the requested weight, when resolved.
    /// <para>解析成功时按请求字重固定的可变字形。</para></param>
    /// <returns><c>true</c> when the family is served by this collection.
    /// <para>本集合可以应答该字族时返回 <c>true</c>。</para></returns>
    public override bool TryGetGlyphTypeface(
        string familyName,
        FontStyle style,
        FontWeight weight,
        FontStretch stretch,
        [NotNullWhen(true)] out GlyphTypeface? glyphTypeface)
    {
        glyphTypeface = null;
        if (_faceCount == 0)
        {
            return false;
        }

        var requestedStyle = style == FontStyle.Oblique ? FontStyle.Italic : style;
        if (!base.TryGetGlyphTypeface(familyName, requestedStyle, FontWeight.Normal, FontStretch.Normal, out var baseFace)
            && (requestedStyle == FontStyle.Normal
                || !base.TryGetGlyphTypeface(familyName, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out baseFace)))
        {
            return false;
        }

        if (baseFace is null)
        {
            return false;
        }

        var wght = Math.Clamp((int)weight, _minimumWeight, _maximumWeight);
        glyphTypeface = baseFace.WithVariations(FontVariationSettings.Parse(
            string.Create(CultureInfo.InvariantCulture, $"wght={wght}")));
        return true;
    }
}