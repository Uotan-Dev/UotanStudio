using System.Collections.Concurrent;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace UotanStudio.Converters;

/// <summary>
/// Converts asset addresses into images for binding targets and caches each address so its bitmap
/// is decoded only once.
/// <para>把资源地址转换为绑定目标可用的图像，并按地址缓存，使位图仅解码一次。</para>
/// </summary>
public sealed class AssetImageConverter : IValueConverter
{
    private readonly ConcurrentDictionary<string, IImage?> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Converts an <c>avares://</c> or relative asset address into a bitmap; empty addresses and
    /// missing assets yield null so the placeholder fill stays visible.
    /// <para>把 <c>avares://</c> 或相对资源地址转换为位图；地址为空或资源缺失时返回 null，占位底色保持可见。</para>
    /// </summary>
    /// <param name="value">The asset address string. <para>资源地址字符串。</para></param>
    /// <param name="targetType">The target type of the binding. <para>绑定的目标类型。</para></param>
    /// <param name="parameter">The converter parameter, unused. <para>转换参数，未使用。</para></param>
    /// <param name="culture">The culture information, unused. <para>区域信息，未使用。</para></param>
    /// <returns>The cached image for the address, or null. <para>该地址对应的缓存图像，或 null。</para></returns>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string address || string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        return _cache.GetOrAdd(address, LoadAsset);
    }

    /// <summary>
    /// Reverse conversion is not supported.
    /// <para>不支持反向转换。</para>
    /// </summary>
    /// <param name="value">The converted value, ignored. <para>已转换的值，忽略。</para></param>
    /// <param name="targetType">The target type of the binding, ignored. <para>绑定的目标类型，忽略。</para></param>
    /// <param name="parameter">The converter parameter, ignored. <para>转换参数，忽略。</para></param>
    /// <param name="culture">The culture information, ignored. <para>区域信息，忽略。</para></param>
    /// <returns>An unset marker leaving the target value unchanged. <para>使目标值保持不变的未设置标记。</para></returns>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Avalonia.AvaloniaProperty.UnsetValue;

    private static IImage? LoadAsset(string address)
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(address, UriKind.RelativeOrAbsolute));
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }
}