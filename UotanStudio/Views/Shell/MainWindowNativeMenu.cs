using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using UotanStudio.ViewModels.Shell;

namespace UotanStudio.Views.Shell;

/// <summary>
/// <para>将主窗口现有菜单映射到 macOS 菜单栏，共用命令、本地化和动态条目。</para>
/// Projects the existing window menu into the macOS menu bar, sharing commands, localization and dynamic items.
/// </summary>
internal sealed class MainWindowNativeMenu : IDisposable
{
    private readonly Window _window;
    private readonly Menu _source;
    private readonly MenuItems _items;
    private bool _disposed;

    public MainWindowNativeMenu(Window window, Menu source)
    {
        _window = window;
        _source = source;
        var menu = new NativeMenu();
        _items = new MenuItems(source, menu);
        window.PropertyChanged += OnWindowPropertyChanged;
        NativeMenu.SetMenu(window, menu);
        UpdateMenuVisibility();
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == NativeMenu.IsNativeMenuExportedProperty)
            UpdateMenuVisibility();
    }

    private void UpdateMenuVisibility() => _source.IsVisible = !NativeMenu.GetIsNativeMenuExported(_window);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window.PropertyChanged -= OnWindowPropertyChanged;

        _items.Dispose();
    }

    private sealed class MenuItems : IDisposable
    {
        private readonly ItemsControl _source;
        private readonly NativeMenu _target;
        private readonly List<IDisposable> _subscriptions = [];

        public MenuItems(ItemsControl source, NativeMenu target)
        {
            _source = source;
            _target = target;
            ((INotifyCollectionChanged)source.Items).CollectionChanged += OnItemsChanged;
            Rebuild();
        }

        private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

        private void Rebuild()
        {
            Clear();
            foreach (var entry in _source.Items)
            {
                if (entry is Separator)
                {
                    _target.Items.Add(new NativeMenuItemSeparator());
                }
                else if (entry is RecentProjectEntry recent)
                {

                    _target.Items.Add(new NativeMenuItem { Header = recent.DisplayName, Command = recent.OpenCommand });
                }
                else if (entry is MenuItem item)
                {
                    var native = new NativeMenuItem { Gesture = MacGesture(item.InputGesture) };
                    if (item.Header is TextBlock text)
                        _subscriptions.Add(native.Bind(NativeMenuItem.HeaderProperty, text.GetObservable(TextBlock.TextProperty)));
                    else
                        _subscriptions.Add(native.Bind(NativeMenuItem.HeaderProperty, item.GetObservable(MenuItem.HeaderProperty)));
                    _subscriptions.Add(native.Bind(NativeMenuItem.CommandProperty, item.GetObservable(MenuItem.CommandProperty)));
                    _subscriptions.Add(native.Bind(NativeMenuItem.CommandParameterProperty, item.GetObservable(MenuItem.CommandParameterProperty)));
                    _subscriptions.Add(native.Bind(NativeMenuItem.IsEnabledProperty, item.GetObservable(MenuItem.IsEnabledProperty)));
                    _subscriptions.Add(native.Bind(NativeMenuItem.IsVisibleProperty, item.GetObservable(MenuItem.IsVisibleProperty)));
                    _subscriptions.Add(native.Bind(NativeMenuItem.ToggleTypeProperty, item.GetObservable(MenuItem.ToggleTypeProperty)));
                    _subscriptions.Add(native.Bind(NativeMenuItem.IsCheckedProperty, item.GetObservable(MenuItem.IsCheckedProperty)));
                    if (item.Items.Count > 0 || item.ItemsSource is not null)
                    {
                        native.Menu = new NativeMenu();
                        _subscriptions.Add(new MenuItems(item, native.Menu));
                    }
                    _target.Items.Add(native);
                }
            }
        }

        private static KeyGesture? MacGesture(KeyGesture? gesture)
        {
            if (gesture is null || !gesture.KeyModifiers.HasFlag(KeyModifiers.Control)) return gesture;

            if (gesture.Key == Key.H) return new KeyGesture(Key.F, KeyModifiers.Meta | KeyModifiers.Alt);
            if (gesture.Key == Key.Y) return new KeyGesture(Key.Z, KeyModifiers.Meta | KeyModifiers.Shift);
            return new KeyGesture(gesture.Key, (gesture.KeyModifiers & ~KeyModifiers.Control) | KeyModifiers.Meta);
        }

        private void Clear()
        {
            foreach (var subscription in _subscriptions) subscription.Dispose();
            _subscriptions.Clear();
            _target.Items.Clear();
        }

        public void Dispose()
        {
            ((INotifyCollectionChanged)_source.Items).CollectionChanged -= OnItemsChanged;
            Clear();
        }
    }
}