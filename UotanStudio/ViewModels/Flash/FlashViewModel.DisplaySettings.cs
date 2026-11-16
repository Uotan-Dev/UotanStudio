using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using UotanStudio.Messages;
using UotanStudio.Models;
using UotanStudio.Services;

namespace UotanStudio.ViewModels.Flash;

/// <summary>
/// Supplies the flash page's bound protocol display state and refreshes it after settings are saved.
/// <para>提供烧录页面绑定的协议显示状态，并在设置保存后刷新。</para>
/// </summary>
public sealed partial class FlashViewModel
{
    /// <summary>Gets the ordered protocol display preferences.<para>获取有序的协议显示偏好。</para></summary>
    [ObservableProperty]
    private IReadOnlyList<DisplayPreference> _flashProtocolDisplay = [];

    internal void BeginFlashProtocolDisplayUpdates()
    {
        LoadFlashProtocolDisplay();
        WeakReferenceMessenger.Default.Register<FlashViewModel, SettingsSaveCompletedMessage>(this,
            static (viewModel, message) =>
            {
                if (message.Succeeded)
                {
                    viewModel.LoadFlashProtocolDisplay();
                }
            });
    }

    internal void EndFlashProtocolDisplayUpdates() =>
        WeakReferenceMessenger.Default.Unregister<SettingsSaveCompletedMessage>(this);

    private void LoadFlashProtocolDisplay() =>
        FlashProtocolDisplay = FlashProtocolCatalog.Normalize(
            SettingsPersistenceService.Instance.LoadSettings().FlashProtocolDisplay);
}