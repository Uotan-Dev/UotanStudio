using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.MarkupExtensions;
using CommunityToolkit.Mvvm.Messaging;
using Material.Icons;
using Material.Icons.Avalonia;
using UotanStudio.Firmware.Core;
using UotanStudio.Messages;
using UotanStudio.Services.Workspace.Documents;
using UotanStudio.ViewModels.Workspace;

namespace UotanStudio.Views.Controls;

/// <summary>
/// The "Open As" menu entry: builds every child from the <see cref="DocumentOpenRegistry"/> at construction
/// time (Auto pinned first, the rest in MenuOrder), because programmatic or headless ContextMenu opens do not
/// raise <c>Opening</c>. <see cref="RefreshFor"/> then re-orders those children per node once the explorer
/// reports the right-clicked file, so entries whose format probe matches appear before the rest; nothing is
/// hidden, because this menu exists to override automatic detection.
/// <para>"打开方式"菜单项：构造时即按 <see cref="DocumentOpenRegistry"/> 构建全部子项
/// （Auto 伪项置顶，其余按 MenuOrder 升序），不依赖菜单打开事件——程序化或无头打开
/// ContextMenu 不会触发 <c>Opening</c>。资源管理器汇报被右键的文件后，<see cref="RefreshFor"/>
/// 再按节点重排这些子项：格式探测命中的条目排在前面，其余随后；不隐藏任何条目，
/// 因为该菜单的存在意义就是覆盖自动识别。</para>
/// </summary>
public sealed class OpenAsMenuItem : MenuItem
{
    private ExplorerNode? _node;

    /// <summary>
    /// Keeps the entry styled as a plain <see cref="MenuItem"/>. Avalonia looks a control theme up by the
    /// element's style key, which defaults to the concrete type; a derived control therefore inherits no
    /// theme from its base unless it says so. Without this override the entry gets no <c>ControlTheme</c> —
    /// no template, no header presenter and no submenu — so it stays in the menu's item list while rendering
    /// nothing, which is exactly how a right-clicked file lost its "Open As" entry.
    /// <para>让本入口继续按普通 <see cref="MenuItem"/> 取样式。Avalonia 以元素的样式键查找控件主题，该键默认取具体类型，
    /// 派生控件若不声明就继承不到基类主题。没有该重写时入口拿不到 <c>ControlTheme</c>——
    /// 既无模板，也无标题呈现器与子菜单——它仍留在菜单项列表里却不渲染任何内容，
    /// 右键文件看不到「打开方式」正是由此产生。</para>
    /// </summary>
    protected override Type StyleKeyOverride => typeof(MenuItem);

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
    /// Gets the node this entry was last prepared for; null after construction and for programmatic opens.
    /// <para>获取本入口最后一次为之准备的节点；构造后与程序化打开时为 null。</para>
    /// </summary>
    internal ExplorerNode? Node => _node;

    /// <summary>
    /// Re-orders the children for the file this entry is about to act on. Already-prepared nodes are skipped,
    /// so repeated right-clicks on the same file neither re-read its header nor rebuild the items. Directories,
    /// placeholders, missing files and unreadable headers keep the plain registry order.
    /// <para>为即将作用于的文件重排子项。已准备的节点直接跳过，因此对同一文件重复右键既不重读文件头也不重建子项。
    /// 目录、占位节点、文件缺失与头部不可读时保持注册表原序。</para>
    /// </summary>
    /// <param name="node">The right-clicked node. <para>被右键的节点。</para></param>
    internal void RefreshFor(ExplorerNode node)
    {
        if (ReferenceEquals(_node, node))
        {
            return;
        }

        _node = node;
        var entries = DocumentOpenRegistry.MenuEntries;
        var applicable = 0;
        if (!node.IsDirectory && !node.IsPlaceholder && File.Exists(node.Path))
        {
            (entries, applicable) = DocumentOpenRegistry.OrderByApplicability(
                entries, node.Path, ReadHeader(node.Path));
        }

        Items.Clear();
        Items.Add(BuildItem("Explorer_OpenAsAuto", EditorOpenMode.Auto, null));
        for (var index = 0; index < entries.Count; index++)
        {
            if (applicable > 0 && applicable < entries.Count && index == applicable)
            {
                Items.Add(new Separator());
            }

            var descriptor = entries[index];
            Items.Add(BuildItem(descriptor.LocaleKey, descriptor.Mode, descriptor.IconKind));
        }
    }

    /// <summary>
    /// Reads the bounded file header once for the whole probe pass; an unreadable file yields null so the
    /// predicates fall back to extension-only routing.
    /// <para>为整趟探测只读取一次有界文件头；文件不可读时返回 null，使各谓词退回纯扩展名路由。</para>
    /// </summary>
    /// <param name="path">The file to read. <para>待读取的文件。</para></param>
    /// <returns>The header bytes, or null. <para>文件头字节，或为 null。</para></returns>
    private static byte[]? ReadHeader(string path)
    {
        try
        {
            return FormatDetector.ReadHeader(path, FileOpenMode.ContentProbeLength);
        }
        catch
        {
            return null;
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
            var target = FindOwner(item)?.Node ?? ResolveExplorerNode(item);
            if (target is { IsDirectory: false, IsPlaceholder: false })
            {
                WeakReferenceMessenger.Default.Send(new OpenDocumentRequestMessage(target.Path, mode));
            }

            e.Handled = true;
        }
    }

    /// <summary>
    /// Finds the enclosing open-as entry of a clicked child.
    /// <para>查找被点击子项所属的「打开方式」入口。</para>
    /// </summary>
    /// <param name="item">The clicked child. <para>被点击的子项。</para></param>
    /// <returns>The owning entry, or null. <para>所属入口，或为 null。</para></returns>
    private static OpenAsMenuItem? FindOwner(MenuItem item)
    {
        for (Control? current = item; current is not null; current = current.Parent as Control)
        {
            if (current is OpenAsMenuItem owner)
            {
                return owner;
            }
        }

        return null;
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