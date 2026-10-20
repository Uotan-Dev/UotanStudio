using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UotanStudio.ViewModels;

namespace UotanStudio.Views.Controls;

/// <summary>
/// Hosts the Iconica control selected by a runtime <see cref="AppIconKind"/> (explorer nodes, compare
/// rows and generated menus). Static XAML uses the strongly-typed Iconica controls directly; this host exists only
/// where the glyph is data-bound. A <see cref="Viewbox"/> scales the native 24-unit icon to the host size without
/// losing vector rendering, and the inherited foreground still reaches the icon through the logical tree.
/// <para>承载按运行时 <see cref="AppIconKind"/> 选择的 Iconica 控件（资源管理器节点、对比行与生成菜单）。
/// 静态 XAML 直接使用强类型 Iconica 控件；只有图标由数据绑定时才用本宿主。
/// <see cref="Viewbox"/> 把原生 24 单位图标缩放到宿主尺寸且保持矢量渲染，继承的前景色仍会经逻辑树传到图标。</para>
/// </summary>
public sealed class AppIcon : ContentControl
{
    /// <summary>Identifies the <see cref="Kind"/> property. <para>标识 <see cref="Kind"/> 属性。</para></summary>
    public static readonly StyledProperty<AppIconKind?> KindProperty =
        AvaloniaProperty.Register<AppIcon, AppIconKind?>(nameof(Kind));

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
    public AppIconKind? Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    private void UpdateContent()
    {
        Content = Kind is { } kind
            ? new Viewbox
            {
                Child = AppIconFactory.Create(kind),
                Stretch = Stretch.Uniform,
            }
            : null;
    }
}