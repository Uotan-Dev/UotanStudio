using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.MarkupExtensions;
using CommunityToolkit.Mvvm.Messaging;
using Material.Icons;
using Material.Icons.Avalonia;
using UotanStudio.Messages;
using UotanStudio.Services.Workspace.Documents;
using UotanStudio.ViewModels.Workspace;

namespace UotanStudio.Views.Controls;

/// <summary>
/// The "Open As" menu entry: builds all children from the <see cref="DocumentOpenRegistry"/> at
/// construction time (Auto pinned first, the rest in MenuOrder), because programmatic or headless
/// ContextMenu opens do not raise <c>Opening</c>.
/// <para>"打开方式"菜单项：构造时即按 <see cref="DocumentOpenRegistry"/> 构建全部子项
/// （Auto 伪项置顶，其余按 MenuOrder 升序），不依赖菜单打开事件——程序化或无头打开
/// ContextMenu 不会触发 <c>Opening</c>。</para>
/// </summary>
public sealed class OpenAsMenuItem : MenuItem
{
    /// <summary>
    /// Creates the entry and fills its "Open As" children from the registry immediately.
    /// <para>创建并立即按注册表填充"打开方式"子项。</para>
    /// </summary>
    public OpenAsMenuItem()
    {
        Items.Add(BuildItem("Explorer_OpenAsAuto", EditorOpenMode.Auto, null));
        foreach (var descriptor in DocumentOpenRegistry.MenuEntries)
        {
            Items.Add(BuildItem(descriptor.LocaleKey, descriptor.Mode, descriptor.IconKind));
        }
    }

    /// <summary>
    /// Builds one child: the header binds a DynamicResource for live language switching, the icon
    /// resolves from the descriptor's MaterialIconKind name, and the click sends an
    /// <see cref="OpenDocumentRequestMessage"/>.
    /// <para>构建单个子项：标题绑定 DynamicResource 以支持语言热切换，图标按描述符的
    /// MaterialIconKind 名称解析，点击发送 <see cref="OpenDocumentRequestMessage"/>。</para>
    /// </summary>
    private static MenuItem BuildItem(string localeKey, EditorOpenMode? mode, string? iconKind)
    {
        var item = new MenuItem { Tag = mode };
        item.Bind(HeaderProperty, new DynamicResourceExtension(localeKey));
        if (!string.IsNullOrEmpty(iconKind) && Enum.TryParse<MaterialIconKind>(iconKind, out var kind))
        {
            item.Icon = new MaterialIcon { Kind = kind, Width = 16, Height = 16 };
        }

        item.Click += OnOpenWith;
        return item;
    }

    private static void OnOpenWith(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: EditorOpenMode mode } item)
        {
            var target = ResolveExplorerNode(item);
            if (target is { IsDirectory: false, IsPlaceholder: false })
            {
                WeakReferenceMessenger.Default.Send(new OpenDocumentRequestMessage(target.Path, mode));
            }

            e.Handled = true;
        }
    }

    private static ExplorerNode? ResolveExplorerNode(MenuItem item)
    {
        for (Control? current = item; current is not null; current = current.Parent as Control)
        {
            if (current.DataContext is ExplorerNode node)
            {
                return node;
            }
        }

        return null;
    }
}