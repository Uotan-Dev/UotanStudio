using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Platform;
using Avalonia.Platform;

namespace UotanStudio.Services;

/// <summary>
/// Probes the running Avalonia process for the window backend, graphics implementation and Win32 composition
/// host actually in use, so the settings page can report live state instead of the configured fallback order.
/// <para>探测运行中的 Avalonia 进程实际使用的窗口后端、图形实现与 Win32 合成宿主，使设置页报告实时状态而不是
/// 配置的回退顺序。</para>
/// </summary>
internal static class RenderingRuntimeProbe
{
    /// <summary>
    /// Resolves the windowing backend of the running application, distinguishing native Wayland from X11; falls
    /// back to the OS family when no window exists.
    /// <para>解析运行中应用的窗口后端，区分原生 Wayland 与 X11；没有窗口时回退到操作系统族。</para>
    /// </summary>
    /// <returns>The detected windowing platform family.<para>检测到的窗口平台族。</para></returns>
    internal static RenderingPlatform DetectWindowingPlatform()
    {
        foreach (var window in Windows())
        {
            if (window.PlatformImpl is IWaylandOptionsToplevelImplFeature)
            {
                return RenderingPlatform.LinuxWayland;
            }

            if (window.PlatformImpl is IX11OptionsToplevelImplFeature)
            {
                return RenderingPlatform.LinuxX11;
            }
        }

        return RenderingPreferenceCatalog.CurrentPlatform;
    }

    /// <summary>
    /// Returns the locale resource key of the rendering backend the running process actually uses, or null when
    /// no Avalonia application is running or the implementation is unrecognized.
    /// <para>返回运行进程实际使用的渲染后端的语言资源键；没有运行中的 Avalonia 应用或实现无法识别时返回 null。</para>
    /// </summary>
    /// <returns>The backend label key, or null.<para>后端标签资源键，或 null。</para></returns>
    internal static string? DetectActiveBackendLabelKey()
        => TryResolvePlatformGraphics(out var graphics) ? MapGraphicsLabelKey(graphics?.GetType().Name) : null;

    /// <summary>
    /// Returns the locale resource key of the Win32 composition host the running process actually uses, or null
    /// when the host cannot be observed.
    /// <para>返回运行进程实际使用的 Win32 合成宿主的语言资源键；无法观测时返回 null。</para>
    /// </summary>
    /// <returns>The composition label key, or null.<para>合成模式标签资源键，或 null。</para></returns>
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "The composition surface getter is read from the live window implementation only to report " +
            "the host in use; a trimmed getter degrades the label to the automatic text.")]
    internal static string? DetectActiveCompositionLabelKey()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var windowImpl = FindWindowImpl();
        if (windowImpl is null)
        {
            return null;
        }

        if (windowImpl.GetType().Name.Contains("SwapChainTopLevelImpl", StringComparison.Ordinal))
        {
            return "Settings_CompositionModeLowLatency";
        }

        var surfaceProperty = windowImpl.GetType().GetProperty(
            "CompositionEffectsSurface", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (surfaceProperty is null)
        {
            return null;
        }

        var surface = surfaceProperty.GetValue(windowImpl);
        return surface is null
            ? "Settings_CompositionModeRedirection"
            : MapCompositionLabelKey(surface.GetType().Name);
    }

    /// <summary>
    /// Maps a platform graphics implementation type name to the locale resource key of its backend label.
    /// <para>把平台图形实现的类型名映射为后端标签的语言资源键。</para>
    /// </summary>
    /// <param name="implementationTypeName">The implementation type name; null means software rendering.
    /// <para>实现类型名；null 表示软件渲染。</para></param>
    /// <returns>The backend label key, or null when the implementation is unrecognized.
    /// <para>后端标签资源键；实现无法识别时为 null。</para></returns>
    internal static string? MapGraphicsLabelKey(string? implementationTypeName) => implementationTypeName switch
    {
        null => "Settings_RenderingBackendSoftware",
        var name when name.Contains("D3D9Angle", StringComparison.Ordinal) => "Settings_RenderingBackendAngleEglD3D9",
        var name when name.Contains("D3D11Angle", StringComparison.Ordinal) => "Settings_RenderingBackendAngleEgl",
        var name when name.Contains("Wgl", StringComparison.Ordinal) => "Settings_RenderingBackendWgl",
        var name when name.Contains("Vulkan", StringComparison.Ordinal) => "Settings_RenderingBackendVulkan",
        var name when name.Contains("Glx", StringComparison.Ordinal) => "Settings_RenderingBackendGlx",
        var name when name.Contains("Metal", StringComparison.Ordinal) => "Settings_RenderingBackendMetal",
        var name when name.Contains("AvaloniaNativeGl", StringComparison.Ordinal) => "Settings_RenderingBackendOpenGl",
        var name when name.Contains("Wayland", StringComparison.Ordinal) => "Settings_RenderingBackendEgl",
        var name when name.Contains("Egl", StringComparison.Ordinal) => "Settings_RenderingBackendEgl",
        _ => null,
    };

    /// <summary>
    /// Maps a Win32 composition surface type name to the locale resource key of its composition-mode label.
    /// <para>把 Win32 合成表面类型名映射为合成模式标签的语言资源键。</para>
    /// </summary>
    /// <param name="surfaceTypeName">The composition surface type name.<para>合成表面类型名。</para></param>
    /// <returns>The composition label key, or null when the surface is unrecognized.
    /// <para>合成模式标签资源键；表面无法识别时为 null。</para></returns>
    internal static string? MapCompositionLabelKey(string? surfaceTypeName) => surfaceTypeName switch
    {
        null => null,
        var name when name.Contains("WinUiCompositedWindowSurface", StringComparison.Ordinal) =>
            "Settings_CompositionModeWinUI",
        var name when name.Contains("DirectCompositedWindowSurface", StringComparison.Ordinal) =>
            "Settings_CompositionModeDirect",
        _ => null,
    };

    private static IEnumerable<Window> Windows() =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.Windows
            : [];

    private static ITopLevelImpl? FindWindowImpl()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return null;
        }

        var window = desktop.Windows.FirstOrDefault(candidate => candidate.IsActive)
            ?? desktop.MainWindow
            ?? desktop.Windows.FirstOrDefault();
        return window?.PlatformImpl;
    }

    /// <summary>
    /// Resolves the platform graphics service through the dependency resolver Avalonia keeps for platform
    /// services. The resolver accessors are internal to Avalonia's reference assemblies, so the accessors and
    /// resolver method are preserved explicitly for trimming.
    /// <para>通过 Avalonia 为平台服务保留的依赖解析器取得平台图形服务。解析器访问器在 Avalonia 的引用程序集中
    /// 是 internal，因此显式保留访问器与解析方法供裁剪使用。</para>
    /// </summary>
    /// <param name="graphics">The resolved service, or null when the process renders in software.
    /// <para>解析到的服务；进程使用软件渲染时为 null。</para></param>
    /// <returns>True when the service query itself succeeded.<para>服务查询本身成功时为 true。</para></returns>
    [DynamicDependency("get_Current", typeof(AvaloniaLocator))]
    [DynamicDependency("GetService", typeof(AvaloniaLocator))]
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "AvaloniaLocator.GetService is preserved by DynamicDependency; the resolver instance is " +
            "the retained AvaloniaLocator.")]
    private static bool TryResolvePlatformGraphics(out IPlatformGraphics? graphics)
    {
        graphics = null;
        var current = typeof(AvaloniaLocator).GetProperty("Current", BindingFlags.Public | BindingFlags.Static)
            ?? typeof(AvaloniaLocator).GetProperty("Current", BindingFlags.NonPublic | BindingFlags.Static);
        if (current?.GetValue(null) is not IAvaloniaDependencyResolver resolver)
        {
            return false;
        }

        var getService = resolver.GetType().GetMethod(
            "GetService", BindingFlags.Public | BindingFlags.Instance, null, [typeof(Type)], null);
        if (getService is null)
        {
            return false;
        }

        graphics = getService.Invoke(resolver, [typeof(IPlatformGraphics)]) as IPlatformGraphics;
        return true;
    }
}