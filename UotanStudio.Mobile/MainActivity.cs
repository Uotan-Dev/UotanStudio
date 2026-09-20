using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Avalonia.Android;

namespace UotanStudio.Mobile;

/// <summary>
/// Android entry activity hosting the shared Avalonia App.
/// <para>承载共享 Avalonia App 的 Android 入口 Activity。</para>
/// Avalonia 12 moved App initialization to <see cref="MobileApplication"/>; the activity
/// itself stays a plain non-generic <see cref="AvaloniaMainActivity"/>.
/// <para>Avalonia 12 将 App 初始化移至 <see cref="MobileApplication"/>；本 Activity
/// 保持为非泛型 <see cref="AvaloniaMainActivity"/>。</para>
/// </summary>
[Activity(
    Label = "UotanStudio",
    Theme = "@style/Theme.App.Starting",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Android 15 (targetSdk 35) enforces edge-to-edge: the window lays out under the
        // status bar no matter what the app sets, so the Avalonia title bar/menu is drawn
        // underneath the system bar and invisible. Pad the content view by the system
        // bar insets to push the whole UI below the status bar.
        // <para>Android 15（targetSdk 35）强制 edge-to-edge：窗口必然在状态栏下布局，
        // Avalonia 标题栏/菜单会被系统栏遮挡。按系统栏 insets 给内容视图加内边距，
        // 把整个 UI 推到状态栏之下。</para>
        var content = FindViewById(Android.Resource.Id.Content);
        if (content is not null)
        {
            ViewCompat.SetOnApplyWindowInsetsListener(content, new InsetsPaddingListener());
        }
    }

    private sealed class InsetsPaddingListener : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        public WindowInsetsCompat OnApplyWindowInsets(View v, WindowInsetsCompat insets)
        {
            var bars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars());
            v.SetPadding(bars.Left, bars.Top, bars.Right, bars.Bottom);
            return WindowInsetsCompat.Consumed;
        }
    }
}
