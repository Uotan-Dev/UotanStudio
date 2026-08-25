using System;
using Avalonia;
using Dock.Settings;
using UotanStudio.Services;
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
        // 解析命令行参数（打开解决方案 / 导入文件 / 帮助 / 版本），其余参数仍传给 Avalonia。
        // Parse command-line options (open solution / import files / help / version); the rest are
        // still forwarded to Avalonia.
        var options = CommandLineParser.Parse(args);
        if (options.HelpRequested)
        {
            PrintHelp();
            return;
        }

        if (options.VersionRequested)
        {
            PrintVersion();
            return;
        }

        if (options.RegisterAssociations)
        {
            Console.WriteLine(FileAssociationService.Register()
                ? "File associations registered.  文件关联已注册。"
                : "Failed to register file associations.  文件关联注册失败。");
            return;
        }

        if (options.UnregisterAssociations)
        {
            Console.WriteLine(FileAssociationService.Unregister()
                ? "File associations unregistered.  文件关联已注销。"
                : "Failed to unregister file associations.  文件关联注销失败。");
            return;
        }

        App.StartupOptions = options;
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

    private static void PrintHelp()
    {
        Console.WriteLine("UotanStudio - Android firmware tool");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  UotanStudio [options] [<solution.uotan>] [<file>...]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -o, --open <path>     Open a .uotan solution on startup");
        Console.WriteLine("  -i, --import <file>   Import a file into the opened solution (repeatable)");
        Console.WriteLine("  -h, --help            Show this help and exit");
        Console.WriteLine("  -v, --version         Show version and exit");
        Console.WriteLine("  --register-file-associations    Register .uotan/.uotanfw/.uotannode associations and exit");
        Console.WriteLine("  --unregister-file-associations  Remove the associations and exit");
        Console.WriteLine();
        Console.WriteLine("A positional <path>.uotan argument opens a solution; other positional files are imported.");
    }

    private static void PrintVersion()
    {
        var version = typeof(Program).Assembly.GetName().Version;
        Console.WriteLine($"UotanStudio {version?.ToString(3) ?? "1.0.0"}");
    }
}