using Avalonia.Skia;
using Xunit;

namespace UotanStudio.Tests.Infrastructure;

/// <summary>
/// 轻量 Avalonia 平台初始化：仅绑定 Skia 渲染接口与字体服务（不启动窗口平台、生命周期或
/// 真实 App 的副作用），供需要构造 Avalonia 控件树（如 <c>WorkspaceDockFactory</c> 的
/// EmptyContent MaterialIcon，其构造函数同步解析几何图形）的测试使用。
/// 幂等初始化，整个测试进程仅执行一次。
/// <para>A lightweight Avalonia platform initialization: binds only the Skia render interface and
/// font services (no windowing platform, lifetime, or real-app side effects), for tests that
/// construct Avalonia control trees (e.g. the WorkspaceDockFactory EmptyContent MaterialIcon,
/// whose constructor parses geometry synchronously). Idempotent — once per test process.</para>
/// </summary>
public static class HeadlessAvalonia
{
    private static int _initialized;

    public static void EnsureInitialized()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1)
        {
            return;
        }

        SkiaPlatform.Initialize();
    }
}