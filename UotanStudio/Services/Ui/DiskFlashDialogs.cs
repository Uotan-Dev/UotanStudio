using Avalonia.Controls;
using UotanStudio.UiAbstractions;
using Ursa.Controls;

namespace UotanStudio.Services.Ui;

/// <summary>
/// Desktop implementation of the disk-flasher message windows.
/// <para>磁盘烧录消息窗口的桌面实现。</para>
/// </summary>
public sealed class DiskFlashDialogs : IDiskFlashDialogs
{
    private readonly Func<Window?> _owner;

    /// <summary>
    /// Creates the dialogs that parent their windows to the owner supplied by the caller.
    /// <para>创建对话框，其窗口以调用方提供的所有者窗口为父窗口。</para>
    /// </summary>
    /// <param name="owner">Supplies the owner window, or <see langword="null"/> when none is available.
    /// <para>提供所有者窗口；没有可用窗口时返回 <see langword="null"/>。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="owner"/> is <see langword="null"/>.
    /// <para><paramref name="owner"/> 为 <see langword="null"/>。</para></exception>
    public DiskFlashDialogs(Func<Window?> owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }

    /// <inheritdoc />
    public Task ShowErrorAsync(string title, string message) => ShowAsync(title, message, MessageBoxIcon.Error);

    /// <inheritdoc />
    public Task ShowWarningAsync(string title, string message) => ShowAsync(title, message, MessageBoxIcon.Warning);

    private async Task ShowAsync(string title, string message, MessageBoxIcon icon)
    {
        Window? owner = _owner();
        if (owner is null)
        {
            return;
        }

        await DialogService.ShowAsync(owner, title, message, icon).ConfigureAwait(true);
    }
}