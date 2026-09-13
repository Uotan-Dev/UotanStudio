using Dock.Model.Core;
using UotanStudio.Services.Terminal;
using UotanStudio.ViewModels.Workspace;

namespace UotanStudio.Services.Workspace;

internal sealed class TerminalDockService(IFactory factory, IDock defaultHost) : IDisposable
{
    private sealed class Entry(TerminalViewModel model, IDock host)
    {
        internal TerminalViewModel Model { get; } = model;
        internal IDock Host { get; set; } = host;
    }

    private readonly List<Entry> _entries = [];
    private int _nextId;
    private bool _disposed;

    internal bool IsVisible => _entries.Any(entry => GetHost(entry).VisibleDockables?.Contains(entry.Model) == true);

    internal TerminalViewModel CreateTerminal(TerminalProfile? profile = null, IDock? host = null, bool activate = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        host ??= defaultHost;
        var model = new TerminalViewModel(++_nextId, profile,
            source => CreateTerminal(source.SelectedProfile, GetHost(_entries.Single(entry => entry.Model == source))),
            source => factory.CloseDockable(source));
        _entries.Add(new Entry(model, host));
        factory.AddDockable(host, model);
        if (activate) Activate(model);
        WorkspacePanelService.Instance.RefreshTerminalVisibility();
        return model;
    }

    internal void Show()
    {
        if (_disposed) return;
        if (_entries.Count == 0)
        {
            CreateTerminal();
            return;
        }
        foreach (var entry in _entries)
        {
            var host = GetHost(entry);
            if (host.VisibleDockables?.Contains(entry.Model) != true) factory.AddDockable(host, entry.Model);
        }
        Activate(_entries[^1].Model);
    }

    /// <summary>
    /// <para>仅把终端加回宿主可见列表，不激活任何终端（供会话恢复使用，避免恢复可见性时
    /// 把活动面板切到终端上）。无条目时创建但不激活。</para>
    /// Re-adds the terminals to their hosts without activating any (used by session restore so
    /// restoring visibility never switches the active panel to a terminal). Creates one without
    /// activation when there are no entries.
    /// </summary>
    internal void ShowWithoutActivate()
    {
        if (_disposed) return;
        if (_entries.Count == 0)
        {
            CreateTerminal(activate: false);
            return;
        }
        foreach (var entry in _entries)
        {
            var host = GetHost(entry);
            if (host.VisibleDockables?.Contains(entry.Model) != true) factory.AddDockable(host, entry.Model);
        }
        WorkspacePanelService.Instance.RefreshTerminalVisibility();
    }

    internal void Hide()
    {
        foreach (var entry in _entries)
        {
            var host = GetHost(entry);
            host.VisibleDockables?.Remove(entry.Model);
            if (host.ActiveDockable == entry.Model) host.ActiveDockable = host.VisibleDockables?.FirstOrDefault();
        }
    }

    internal void Remove(TerminalViewModel model)
    {
        _entries.RemoveAll(entry => entry.Model == model);
        model.Dispose();
        WorkspacePanelService.Instance.RefreshTerminalVisibility();
    }

    private void Activate(TerminalViewModel model)
    {
        factory.SetActiveDockable(model);
        if (factory.FindRoot(model) is { } root) factory.SetFocusedDockable(root, model);
    }

    private static IDock GetHost(Entry entry)
    {
        // Keep the latest owner when a terminal is dragged or floated, including while it is hidden.
        if (entry.Model.Owner is IDock host) entry.Host = host;
        return entry.Host;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var entry in _entries) entry.Model.Dispose();
        _entries.Clear();
    }
}
