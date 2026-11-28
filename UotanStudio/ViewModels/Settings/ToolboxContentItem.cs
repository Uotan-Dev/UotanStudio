using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UotanStudio.Services;
using UotanStudio.Services.Toolbox;

namespace UotanStudio.ViewModels.Settings;

/// <summary>
/// One row of the toolbox card: an embedded asset bundle, a vendor driver package, a rebuildable download cache, or a
/// root patch package that was downloaded from a root project's releases.
/// <para>工具箱卡片的一行：内嵌资源包、厂商驱动包、可重建下载缓存，或从 root 项目 Release 下载的补丁包。</para>
/// </summary>
public sealed partial class ToolboxContentItem : ObservableObject
{
    private readonly ToolboxContentRow _row;

    internal ToolboxContentItem(ToolboxContentRow row, Action<ToolboxContentItem> purge,
        Action<ToolboxContentItem> export, Action<ToolboxContentItem>? install,
        Action<ToolboxContentItem, ToolboxContentEntryItem>? deleteEntry = null)
    {
        _row = row;
        PurgeCommand = new RelayCommand(() => purge(this), () => row.CanPurge);

        ExportCommand = new RelayCommand(
            () => export(this),
            () => row.Kind is ToolboxContentKind.EmbeddedBundle or ToolboxContentKind.DriverPackage);
        InstallCommand = new RelayCommand(() => install?.Invoke(this), () => install is not null && row.CanInstall);

        Entries = row.Entries is { Count: > 0 } entries
            ? entries
                .Select(entry => new ToolboxContentEntryItem(
                    entry, deleteEntry is null ? null : item => deleteEntry(this, item)))
                .ToArray()
            : [];

        IsDriverRow = row.Kind == ToolboxContentKind.DriverPackage;
    }

    internal ToolboxContentRow Row => _row;

    /// <summary>Gets the bundle key, cache role, driver package id or root project key backing this row.
    /// <para>获取该行背后的资源包键、缓存角色、驱动包标识或 root 项目键。</para>
    /// </summary>
    internal string Id => _row.Id;

    /// <summary>Gets the localized display name, or the literal upstream project name for root patch rows.
    /// <para>获取本地化的显示名称；补丁包行为上游项目字面名。</para>
    /// </summary>
    public string DisplayName => _row.LiteralTitle is { Length: > 0 } literal
        ? literal
        : LanguageService.Instance.GetString(_row.TitleKey ?? string.Empty);

    /// <summary>Gets the localized description.<para>获取本地化的说明。</para></summary>
    public string Description => LanguageService.Instance.GetString(_row.DescriptionKey);

    /// <summary>Gets whether the row is an embedded bundle rather than a cache, driver package or patch package.
    /// <para>获取该行是否为内嵌资源包，而不是缓存、驱动包或补丁包。</para>
    /// </summary>
    public bool IsEmbedded => _row.Kind == ToolboxContentKind.EmbeddedBundle;

    /// <summary>Gets whether the row is a download cache that can be purged.
    /// <para>获取该行是否为可以清除的下载缓存。</para>
    /// </summary>
    public bool IsCache => _row.Kind == ToolboxContentKind.DownloadCache;

    /// <summary>Gets whether the row describes a vendor driver package.
    /// <para>获取该行是否描述厂商驱动包。</para>
    /// </summary>
    public bool IsDriver => _row.Kind == ToolboxContentKind.DriverPackage;

    /// <summary>Gets whether the row describes a downloaded root patch package.
    /// <para>获取该行是否描述已下载的 root 补丁包。</para>
    /// </summary>
    public bool IsRootPatch => _row.Kind == ToolboxContentKind.RootPatchPackage;

    /// <summary>Gets whether the payload is shipped with this build, which for a driver package means the embedded
    /// INFs are present and Windows can stage them.
    /// <para>获取该负载是否随本次构建分发；对驱动包而言即内嵌 INF 齐备且 Windows 能够暂存它。</para>
    /// </summary>
    public bool IsShipped => _row.IsShipped;

    /// <summary>Gets whether a download cache or patch folder currently holds anything.
    /// <para>获取下载缓存或补丁目录当前是否有内容。</para>
    /// </summary>
    public bool HasCachedContent => _row.PresentBytes > 0;

    /// <summary>Gets the declared size of the payload, already formatted for display.
    /// <para>获取负载声明的体积，已格式化为可显示文本。</para>
    /// </summary>
    public string DeclaredSizeText => FormatBytes(_row.DeclaredBytes);

    /// <summary>Gets the size currently on disk, or an explicit label when nothing is materialized or cached.
    /// <para>获取当前磁盘上的体积；没有物化或缓存时给出明确的文本。</para>
    /// </summary>
    public string PresentSizeText => HasCachedContent
        ? FormatBytes(_row.PresentBytes)
        : LanguageService.Instance.GetString(IsEmbedded ? "Toolbox_NotMaterialized" : "Toolbox_CacheEmpty");

    /// <summary>Gets the file-count summary, for example <c>12 files</c>.
    /// <para>获取文件数摘要，例如 <c>12 个文件</c>。</para>
    /// </summary>
    public string FileCountText => string.Format(
        LanguageService.Instance.GetString("Toolbox_FileCount"), _row.FileCount);

    /// <summary>Gets the localized state of the row: shipped or not for bundles, installed, installable or
    /// unavailable for driver packages, and cached or empty for download caches and patch folders.
    /// <para>获取该行的本地化状态：资源包为随包与否，驱动包为已安装、可安装或不可用，下载缓存与补丁目录为已缓存或为空。</para>
    /// </summary>
    public string StateText
    {
        get
        {
            if (IsDriver)
            {
                if (!_row.IsShipped)
                {
                    return _row.Detail is { Length: > 0 } reason
                        ? FormatUnavailable(reason)
                        : LanguageService.Instance.GetString("Toolbox_StateUnavailable");
                }

                if (_row.IsInstalled)
                {
                    return LanguageService.Instance.GetString("Toolbox_StateInstalled");
                }

                return LanguageService.Instance.GetString(
                    _row.Detail is { Length: > 0 } ? "Toolbox_StateUnavailable" : "Toolbox_StateInstallable");
            }

            if (IsCache || IsRootPatch)
            {
                return LanguageService.Instance.GetString(
                    HasCachedContent ? "Toolbox_StateCached" : "Toolbox_CacheEmpty");
            }

            return LanguageService.Instance.GetString(
                _row.IsShipped ? "Toolbox_StateEmbedded" : "Toolbox_StateNotShipped");
        }
    }

    /// <summary>Gets whether the purge action is offered for this row.
    /// <para>获取该行是否提供清除动作。</para>
    /// </summary>
    public bool CanPurgeCache => _row.CanPurge;

    /// <summary>Gets whether the export action is offered for this row.
    /// <para>获取该行是否提供导出动作。</para>
    /// </summary>
    public bool CanExport => _row.IsShipped && (IsEmbedded || IsDriver);

    /// <summary>Gets whether the driver action is offered for this row.
    /// <para>获取该行是否提供驱动动作。</para>
    /// </summary>
    public bool CanInstall => _row.CanInstall;

    /// <summary>Gets whether the driver package is already staged, which turns the action into a removal.
    /// <para>获取该驱动包是否已暂存，此时动作变为移除。</para>
    /// </summary>
    public bool IsInstalled => _row.IsInstalled;

    /// <summary>Gets the localized label of the driver action, which reads as removal once the package is staged.
    /// Both labels come from the same key family so the pair stays aligned in every language.
    /// <para>获取驱动动作的本地化标签；包已暂存时读作移除。两个标签取自同一组键，使这对文案在任何语言下都保持对齐。</para>
    /// </summary>
    public string InstallActionText => LanguageService.Instance.GetString(
        _row.IsInstalled ? "Toolbox_DriverUninstall" : "Toolbox_DriverInstall");

    /// <summary>Gets the command that deletes this row's derived cache.
    /// <para>获取删除该行派生缓存的命令。</para>
    /// </summary>
    public IRelayCommand PurgeCommand { get; }

    /// <summary>Gets the command that exports this embedded bundle to a directory the user picks.
    /// <para>获取把该内嵌资源包导出到用户所选目录的命令。</para>
    /// </summary>
    public IRelayCommand ExportCommand { get; }

    /// <summary>Gets the command that stages or removes this driver package in the Windows driver store.
    /// <para>获取在 Windows 驱动仓库中暂存或移除该驱动包的命令。</para>
    /// </summary>
    public IRelayCommand InstallCommand { get; }

    /// <summary>Gets whether this row may render the driver action at all. Only driver rows do; every other kind hides
    /// the button instead of disabling it.
    /// <para>获取该行是否可以渲染驱动动作。只有驱动行可以；其它类别隐藏按钮而不是禁用它。</para>
    /// </summary>
    public bool IsDriverRow { get; }

    /// <summary>Gets whether the row offers the expander that reveals each downloaded version and its path.
    /// <para>获取该行是否提供展开箭头，用以显示每个已下载版本及其路径。</para>
    /// </summary>
    public bool CanExpand => Entries.Count > 0;

    /// <summary>Gets the downloaded artifacts behind this row, each with its own path.
    /// <para>获取该行背后逐个下载的产物，每个都带自己的路径。</para>
    /// </summary>
    public IReadOnlyList<ToolboxContentEntryItem> Entries { get; }

    /// <summary>Gets or sets whether the version list is expanded.
    /// <para>获取或设置版本列表是否展开。</para>
    /// </summary>
    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>Gets the icon that mirrors the expander state.
    /// <para>获取反映展开状态的图标。</para>
    /// </summary>
    public string ExpandGlyph => IsExpanded ? "\uE70E" : "\uE70D";

    /// <summary>Gets the entry count summary shown next to the expander.
    /// <para>获取展开箭头旁显示的条目数摘要。</para>
    /// </summary>
    public string EntryCountText => string.Format(
        LanguageService.Instance.GetString("Toolbox_EntryCount"), Entries.Count);

    /// <summary>Toggles the version list visibility.
    /// <para>切换版本列表的可见性。</para>
    /// </summary>
    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ExpandGlyph));

    internal void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(DeclaredSizeText));
        OnPropertyChanged(nameof(PresentSizeText));
        OnPropertyChanged(nameof(FileCountText));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(InstallActionText));
        OnPropertyChanged(nameof(EntryCountText));
    }

    private static string FormatUnavailable(string reasonKey)
    {
        var key = "Toolbox_Unavailable" + reasonKey;
        var text = LanguageService.Instance.GetString(key);
        return string.Equals(text, key, StringComparison.Ordinal)
            ? LanguageService.Instance.GetString("Toolbox_StateUnavailable")
            : text;
    }

    private static string FormatBytes(long bytes) => SettingsViewModel.FormatByteSize(bytes);
}
/// <summary>
/// One downloaded version inside a toolbox row, revealed by the expander with its size and absolute path. The values are
/// read once when the row is built, so the instance is immutable apart from the action it forwards.
/// <para>工具箱行内一个已下载的版本，由展开箭头连同其体积与绝对路径一起显示。取值在构建该行时一次读完，
/// 因此除它所转发的动作外，实例状态保持不可变。</para>
/// </summary>
public sealed class ToolboxContentEntryItem
{
    private readonly ToolboxContentEntry _entry;

    internal ToolboxContentEntryItem(ToolboxContentEntry entry, Action<ToolboxContentEntryItem>? delete)
    {
        _entry = entry;
        DeleteCommand = new RelayCommand(() => delete?.Invoke(this), () => delete is not null);
    }

    /// <summary>Gets the command that deletes this single downloaded artifact, leaving the row's other versions in
    /// place.<para>获取删除这一个已下载产物的命令；该行的其它版本保持不动。</para>
    /// </summary>
    public IRelayCommand DeleteCommand { get; }

    /// <summary>Gets the library or version name as it appears on disk.
    /// <para>获取磁盘上出现的库名或版本名。</para>
    /// </summary>
    public string Name => _entry.Name;

    /// <summary>Gets the raw entry the toolbox service validates against when deleting this artifact.
    /// <para>获取工具箱服务在删除该产物时用于校验的原始条目。</para>
    /// </summary>
    internal ToolboxContentEntry Entry => _entry;

    /// <summary>Gets the absolute path of the downloaded artifact.
    /// <para>获取已下载产物的绝对路径。</para>
    /// </summary>
    public string Path => _entry.Path;

    /// <summary>Gets the formatted size of the artifact.
    /// <para>获取该产物的格式化体积。</para>
    /// </summary>
    public string SizeText => SettingsViewModel.FormatByteSize(_entry.Bytes);
}