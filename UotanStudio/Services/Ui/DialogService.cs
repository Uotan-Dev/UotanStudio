using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using UotanStudio.Views.Dialogs;
using Ursa.Controls;

namespace UotanStudio.Services.Ui;

/// <summary>
/// Input for the dangerous-operation confirmation dialog.
/// <para>危险操作二次确认对话框的入参。</para>
/// </summary>
public sealed record DangerousConfirmRequest
{
    /// <summary>
    /// Name of the module the operation belongs to.
    /// <para>操作所属模块的名称。</para>
    /// </summary>
    /// <value>The module name carried with the request.<para>随请求一起传递的模块名。</para></value>
    public required string TabTitle { get; init; }

    /// <summary>
    /// Warning title shown by the confirmation window.
    /// <para>确认窗口显示的警示标题。</para>
    /// </summary>
    /// <value>The warning title text.<para>警示标题文本。</para></value>
    public required string Title { get; init; }

    /// <summary>
    /// Target device, partition or address, byte count and other operation specifics.
    /// <para>目标设备、分区或地址、字节数等具体描述。</para>
    /// </summary>
    /// <value>The detail text shown under the warning title.<para>显示在警示标题下方的说明文本。</para></value>
    public required string Detail { get; init; }

    /// <summary>
    /// Token that has to be typed exactly before the confirmation button becomes usable.
    /// <para>确认按钮可用之前必须严格输入的口令文本。</para>
    /// </summary>
    /// <value>The expected token, defaulting to "CONFIRM".<para>期望的口令，默认为 "CONFIRM"。</para></value>
    public string ConfirmWord { get; init; } = "CONFIRM";

    /// <summary>
    /// Optional device serial number or loader build identifier.
    /// <para>可选的设备序列号或 loader build 标识。</para>
    /// </summary>
    /// <value>The device identifier, or <c>null</c> when none is supplied.<para>设备标识；未提供时为 <c>null</c>。</para></value>
    public string? DeviceIdentifier { get; init; }
}

/// <summary>
/// Shows localized message windows without an overlay or owner mask.
/// <para>显示本地化的消息窗口，不使用遮罩或所有者遮挡。</para>
/// </summary>
internal static class DialogService
{
    internal static Window? GetOwnerWindow() =>
        Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.Windows.FirstOrDefault(window => window.IsActive) ?? desktop.MainWindow
            : null;

    internal static Task<MessageBoxResult> SaveChangesAsync(Window owner, string name)
    {
        var language = LanguageService.Instance;
        var dialog = CreateWindow(language.GetString("Dialog_UnsavedTitle"),
            string.Format(language.GetString("Dialog_UnsavedMessage"), name),
            MessageBoxIcon.Warning, MessageBoxButton.YesNoCancel);
        dialog.Resources["STRING_MENU_DIALOG_YES"] = language.GetString("Menu_Save");
        dialog.Resources["STRING_MENU_DIALOG_NO"] = language.GetString("Dialog_DiscardChanges");
        return dialog.ShowDialog<MessageBoxResult>(owner);
    }

    internal static Task<MessageBoxResult> ShowAsync(
        Window owner, string title, string message, MessageBoxIcon icon) =>
        CreateWindow(title, message, icon, MessageBoxButton.OK)
            .ShowDialog<MessageBoxResult>(owner);

    internal static async Task<bool> ConfirmAsync(
        Window owner, string title, string message, string? confirmText = null, string? cancelText = null)
    {
        var dialog = CreateWindow(title, message, MessageBoxIcon.Warning,
            MessageBoxButton.OKCancel, confirmText, cancelText);
        return await dialog.ShowDialog<MessageBoxResult>(owner) == MessageBoxResult.OK;
    }

    /// <summary>
    /// Shows a localized yes/no confirmation window without an overlay or owner mask.
    /// <para>显示本地化的是/否确认窗口，无遮罩与所有者遮挡。</para>
    /// </summary>
    /// <param name="owner">The owner window.<para>所有者窗口。</para></param>
    /// <param name="title">The window title.<para>窗口标题。</para></param>
    /// <param name="message">The message body.<para>消息正文。</para></param>
    /// <param name="yesText">Optional label for the affirmative button.<para>肯定按钮的可选标签。</para></param>
    /// <param name="noText">Optional label for the negative button.<para>否定按钮的可选标签。</para></param>
    /// <returns><c>true</c> when the affirmative option was chosen.<para>选择肯定选项时为 <c>true</c>。</para></returns>
    internal static async Task<bool> ConfirmYesNoAsync(
        Window owner, string title, string message, string? yesText = null, string? noText = null)
    {
        var language = LanguageService.Instance;
        var dialog = CreateWindow(title, message, MessageBoxIcon.Warning, MessageBoxButton.YesNo);
        dialog.Resources["STRING_MENU_DIALOG_YES"] = yesText ?? language.GetString("Editor_Yes");
        dialog.Resources["STRING_MENU_DIALOG_NO"] = noText ?? language.GetString("Editor_Cancel");
        return await dialog.ShowDialog<MessageBoxResult>(owner) == MessageBoxResult.Yes;
    }

    internal static async Task<string?> InputAsync(
        Window owner, string title, string defaultText = "")
    {
        var input = new TextBox { Text = defaultText, MinWidth = 240 };
        var dialog = CreateWindow(title, input, MessageBoxIcon.Information, MessageBoxButton.OKCancel);
        dialog.Loaded += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (!dialog.IsVisible) return;
            input.Focus();
            input.SelectAll();
        }, DispatcherPriority.Input);
        input.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            if (!string.IsNullOrWhiteSpace(input.Text)) dialog.Close(MessageBoxResult.OK);
            e.Handled = true;
        };

        var result = await dialog.ShowDialog<MessageBoxResult>(owner);
        return result == MessageBoxResult.OK && !string.IsNullOrWhiteSpace(input.Text) ? input.Text : null;
    }

    /// <summary>
    /// Shows the dangerous-operation confirmation window, requiring an irreversibility tick and the exact
    /// <c>ConfirmWord</c> before "Proceed" is enabled. Closing without pressing "Proceed" returns <c>false</c>.
    /// <para>弹出危险操作二次确认窗口，必须勾选不可逆确认并严格输入 <c>ConfirmWord</c> 才能点亮「继续」；未点击「继续」即关闭时返回 <c>false</c>。</para>
    /// </summary>
    /// <param name="owner">The owner window. <para>所有者窗口。</para></param>
    /// <param name="request">The dangerous operation request. <para>危险操作请求。</para></param>
    /// <returns><c>true</c> when "Proceed" was pressed with the gate satisfied.<para>闸门条件满足且按下「继续」时为 <c>true</c>。</para></returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="request"/> is null.<para>当 <paramref name="request"/> 为 null 时抛出。</para></exception>
    public static async Task<bool> ConfirmDangerousAsync(Window owner, DangerousConfirmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var dialog = new DangerousConfirmDialog();
        dialog.Configure(request);
        await dialog.ShowDialog(owner);
        return dialog.Confirmed;
    }

    /// <summary>
    /// Decides whether the dangerous-confirmation "Proceed" button should be enabled: the
    /// acknowledgement must be ticked and the typed text must equal the confirm word exactly.
    /// <para>判断危险操作「继续」按钮是否应当启用：必须勾选且输入文本严格等于确认口令。</para>
    /// </summary>
    /// <param name="isChecked">Whether the irreversibility acknowledgement checkbox is ticked.<para>不可逆确认复选框是否被勾选。</para></param>
    /// <param name="typed">The text typed in the token input.<para>在口令输入框中键入的文本。</para></param>
    /// <param name="word">The expected confirm word. <para>期望的确认口令。</para></param>
    /// <returns><c>true</c> when both conditions hold.<para>两个条件都满足时为 <c>true</c>。</para></returns>
    internal static bool ShouldEnableConfirm(bool isChecked, string typed, string word) =>
        isChecked && typed == word;

    private static MessageBoxWindow CreateWindow(
        string title, object content, MessageBoxIcon icon, MessageBoxButton buttons,
        string? confirmText = null, string? cancelText = null)
    {
        var dialog = new MessageBoxWindow(buttons)
        {
            Title = title,
            Content = content,
            MessageIcon = icon,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false
        };
        dialog.Resources["STRING_MENU_DIALOG_OK"] = confirmText ?? LanguageService.Instance.GetString("Dialog_OK");
        dialog.Resources["STRING_MENU_DIALOG_CANCEL"] = cancelText ?? LanguageService.Instance.GetString("Editor_Cancel");
        return dialog;
    }
}