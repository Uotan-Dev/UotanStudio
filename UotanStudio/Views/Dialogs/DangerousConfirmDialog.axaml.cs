using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using UotanStudio.Services;

namespace UotanStudio.Views.Dialogs;

/// <summary>
/// Dangerous operation confirmation dialog: the "Proceed" button only enables when the
/// irreversibility acknowledgement is ticked and the typed token matches the confirm word.
/// <para>危险操作二次确认对话框：勾选不可逆确认且输入匹配口令时「继续」按钮才可点。</para>
/// </summary>
internal sealed partial class DangerousConfirmDialog : Window
{
    private string _confirmWord = string.Empty;

    /// <summary>
    /// <para>用户是否点击了「继续」按钮（Enter 键走 IsDefault 路径，同样计入）。</para>
    /// <para>Whether the user clicked the "Proceed" button (Enter via IsDefault also counts).</para>
    /// </summary>
    public bool Confirmed { get; private set; }

    /// <summary>
    /// <para>请求继续的口令；用于严格校验输入文本。</para>
    /// <para>The expected token used to strictly validate the typed text.</para>
    /// </summary>
    public string ConfirmWord => _confirmWord;

    /// <summary>
    /// <para>设置对话框内容并显示。</para>
    /// <para>Populates and shows the dialog content.</para>
    /// </summary>
    /// <param name="request">The dangerous operation request. <para>危险操作请求。</para></param>
    internal void Configure(DangerousConfirmRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TabTitle))
        {
            TabTitleText.IsVisible = false;
        }
        else
        {
            TabTitleText.IsVisible = true;
            TabTitleText.Text = request.TabTitle;
        }

        TitleText.Text = request.Title;
        DetailText.Text = request.Detail;

        if (string.IsNullOrWhiteSpace(request.DeviceIdentifier))
        {
            DevicePanel.IsVisible = false;
        }
        else
        {
            DevicePanel.IsVisible = true;
            DeviceIdentifierText.Text = request.DeviceIdentifier;
        }

        _confirmWord = request.ConfirmWord;
        UpdateConfirmEnabled();
    }

    /// <summary>
    /// <para>根据当前勾选与输入状态更新「继续」按钮的可用性。</para>
    /// <para>Updates the "Proceed" button state from the acknowledgement and token text.</para>
    /// </summary>
    private void OnGateChanged(object? sender, RoutedEventArgs e) => UpdateConfirmEnabled();

    private void UpdateConfirmEnabled()
    {
        var typed = ConfirmWordBox.Text ?? string.Empty;
        ConfirmButton.IsEnabled = DialogService.ShouldEnableConfirm(AckCheckBox.IsChecked == true, typed, _confirmWord);
    }

    /// <summary>
    /// <para>用户点击「继续」：仅在闸门校验通过时确认。</para>
    /// <para>Handles the "Proceed" click: only confirms when the gate allows it.</para>
    /// </summary>
    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        if (!ConfirmButton.IsEnabled) return;
        Confirmed = true;
        Close();
    }

    /// <summary>
    /// <para>用户点击「取消」：显式取消，Esc 键亦走此路径。</para>
    /// <para>Handles the "Cancel" click (also triggered by Esc): explicit cancellation.</para>
    /// </summary>
    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Confirmed = false;
        Close();
    }
}