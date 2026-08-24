using System;
using Avalonia;
using Dock.Settings;
#if DEBUG
using AvaloniaMcp.Diagnostics;
#endif

namespace UotanStudio.Desktop;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .WithDockSettings(new DockSettingsOptions
            {
                CommandBarMergingEnabled = true,
                CommandBarMergingScope = DockCommandBarMergingScope.ActiveDocument
            })
            .LogToTrace();
#if DEBUG
        // 启动命名管道 MCP 诊断服务器，供 AI 助手/CLI 在线检查与交互当前运行的 Avalonia 应用。
        builder = builder.UseMcpDiagnostics();
#endif
        return builder;
    }
}