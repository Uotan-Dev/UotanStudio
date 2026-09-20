using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using Avalonia.Media;

namespace UotanStudio.Mobile;

/// <summary>
/// Android Application class initializing the shared Avalonia App (Avalonia 12 pattern).
/// <para>初始化共享 Avalonia App 的 Android Application 类（Avalonia 12 模式）。</para>
/// </summary>
[Application]
public class MobileApplication(IntPtr javaReference, JniHandleOwnership transfer)
    : AvaloniaAndroidApplication<App>(javaReference, transfer)
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        => builder
            // Inter has no CJK glyphs; on Android the embedded Inter would render all
            // Chinese text as tofu boxes. Do not set a default font here so Skia falls
            // back to system fonts (Roboto/NotoSansCJK) which cover Chinese.
            // <para>Inter 不含中文字形；嵌入字体会把中文全部渲染成豆腐块。此处不设置
            // 默认字体，让 Skia 回退到覆盖中文的系统字体（Roboto/NotoSansCJK）。</para>
            .UseAndroid()
            .With(new FontManagerOptions { DefaultFamilyName = "sans-serif" })
            .With(new SkiaOptions { MaxGpuResourceSizeBytes = 1024 * 1024 * 16 })
            .UseSkia();
}