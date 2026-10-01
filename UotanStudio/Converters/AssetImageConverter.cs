using System.Collections.Concurrent;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace UotanStudio.Converters;

/// <summary>
/// <para>把资源地址字符串转换成图像，供 <c>Image.Source</c> 绑定使用。
/// Avalonia 不会在绑定里把普通字符串隐式转成 <see cref="IImage"/>（直接绑定会得到 null），
/// 因此本地资源头像必须经这个转换器；结果按地址缓存，避免每次重建位图。</para>
/// Converts an asset address string into an image for <c>Image.Source</c> bindings. Avalonia does not
/// implicitly turn a plain string into an <see cref="IImage"/> in a binding (the source stays null), so
/// asset-backed avatars need this converter. Results are cached per address.
/// </summary>
public sealed class AssetImageConverter : IValueConverter
{
    private readonly ConcurrentDictionary<string, IImage?> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// <para>把 <c>avares://</c> 或相对资源地址转成位图；地址为空或资源缺失时返回 null，界面保留占位底色。</para>
    /// Converts an <c>avares://</c> or relative asset address into a bitmap; empty addresses or missing
    /// assets yield null so the placeholder fill stays visible.
    /// </summary>
    /// <param name="value"><para>资源地址字符串。</para>The asset address string.</param>
    /// <param name="targetType"><para>目标类型。</para>The target type.</param>
    /// <param name="parameter"><para>转换参数（未使用）。</para>The converter parameter (unused).</param>
    /// <param name="culture"><para>区域信息（未使用）。</para>The culture (unused).</param>
    /// <returns><para>加载到的图像，或 null。</para>The loaded image, or null.</returns>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string address || string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        return _cache.GetOrAdd(address, LoadAsset);
    }

    /// <summary>
    /// <para>不支持反向转换。</para>
    /// Reverse conversion is not supported.
    /// </summary>
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