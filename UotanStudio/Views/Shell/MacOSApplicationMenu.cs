using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace UotanStudio.Views.Shell;

/// <summary>
/// <para>本地化 Avalonia 提供的应用菜单，保留原生系统动作并替换框架关于窗口。</para>
/// Localizes Avalonia's application menu, retaining native system actions and replacing its About dialog.
/// </summary>
internal sealed class MacOSApplicationMenu : IDisposable
{
    private readonly List<IDisposable> _bindings = [];
    private readonly NativeMenuItem _aboutItem = new();
    private readonly EventHandler _aboutClick;

    public MacOSApplicationMenu(Application app, Action showAbout)
    {
        _aboutClick = (_, _) => showAbout();
        _aboutItem.Click += _aboutClick;

        if (NativeMenu.GetMenu(app) is not { } menu) return;

        // Avalonia 12.1.2 creates these English headers and attaches private native callbacks.
        // Capture each item before localization so language changes preserve Services/Hide/Show/Quit behavior.
        // 在本地化前识别框架默认项，保留系统服务子菜单及隐藏、显示、退出的原有回调。
        foreach (var item in menu.Items.OfType<NativeMenuItem>().ToArray())
        {
            if (item.Header == "About Avalonia")
            {
                var index = menu.Items.IndexOf(item);
                menu.Items.RemoveAt(index);
                menu.Items.Insert(index, _aboutItem);
                Localize(_aboutItem, "Menu_About");
                continue;
            }

            var resourceKey = item.Header switch
            {
                "Services" => "Menu_MacServices",
                "Hide Others" => "Menu_MacHideOthers",
                "Show All" => "Menu_MacShowAll",
                "Quit" => "Menu_MacQuit",
                var header when header == "Hide " + app.Name => "Menu_MacHide",
                _ => null
            };
            if (resourceKey is not null) Localize(item, resourceKey);
            if (resourceKey == "Menu_MacHideOthers")
                item.Gesture = new KeyGesture(Key.H, KeyModifiers.Meta | KeyModifiers.Alt);
        }

        void Localize(NativeMenuItem item, string resourceKey) =>
            _bindings.Add(item.Bind(NativeMenuItem.HeaderProperty, app.GetResourceObservable(resourceKey)));
    }

    public void Dispose()
    {
        _aboutItem.Click -= _aboutClick;
        foreach (var binding in _bindings) binding.Dispose();
        _bindings.Clear();
    }
}
