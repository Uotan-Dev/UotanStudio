using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Services;

namespace UotanStudio.ViewModels.Flash;

/// <summary>
/// Restores and persists the flash page's side-section expander state so a restart reopens the sections the user
/// left open.
/// <para>恢复并持久化烧录页面侧边分类的展开状态，使重启后重新打开用户上次展开的分类。</para>
/// </summary>
public sealed partial class FlashViewModel
{
    private bool _loadingSectionExpansion;

    /// <summary>Whether the Qualcomm EDL section is expanded. <para>高通 EDL 分类是否展开。</para></summary>
    [ObservableProperty]
    private bool _isEdlSectionExpanded;

    /// <summary>Whether the MediaTek section is expanded. <para>联发科分类是否展开。</para></summary>
    [ObservableProperty]
    private bool _isMtkSectionExpanded;

    /// <summary>Whether the Rockchip section is expanded. <para>瑞芯微分类是否展开。</para></summary>
    [ObservableProperty]
    private bool _isRockchipSectionExpanded;

    /// <summary>Whether the image operations section is expanded. <para>镜像操作分类是否展开。</para></summary>
    [ObservableProperty]
    private bool _isImageSectionExpanded;

    partial void OnIsEdlSectionExpandedChanged(bool value) => PersistSectionExpansion();

    partial void OnIsMtkSectionExpandedChanged(bool value) => PersistSectionExpansion();

    partial void OnIsRockchipSectionExpandedChanged(bool value) => PersistSectionExpansion();

    partial void OnIsImageSectionExpandedChanged(bool value) => PersistSectionExpansion();

    private void LoadSectionExpansion()
    {
        var sections = SettingsPersistenceService.Instance.LoadSettings().ExpandedFlashSections;
        _loadingSectionExpansion = true;
        try
        {
            IsEdlSectionExpanded = FlashSectionState.IsExpanded(sections, FlashSectionState.Edl);
            IsMtkSectionExpanded = FlashSectionState.IsExpanded(sections, FlashSectionState.Mtk);
            IsRockchipSectionExpanded = FlashSectionState.IsExpanded(sections, FlashSectionState.Rockchip);
            IsImageSectionExpanded = FlashSectionState.IsExpanded(sections, FlashSectionState.Image);
        }
        finally
        {
            _loadingSectionExpansion = false;
        }
    }

    private void PersistSectionExpansion()
    {
        if (_loadingSectionExpansion)
        {
            return;
        }

        SettingsPersistenceService.Instance.SaveExpandedFlashSections(FlashSectionState.Capture(
            IsEdlSectionExpanded,
            IsMtkSectionExpanded,
            IsRockchipSectionExpanded,
            IsImageSectionExpanded));
    }
}