using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using Sdcb.SimdCodecs;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Heif;
using UotanStudio.Firmware.Core.Magic;

namespace UotanStudio.Services.Images;

/// <summary>
/// Extension and signature catalogue for the image viewer: the single source of truth for which file
/// suffixes belong to the image browser and for magic-byte format recognition.
/// <para>图片查看器的扩展名与签名目录：统一维护归入图片浏览器的后缀集合与魔数格式识别。</para>
/// </summary>
internal static class ImageFileCatalog
{
    /// <summary>
    /// Enables the opt-in HEIF decoder before any facade lookup. HEVC is patent encumbered, so the
    /// facade never references this codec; the host registers it explicitly here.
    /// <para>在任何门面查询前启用可选 HEIF 解码器。HEVC 受专利约束，门面不引用该编解码器，由宿主在此显式注册。</para>
    /// </summary>
    static ImageFileCatalog()
    {
        try
        {
            RegisterOptionalCodecs();
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Registers the opt-in codec in its own method so an assembly-load failure is isolated from the
    /// type initializer and the built-in decoders remain usable.
    /// <para>在独立方法中注册可选编解码器，使程序集加载失败与类型初始化隔离，内置解码器仍可用。</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RegisterOptionalCodecs() => ImageCodecs.Register(HeifImageDecoder.Registration);

    /// <summary>Extension to codec-format table; entries whose format the facade cannot decode are kept so the viewer can name them.</summary>
    private static readonly FrozenDictionary<string, ImageFormat> _formats =
        new Dictionary<string, ImageFormat>(StringComparer.OrdinalIgnoreCase)
        {
            [".jpg"] = ImageFormat.Jpeg,
            [".jpeg"] = ImageFormat.Jpeg,
            [".jpe"] = ImageFormat.Jpeg,
            [".jfif"] = ImageFormat.Jpeg,
            [".jif"] = ImageFormat.Jpeg,
            [".jfi"] = ImageFormat.Jpeg,
            [".pjpeg"] = ImageFormat.Jpeg,
            [".pjp"] = ImageFormat.Jpeg,
            [".mpo"] = ImageFormat.Jpeg,
            [".png"] = ImageFormat.Png,
            [".apng"] = ImageFormat.Png,
            [".webp"] = ImageFormat.Webp,
            [".gif"] = ImageFormat.Gif,
            [".bmp"] = ImageFormat.Bmp,
            [".dib"] = ImageFormat.Bmp,
            [".rle"] = ImageFormat.Bmp,
            [".ico"] = ImageFormat.Ico,
            [".cur"] = ImageFormat.Cur,
            [".tif"] = ImageFormat.Tiff,
            [".tiff"] = ImageFormat.Tiff,
            [".ptif"] = ImageFormat.Tiff,
            [".ptiff"] = ImageFormat.Tiff,
            [".qoi"] = ImageFormat.Qoi,
            [".pbm"] = ImageFormat.Pnm,
            [".pgm"] = ImageFormat.Pnm,
            [".ppm"] = ImageFormat.Pnm,
            [".pnm"] = ImageFormat.Pnm,
            [".pam"] = ImageFormat.Pnm,
            [".pfm"] = ImageFormat.Pfm,
            [".jp2"] = ImageFormat.Jpeg2000,
            [".j2k"] = ImageFormat.Jpeg2000,
            [".jpf"] = ImageFormat.Jpeg2000,
            [".jpx"] = ImageFormat.Jpeg2000,
            [".jpm"] = ImageFormat.Jpeg2000,
            [".j2c"] = ImageFormat.Jpeg2000,
            [".jpc"] = ImageFormat.Jpeg2000,
            [".jpt"] = ImageFormat.Jpeg2000,
            [".jb2"] = ImageFormat.Jbig2,
            [".jbig2"] = ImageFormat.Jbig2,
            [".jbig"] = ImageFormat.Jbig2,
            [".bid"] = ImageFormat.Jbig2,
            [".heic"] = ImageFormat.Heif,
            [".heif"] = ImageFormat.Heif,
            [".hif"] = ImageFormat.Heif,
            [".heix"] = ImageFormat.Heif,
            [".hevc"] = ImageFormat.Heif,
            [".heim"] = ImageFormat.Heif,
            [".heis"] = ImageFormat.Heif,
            [".avif"] = ImageFormat.Avif,
            [".avifs"] = ImageFormat.Avif,
            [".jxl"] = ImageFormat.JpegXl,
            [".jxr"] = ImageFormat.JpegXr,
            [".wdp"] = ImageFormat.JpegXr,
            [".hdp"] = ImageFormat.JpegXr,
            [".exr"] = ImageFormat.OpenExr,
            [".psd"] = ImageFormat.Psd,
            [".psb"] = ImageFormat.Psd,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>All suffixes routed to the image viewer, in declaration order.<para>归入图片浏览器的全部后缀，按声明顺序。</para></summary>
    /// <value>The registered suffixes, each including its leading dot.<para>带前导点的已登记后缀。</para></value>
    public static IReadOnlyCollection<string> Extensions => _formats.Keys;

    /// <summary>
    /// Reports whether an extension belongs to the image browser.
    /// <para>判断某个扩展名是否归入图片浏览器。</para>
    /// </summary>
    /// <param name="extension">Extension with its leading dot.<para>带前导点的扩展名。</para></param>
    /// <returns>True for a registered image suffix.<para>已登记的图片后缀返回 true。</para></returns>
    public static bool IsImageExtension(string? extension) =>
        !string.IsNullOrEmpty(extension) && _formats.ContainsKey(extension);

    /// <summary>
    /// Reports whether a path carries an image suffix; it never touches the file system.
    /// <para>判断路径是否带图片后缀；不访问文件系统。</para>
    /// </summary>
    /// <param name="path">File name or full path.<para>文件名或完整路径。</para></param>
    /// <returns>True for a registered image suffix.<para>已登记的图片后缀返回 true。</para></returns>
    public static bool HasImageExtension(string? path) =>
        !string.IsNullOrEmpty(path) && IsImageExtension(Path.GetExtension(path));

    /// <summary>
    /// Returns the codec format declared for an extension, or <see cref="ImageFormat.Unknown"/>.
    /// <para>返回扩展名声明的编解码格式，未登记时为 <see cref="ImageFormat.Unknown"/>。</para>
    /// </summary>
    /// <param name="extension">Extension with its leading dot.<para>带前导点的扩展名。</para></param>
    /// <returns>The declared image format.<para>声明的图片格式。</para></returns>
    public static ImageFormat FormatForExtension(string? extension) =>
        extension is { Length: > 0 } && _formats.TryGetValue(extension, out var format)
            ? format
            : ImageFormat.Unknown;

    /// <summary>
    /// Recognises a decodable image from its leading bytes, using the codec facade's signature table
    /// and, for streams a registered codec claims, the compiled file(1) magic MIME type as a second opinion.
    /// <para>按文件头识别可解码图片：先走编解码门面的签名表，对已被注册编解码器认领的流再用编译型
    /// file(1) 魔数的 MIME 类型作二次确认。</para>
    /// </summary>
    /// <param name="header">Leading file bytes; at least 64 bytes are needed for ISO-BMFF brands.<para>文件头字节；ISO-BMFF brand 至少需要 64 字节。</para></param>
    /// <returns>True when the header belongs to a decodable image format.<para>文件头属于可解码图片格式时返回 true。</para></returns>
    public static bool IsDecodableImageHeader(ReadOnlySpan<byte> header)
    {
        if (header.IsEmpty)
        {
            return false;
        }

        var format = ImageCodecs.DetectFormat(header);
        if (format != ImageFormat.Unknown && ImageCodecs.IsDecodable(format))
        {
            return true;
        }

        if (!ImageCodecs.CanDecode(header))
        {
            return false;
        }

        try
        {
            return FileMagic.Identify(header)?.MimeType?
                .StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns a stable display name for a codec format.
    /// <para>返回编解码格式的稳定显示名。</para>
    /// </summary>
    /// <param name="format">The format to name.<para>要命名的格式。</para></param>
    /// <returns>The format name, or an empty string for <see cref="ImageFormat.Unknown"/>.<para>格式名；<see cref="ImageFormat.Unknown"/> 返回空串。</para></returns>
    public static string FormatName(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => "JPEG",
        ImageFormat.Png => "PNG",
        ImageFormat.Webp => "WebP",
        ImageFormat.Gif => "GIF",
        ImageFormat.Bmp => "BMP",
        ImageFormat.Ico => "ICO",
        ImageFormat.Cur => "CUR",
        ImageFormat.Tiff => "TIFF",
        ImageFormat.Qoi => "QOI",
        ImageFormat.Pnm => "Netpbm",
        ImageFormat.Pfm => "PFM",
        ImageFormat.Jpeg2000 => "JPEG 2000",
        ImageFormat.Jbig2 => "JBIG2",
        ImageFormat.Heif => "HEIF/HEIC",
        ImageFormat.Avif => "AVIF",
        ImageFormat.JpegXl => "JPEG XL",
        ImageFormat.JpegXr => "JPEG XR",
        ImageFormat.OpenExr => "OpenEXR",
        ImageFormat.Psd => "PSD",
        _ => string.Empty,
    };
}