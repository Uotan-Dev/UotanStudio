using System;
using Avalonia;

namespace UotanStudio.Desktop;

internal sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // 工作区停靠使用宿主窗口（HostWindow）模式：DockControl 不启用 ManagedWindowLayer，
        // WorkspaceDockFactory.HostWindowLocator 仅注册 HostWindow。
        // 此前曾全局开启 DockSettings.UseManagedWindows/.UseManagedWindows()，但停靠布局
        // 已回退到宿主窗口模式——两者不一致会导致 DockControl 模板实例化失败、UI 空白。
        // The workspace dock uses the HostWindow mode: DockControl does not enable the
        // ManagedWindowLayer and the factory's HostWindowLocator registers only HostWindow.
        // Globally enabling managed windows while the layout runs in host-window mode made the
        // DockControl template fail to instantiate, leaving the UI blank.
        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}