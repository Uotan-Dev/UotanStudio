using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using UotanStudio.Services.Ui;
using UotanStudio.UiAbstractions;
using UotanStudio.Views.Devices;
using UotanStudio.Views.Shell;

namespace UotanStudio.Views;

/// <summary>
/// Desktop window service: the only place that constructs host windows and dialogs for view models.
/// <para>桌面窗口服务：为视图模型构造宿主窗口与对话框的唯一位置。</para>
/// </summary>
public sealed class UiWindowService : IWindowService
{
    /// <summary>
    /// The shared service instance used by the desktop shell and by views that open tool windows.
    /// <para>桌面 Shell 与打开工具窗口的视图共享的服务实例。</para>
    /// </summary>
    public static UiWindowService Shared { get; } = new();

    private Window? _deviceWindow;
    private Window? _logWindow;
    private Window? _usbTreeWindow;
    private Window? _keyManagerWindow;
    private Window? _visualPluginManagerWindow;
    private Window? _apkSigningWindow;
    private Window? _bootPatchWindow;
    private Window? _avbToolWindow;
    private ChecksumWindow? _checksumWindow;

    private UiWindowService()
    {
    }

    /// <inheritdoc />
    public bool HasOwnerWindow => DialogService.GetOwnerWindow() is not null;

    /// <inheritdoc />
    public INewProjectDialog CreateNewProjectDialog(NewProjectWindowMode mode, string? baseDirectory,
        IEnumerable<string>? reservedNames) => new NewProjectWindow(mode, baseDirectory, reservedNames);

    /// <inheritdoc />
    public async Task ShowQrPairingAsync()
    {
        if (DialogService.GetOwnerWindow() is not { } owner)
        {
            return;
        }

        await new QrPairingWindow().ShowDialog(owner);
    }

    /// <inheritdoc />
    public void ShowHostWindow(HostWindowKind kind)
    {
        switch (kind)
        {
            case HostWindowKind.Device:
                ShowToolWindow(ref _deviceWindow, static () => new DeviceWindow());
                break;
            case HostWindowKind.Log:
                ShowToolWindow(ref _logWindow, static () => new LogWindow());
                break;
            case HostWindowKind.UsbTree:
                ShowToolWindow(ref _usbTreeWindow, static () => new UsbTreeWindow());
                break;
            case HostWindowKind.KeyManager:
                ShowToolWindow(ref _keyManagerWindow, static () => new KeyManagerWindow());
                break;
            case HostWindowKind.VisualPluginManager:
                ShowToolWindow(ref _visualPluginManagerWindow, static () => new VisualPluginManagerWindow());
                break;
            case HostWindowKind.ApkSigning:
                ShowToolWindow(ref _apkSigningWindow, static () => new ApkSigningWindow());
                break;
            case HostWindowKind.BootPatch:
                ShowToolWindow(ref _bootPatchWindow, static () => new BootPatchWindow());
                break;
            case HostWindowKind.AvbTool:
                ShowToolWindow(ref _avbToolWindow, static () => new AvbToolWindow());
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown host window kind.");
        }
    }

    /// <inheritdoc />
    public void ShowChecksumWindow(string? filePath = null)
    {
        if (_checksumWindow is { IsVisible: true } existing)
        {
            if (!string.IsNullOrEmpty(filePath))
            {
                existing.SetFile(filePath);
            }

            existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        var window = new ChecksumWindow(filePath);
        _checksumWindow = window;
        window.Show();
    }

    /// <inheritdoc />
    public void ShowAboutDialog()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is { } window)
        {
            new AboutDialog().ShowDialog(window);
        }
    }

    /// <summary>
    /// Shows a cached tool window: creates and shows it if not open, otherwise restores and focuses it.
    /// <para>显示缓存的工具窗口：尚未打开时创建并显示，否则恢复正常状态并前置聚焦。</para>
    /// </summary>
    /// <param name="holder">The cached window reference. <para>缓存的窗口引用。</para></param>
    /// <param name="factory">The factory creating a new window instance. <para>创建新窗口实例的工厂。</para></param>
    private static void ShowToolWindow(ref Window? holder, Func<Window> factory)
    {
        if (holder is not { IsVisible: true })
        {
            holder = factory();
            holder.Show();
        }
        else
        {
            holder.WindowState = WindowState.Normal;
            holder.Activate();
        }
    }
}
