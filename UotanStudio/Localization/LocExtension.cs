using Avalonia.Data;
using Avalonia.Markup.Xaml;
using System;
using System.Diagnostics.CodeAnalysis;

namespace UotanStudio.Localization;

/// <summary>
/// <para>提供 XAML 标记扩展，用于绑定本地化字符串并在语言切换时自动更新。</para>
/// A XAML markup extension that binds to localized strings and auto-updates when the language changes.
/// </summary>
/// <example>
/// <para>在 XAML 中使用：</para>
/// Usage in XAML:
/// <code>&lt;TextBlock Text="{i18n:Loc Menu_Project}" /&gt;</code>
/// </example>
public class LocExtension : MarkupExtension
{
    /// <summary>
    /// <para>获取或设置资源键名。</para>
    /// Gets or sets the resource key name.
    /// </summary>
    public string Key { get; set; }

    /// <summary>
    /// <para>使用指定的资源键初始化 LocExtension 的新实例。</para>
    /// Initializes a new instance of LocExtension with the specified resource key.
    /// </summary>
    /// <param name="key">
    /// <para>资源键名。</para>
    /// The resource key name.
    /// </param>
    public LocExtension(string key)
    {
        Key = key;
    }

    /// <summary>
    /// <para>提供绑定到本地化字符串的值。返回一个到 LocalizationService 的数据绑定，支持运行时语言切换时自动更新。</para>
    /// Provides a value bound to the localized string. Returns a data binding to LocalizationService that auto-updates on language change.
    /// </summary>
    /// <param name="serviceProvider">
    /// <para>服务提供者。</para>
    /// The service provider.
    /// </param>
    /// <returns>
    /// <para>绑定到 LocalizationService 索引器的 Binding 实例。</para>
    /// A Binding instance bound to the LocalizationService indexer.
    /// </returns>
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "Avalonia Binding is AOT-compatible when used with known source types")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "Avalonia Binding is AOT-compatible when used with known source types")]
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding
        {
            Source = LocalizationService.Instance,
            Path = $"[{Key}]",
            Mode = BindingMode.OneWay,
        };
        return binding;
    }
}
