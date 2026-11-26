using System.Collections.Frozen;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using UotanStudio.ViewModels;

namespace UotanStudio.Views.Controls;

/// <summary>
/// Hosts the icon selected by a runtime <see cref="AppIconKind"/> (explorer nodes, compare rows and generated
/// menus). Most kinds use the Iconica vector control; the nine associated file formats use the raster artwork
/// shipped with the application. A <see cref="Viewbox"/> scales the native 24-unit vector to the host size,
/// while raster icons render as a plain <see cref="Image"/> with uniform stretching.
/// <para>承载按运行时 <see cref="AppIconKind"/> 选择的图标（资源管理器节点、对比行与生成菜单）。
/// 大多数标识使用 Iconica 矢量控件；九种已关联文件格式使用随应用分发的位图图案。
/// <see cref="Viewbox"/> 把 24 单位矢量缩放到宿主尺寸，位图图标则以统一拉伸的 <see cref="Image"/> 渲染。</para>
/// </summary>
public sealed partial class AppIcon : ContentControl
{
    private static readonly FrozenDictionary<AppIconKind, string> FormatIconAssets =
        new Dictionary<AppIconKind, string>
        {
            [AppIconKind.FileApk] = "apk",
            [AppIconKind.FileArsc] = "arsc",
            [AppIconKind.FileDex] = "dex",
            [AppIconKind.FileHap] = "hap",
            [AppIconKind.FileLog] = "log",
            [AppIconKind.FileProp] = "prop",
            [AppIconKind.FileQcn] = "qcn",
            [AppIconKind.FileRc] = "rc",
            [AppIconKind.FileImg] = "img",
        }.ToFrozenDictionary();

    private static readonly Dictionary<AppIconKind, Bitmap?> FormatIconBitmaps = new();

    private static Bitmap? GetFormatBitmap(AppIconKind kind)
    {
        lock (FormatIconBitmaps)
        {
            if (FormatIconBitmaps.TryGetValue(kind, out var cached))
            {
                return cached;
            }

            Bitmap? bitmap = null;
            if (FormatIconAssets.TryGetValue(kind, out var name))
            {
                try
                {
                    bitmap = new Bitmap(AssetLoader.Open(
                        new Uri($"avares://UotanStudio/Assets/{name}.png")));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    bitmap = null;
                }
            }

            FormatIconBitmaps[kind] = bitmap;
            return bitmap;
        }
    }

    static AppIcon()
    {
        HorizontalContentAlignmentProperty.OverrideDefaultValue<AppIcon>(HorizontalAlignment.Stretch);
        VerticalContentAlignmentProperty.OverrideDefaultValue<AppIcon>(VerticalAlignment.Stretch);
        FocusableProperty.OverrideDefaultValue<AppIcon>(false);
        IsTabStopProperty.OverrideDefaultValue<AppIcon>(false);
        KindProperty.Changed.AddClassHandler<AppIcon>((icon, _) => icon.UpdateContent());
    }

    /// <summary>Uses the base ContentControl theme so the host gets the content presenter template. <para>沿用 ContentControl 主题，使宿主获得内容呈现器模板。</para></summary>
    protected override Type StyleKeyOverride => typeof(ContentControl);

    /// <summary>
    /// The semantic icon shown by this host; <c>null</c> renders nothing.
    /// <para>本宿主显示的语义图标；<c>null</c> 时不渲染内容。</para>
    /// </summary>
    /// <value>The icon selected by the owning view model or binding. <para>由所属视图模型或绑定选择的图标。</para></value>
    [GeneratedStyledProperty]
    public partial AppIconKind? Kind { get; set; }

    private void UpdateContent()
    {
        Content = Kind switch
        {
            null => null,
            { } kind when GetFormatBitmap(kind) is { } bitmap => new Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
            },
            { } kind => new Viewbox
            {
                Child = AppIconFactory.Create(kind),
                Stretch = Stretch.Uniform,
            },
        };
    }
}