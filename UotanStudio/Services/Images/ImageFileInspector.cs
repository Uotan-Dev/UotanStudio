using Sdcb.SimdCodecs;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Png;
using UotanStudio.Firmware.Core.Magic;

namespace UotanStudio.Services.Images;

/// <summary>
/// Reads image attributes and builds a bounded preview: file size, codec header fields, compiled
/// file(1) magic recognition and an 8-bit RGBA rendering encoded as PNG for the viewer.
/// <para>读取图片属性并生成有界预览：文件大小、编解码头字段、编译型 file(1) 魔数识别，
/// 以及供查看器显示、由 8 位 RGBA 栅格编码的 PNG。</para>
/// </summary>
internal static class ImageFileInspector
{
    /// <summary>Upper bound for a file loaded into memory.<para>载入内存的文件大小上限。</para></summary>
    private const long MaximumFileBytes = 256L * 1024 * 1024;

    /// <summary>Upper bound for a decoded raster.<para>解码栅格的像素数上限。</para></summary>
    private const long MaximumPixels = 32L * 1024 * 1024;

    /// <summary>
    /// Inspects one image file without throwing: every failure is reported as a localized status key.
    /// <para>探测单个图片文件且不抛异常：所有失败都以本地化状态键上报。</para>
    /// </summary>
    /// <param name="path">Absolute or relative path of the image file.<para>图片文件的绝对或相对路径。</para></param>
    /// <param name="cancellationToken">Cancels the inspection and decode.<para>取消探测与解码。</para></param>
    /// <returns>The attributes and an optional PNG preview; never null.<para>属性与可选 PNG 预览；永不为 null。</para></returns>
    public static ImageFileSnapshot Inspect(string path, CancellationToken cancellationToken = default)
    {
        var properties = new ImageFileProperties { FilePath = path };
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path))
            {
                return new ImageFileSnapshot(properties, null, "Dock_ImageViewer_Error");
            }

            var fullPath = Path.GetFullPath(path);
            var file = new FileInfo(fullPath);
            if (!file.Exists)
            {
                return new ImageFileSnapshot(properties, null, "Dock_ImageViewer_Error");
            }

            if (file.Length > MaximumFileBytes)
            {
                return new ImageFileSnapshot(properties, null, "Dock_ImageViewer_TooLarge");
            }

            var bytes = File.ReadAllBytes(fullPath);
            cancellationToken.ThrowIfCancellationRequested();

            var magic = IdentifyMagic(bytes);
            var format = SimdCodecs.DetectFormat(bytes);
            properties = properties with
            {
                FilePath = fullPath,
                FileSize = bytes.LongLength,
                MagicDescription = magic?.Description ?? string.Empty,
                MimeType = magic?.MimeType ?? string.Empty,
                MagicExtensions = magic?.Extensions ?? string.Empty,
                CodecFormat = ImageFileCatalog.FormatName(format),
                Decodable = format != ImageFormat.Unknown && SimdCodecs.IsDecodable(format),
            };

            if (!SimdCodecs.TryReadInfo(bytes, out var info))
            {
                return new ImageFileSnapshot(properties, null, "Dock_ImageViewer_Unsupported");
            }

            properties = properties with
            {
                CodecFormat = ImageFileCatalog.FormatName(info.Format),
                Dimensions = $"{info.Width} x {info.Height}",
                Channels = info.Channels.ToString(),
                BitDepth = info.BitDepth.ToString(),
                SampleFormat = info.SampleFormat.ToString(),
                FrameCount = info.FrameCount.ToString(),
                Orientation = info.Orientation.ToString(),
            };

            var pixels = (long)info.Width * info.Height;
            if (info.Width <= 0 || info.Height <= 0 || pixels > MaximumPixels)
            {
                return new ImageFileSnapshot(properties, null, "Dock_ImageViewer_TooLarge");
            }

            var rgba = new byte[checked((int)(pixels * 4))];
            if (!SimdCodecs.TryDecodeIntoRgba8(bytes, rgba) && !TryStretchFloat(bytes, rgba))
            {
                return new ImageFileSnapshot(properties, null, "Dock_ImageViewer_Unsupported");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var preview = PngWriter.Encode(rgba, info.Width, info.Height);
            return new ImageFileSnapshot(properties, preview);
        }
        catch (OperationCanceledException)
        {
            return new ImageFileSnapshot(properties, null, "Dock_ImageViewer_Error");
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Debug, "ImageViewer", $"Failed to inspect '{path}': {ex.Message}");
            return new ImageFileSnapshot(properties, null, "Dock_ImageViewer_Error");
        }
    }

    /// <summary>
    /// Projects float or 16-bit rasters onto 8-bit RGBA by stretching the finite colour range; used when
    /// the facade refuses an 8-bit projection because a tone decision is required.
    /// <para>把浮点或 16 位栅格按有限色彩区间线性拉伸到 8 位 RGBA；门面因需要色调决策而拒绝
    /// 8 位投影时使用此路径。</para>
    /// </summary>
    private static bool TryStretchFloat(byte[] bytes, byte[] destination)
    {
        if (!SimdCodecs.TryDecode(bytes, out var image) || image is null)
        {
            return false;
        }

        var floats = image.ToFloats();
        if (floats.Length < destination.Length)
        {
            return false;
        }

        var min = float.MaxValue;
        var max = float.MinValue;
        for (var index = 0; index < destination.Length; index++)
        {
            if ((index & 3) == 3)
            {
                continue;
            }

            min = Math.Min(min, floats[index]);
            max = Math.Max(max, floats[index]);
        }

        var scale = max > min ? 255f / (max - min) : 0f;
        for (var index = 0; index < destination.Length; index++)
        {
            destination[index] = (index & 3) == 3
                ? (byte)255
                : (byte)Math.Clamp((floats[index] - min) * scale, 0f, 255f);
        }

        return true;
    }

    /// <summary>
    /// Runs the compiled file(1) magic engine over the already-loaded bytes; an unavailable rule table
    /// degrades to no magic metadata instead of failing the whole inspection.
    /// <para>对已载入字节运行编译型 file(1) 魔数引擎；规则表不可用时降级为无魔数元数据，不使整次探测失败。</para>
    /// </summary>
    private static MagicInfo? IdentifyMagic(byte[] bytes)
    {
        try
        {
            return FileMagic.Identify(bytes);
        }
        catch (Exception)
        {
            return null;
        }
    }
}