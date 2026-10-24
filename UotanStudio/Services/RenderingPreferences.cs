namespace UotanStudio.Services;

/// <summary>
/// Rendering backend requested from the host platform at application start.
/// <para>应用启动时向宿主平台请求的渲染后端。</para>
/// </summary>
public enum RenderingBackend
{
    /// <summary>Keeps the platform's default fallback order.<para>保持平台默认的回退顺序。</para></summary>
    Auto,
    /// <summary>CPU software rasterizer.<para>CPU 软件光栅化。</para></summary>
    Software,
    /// <summary>ANGLE over EGL, which uses D3D11 and falls back to D3D9 on older GPUs.<para>基于 EGL 的 ANGLE，使用 D3D11，在较老 GPU 上回退到 D3D9。</para></summary>
    AngleEgl,
    /// <summary>Desktop OpenGL through WGL.<para>经 WGL 的桌面 OpenGL。</para></summary>
    Wgl,
    /// <summary>Vulkan.<para>Vulkan。</para></summary>
    Vulkan,
    /// <summary>X11 OpenGL through GLX.<para>经 GLX 的 X11 OpenGL。</para></summary>
    Glx,
    /// <summary>X11 EGL.<para>X11 EGL。</para></summary>
    Egl,
    /// <summary>Apple Metal.<para>Apple Metal。</para></summary>
    Metal,
    /// <summary>Apple OpenGL.<para>Apple OpenGL。</para></summary>
    OpenGl,
}

/// <summary>
/// Window composition mode requested from the Win32 platform at application start.
/// <para>应用启动时向 Win32 平台请求的窗口合成模式。</para>
/// </summary>
public enum RenderingCompositionMode
{
    /// <summary>Keeps the platform's default fallback order.<para>保持平台默认的回退顺序。</para></summary>
    Auto,
    /// <summary>Windows.UI.Composition host, which provides acrylic and rounded corners.<para>Windows.UI.Composition 宿主，提供亚克力与圆角。</para></summary>
    WinUIComposition,
    /// <summary>DirectComposition host.<para>DirectComposition 宿主。</para></summary>
    DirectComposition,
    /// <summary>Low-latency DXGI swap chain without transparency effects.<para>无透明效果的低延迟 DXGI 交换链。</para></summary>
    LowLatencyDxgiSwapChain,
    /// <summary>Redirection surface.<para>重定向表面。</para></summary>
    RedirectionSurface,
}

/// <summary>
/// Platform family that determines which rendering backends and composition modes are meaningful.
/// <para>决定哪些渲染后端与合成模式有意义的平台族。</para>
/// </summary>
public enum RenderingPlatform
{
    /// <summary>Windows (Win32).<para>Windows（Win32）。</para></summary>
    Windows,
    /// <summary>Linux through X11; Wayland exposes no selectable rendering backend.<para>经 X11 的 Linux；Wayland 没有可选的渲染后端。</para></summary>
    LinuxX11,
    /// <summary>Linux through the native Wayland backend, which always renders through EGL.<para>经原生 Wayland 后端的 Linux，固定通过 EGL 渲染。</para></summary>
    LinuxWayland,
    /// <summary>macOS (Avalonia.Native).<para>macOS（Avalonia.Native）。</para></summary>
    MacOS,
    /// <summary>A platform without selectable Avalonia rendering options.<para>没有可选 Avalonia 渲染选项的平台。</para></summary>
    Unsupported,
}

/// <summary>
/// Catalog of the rendering backends and composition modes each platform accepts, shared by the
/// settings page and the desktop startup path.
/// <para>各平台接受的渲染后端与合成模式目录，由设置页与桌面启动路径共用。</para>
/// </summary>
public static class RenderingPreferenceCatalog
{
    /// <summary>
    /// Gets the platform family of the running process.
    /// <para>获取当前进程的平台族。</para>
    /// </summary>
    /// <value>The detected platform family.<para>检测到的平台族。</para></value>
    public static RenderingPlatform CurrentPlatform { get; } = DetectPlatform();

    /// <summary>
    /// Returns the selectable rendering backends in display order, with <see cref="RenderingBackend.Auto"/> first.
    /// <para>按显示顺序返回可选的渲染后端，<see cref="RenderingBackend.Auto"/> 位于最前。</para>
    /// </summary>
    /// <param name="platform">The platform family; an unknown value yields only the automatic entry.
    /// <para>平台族；未知取值只返回自动项。</para></param>
    /// <returns>The supported backends.<para>受支持的后端。</para></returns>
    public static IReadOnlyList<RenderingBackend> SupportedBackends(RenderingPlatform platform) => platform switch
    {
        RenderingPlatform.Windows =>
            [RenderingBackend.Auto, RenderingBackend.AngleEgl, RenderingBackend.Software,
                RenderingBackend.Wgl, RenderingBackend.Vulkan],
        RenderingPlatform.LinuxX11 =>
            [RenderingBackend.Auto, RenderingBackend.Glx, RenderingBackend.Egl,
                RenderingBackend.Software, RenderingBackend.Vulkan],
        RenderingPlatform.LinuxWayland => [RenderingBackend.Auto],
        RenderingPlatform.MacOS =>
            [RenderingBackend.Auto, RenderingBackend.Metal, RenderingBackend.OpenGl,
                RenderingBackend.Software],
        _ => [RenderingBackend.Auto],
    };

    /// <summary>
    /// Returns the selectable composition modes in display order, with
    /// <see cref="RenderingCompositionMode.Auto"/> first.
    /// <para>按显示顺序返回可选的合成模式，<see cref="RenderingCompositionMode.Auto"/> 位于最前。</para>
    /// </summary>
    /// <param name="platform">The platform family.<para>平台族。</para></param>
    /// <returns>The supported composition modes; empty outside Windows.<para>受支持的合成模式；Windows 之外为空。</para></returns>
    public static IReadOnlyList<RenderingCompositionMode> SupportedCompositionModes(RenderingPlatform platform) =>
        platform == RenderingPlatform.Windows
            ? [RenderingCompositionMode.Auto, RenderingCompositionMode.WinUIComposition,
                RenderingCompositionMode.DirectComposition, RenderingCompositionMode.LowLatencyDxgiSwapChain,
                RenderingCompositionMode.RedirectionSurface]
            : [];

    /// <summary>
    /// Returns whether composition modes apply on the platform.
    /// <para>返回该平台是否适用合成模式。</para>
    /// </summary>
    /// <param name="platform">The platform family.<para>平台族。</para></param>
    /// <returns>True only for Windows.<para>仅 Windows 为 true。</para></returns>
    public static bool SupportsCompositionMode(RenderingPlatform platform) =>
        platform == RenderingPlatform.Windows;

    /// <summary>
    /// Maps a persisted backend to <see cref="RenderingBackend.Auto"/> when the platform does not accept it.
    /// <para>平台不接受该后端时映射为 <see cref="RenderingBackend.Auto"/>。</para>
    /// </summary>
    /// <param name="platform">The platform family.<para>平台族。</para></param>
    /// <param name="backend">The persisted backend.<para>持久化的后端。</para></param>
    /// <returns>The backend when supported, otherwise the automatic choice.<para>受支持时返回原后端，否则返回自动项。</para></returns>
    public static RenderingBackend Normalize(RenderingPlatform platform, RenderingBackend backend) =>
        SupportedBackends(platform).Contains(backend) ? backend : RenderingBackend.Auto;

    /// <summary>
    /// Maps a persisted composition mode to <see cref="RenderingCompositionMode.Auto"/> when the platform
    /// does not accept it.
    /// <para>平台不接受该合成模式时映射为 <see cref="RenderingCompositionMode.Auto"/>。</para>
    /// </summary>
    /// <param name="platform">The platform family.<para>平台族。</para></param>
    /// <param name="mode">The persisted composition mode.<para>持久化的合成模式。</para></param>
    /// <returns>The mode when supported, otherwise the automatic choice.<para>受支持时返回原模式，否则返回自动项。</para></returns>
    public static RenderingCompositionMode NormalizeComposition(
        RenderingPlatform platform, RenderingCompositionMode mode) =>
        SupportedCompositionModes(platform).Contains(mode) ? mode : RenderingCompositionMode.Auto;

    private static RenderingPlatform DetectPlatform() =>
        OperatingSystem.IsWindows() ? RenderingPlatform.Windows :
        OperatingSystem.IsMacOS() ? RenderingPlatform.MacOS :
        OperatingSystem.IsLinux() ? RenderingPlatform.LinuxX11 : RenderingPlatform.Unsupported;
}