using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Services;
using UotanStudio.Services.Flash;

namespace UotanStudio.ViewModels.Shell;

/// <summary>
/// Editable display preference for one protocol section in the flash settings page.
/// <para>烧录设置页面中单个协议分类的可编辑显示偏好。</para>
/// </summary>
public sealed partial class FlashProtocolDisplayItem : ObservableObject
{
    private readonly Action _visibilityChanged;

    internal FlashProtocolDisplayItem(string key, bool visible, Action visibilityChanged)
    {
        Key = FlashProtocolCatalog.NormalizeKey(key) ?? throw new ArgumentException("Unknown flash protocol key.", nameof(key));
        _isVisible = visible;
        _visibilityChanged = visibilityChanged;
    }

    internal string Key { get; }

    internal string TitleKey => FlashProtocolCatalog.GetTitleKey(Key);

    /// <summary>Gets the localized protocol name.<para>获取本地化的协议名称。</para></summary>
    public string DisplayName => LanguageService.Instance.GetString(TitleKey);

    /// <summary>Gets or sets whether the protocol section is shown on the flash page.<para>获取或设置烧录页面是否显示该协议分类。</para></summary>
    [ObservableProperty]
    private bool _isVisible;

    partial void OnIsVisibleChanged(bool value) => _visibilityChanged();

    internal void RefreshLocalizedText() => OnPropertyChanged(nameof(DisplayName));

    internal FlashProtocolDisplayPreference ToPreference() => new()
    {
        Key = Key,
        Visible = IsVisible
    };
}