using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using UotanStudio.Services;
using UotanStudio.ViewModels.Basic;

namespace UotanStudio.Views.Basic;

/// <summary>Home section of the basic page. 基础页面的「主页」视图。</summary>
public partial class BasicHomeView : UserControl
{
    /// <summary>Initializes the home section. 初始化主页视图。</summary>
    public BasicHomeView() => InitializeComponent();

    /// <summary>
    /// <para>设备信息值按钮的点击处理：把按钮上显示的值复制到剪贴板；值仍是占位符时没有可复制的内容，直接忽略。</para>
    /// Click handler of the device-info value buttons: copies the value shown on the button to the clipboard and
    /// ignores the placeholder, which has nothing worth copying.
    /// </summary>
    private async void OnCopyValueClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Content: string text } || string.IsNullOrWhiteSpace(text)
            || text == BasicViewModel.Placeholder)
        {
            return;
        }

        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            {
                return;
            }

            await clipboard.SetTextAsync(text);
            await clipboard.FlushAsync();
        }
        catch (Exception ex)
        {
            // 剪贴板被其它进程占用时复制会失败，这里记录后继续展示设备信息。
            // A clipboard held by another process makes the copy fail; log it and keep the panel usable.
            LogService.Instance.Write(LogLevel.Error, "BasicHome", $"Copy failed: {ex.Message}");
        }
    }
}