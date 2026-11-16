using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UotanStudio.Models;
using UotanStudio.Services;

namespace UotanStudio.ViewModels.Shell;

/// <summary>
/// Editable order/visibility item shared by configurable display lists.
/// <para>可配置显示列表共用的可编辑顺序与可见性项。</para>
/// </summary>
public sealed partial class DisplayPreferenceItem : ObservableObject
{
    private readonly Action<DisplayPreferenceItem> _visibilityChanged;

    internal DisplayPreferenceItem(
        string key,
        string titleKey,
        string visibilityHintKey,
        bool visible,
        Action<DisplayPreferenceItem> visibilityChanged,
        Action<DisplayPreferenceItem, int> moveRequested)
    {
        Key = key;
        TitleKey = titleKey;
        VisibilityHintKey = visibilityHintKey;
        _isVisible = visible;
        _visibilityChanged = visibilityChanged;
        MoveUpCommand = new RelayCommand(() => moveRequested(this, -1));
        MoveDownCommand = new RelayCommand(() => moveRequested(this, 1));
    }

    internal string Key { get; }

    internal string TitleKey { get; }

    internal string VisibilityHintKey { get; }

    /// <summary>Gets the localized item name.<para>获取本地化的项目名称。</para></summary>
    public string DisplayName => LanguageService.Instance.GetString(TitleKey);

    /// <summary>Gets the localized visibility hint.<para>获取本地化的可见性提示。</para></summary>
    public string VisibilityHint => LanguageService.Instance.GetString(VisibilityHintKey);

    /// <summary>Gets the command that moves the item one position earlier.<para>获取把项目向前移动一位的命令。</para></summary>
    public IRelayCommand MoveUpCommand { get; }

    /// <summary>Gets the command that moves the item one position later.<para>获取把项目向后移动一位的命令。</para></summary>
    public IRelayCommand MoveDownCommand { get; }

    /// <summary>Gets or sets whether the item's visibility can currently be changed.<para>获取或设置当前是否允许修改该项目的可见性。</para></summary>
    [ObservableProperty]
    private bool _canChangeVisibility = true;

    /// <summary>Gets or sets whether the item is shown.<para>获取或设置是否显示该项目。</para></summary>
    [ObservableProperty]
    private bool _isVisible;

    partial void OnIsVisibleChanged(bool value) => _visibilityChanged(this);

    internal void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(VisibilityHint));
    }

    internal DisplayPreference ToPreference() => new()
    {
        Key = Key,
        Visible = IsVisible
    };
}