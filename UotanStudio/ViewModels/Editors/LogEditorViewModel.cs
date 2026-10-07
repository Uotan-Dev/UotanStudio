using System.Collections.Specialized;
using System.Text.RegularExpressions;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Services;
using UotanStudio.Services.Logging;

namespace UotanStudio.ViewModels.Editors;

/// <summary>Read-only offline log document with full-line filtering. 支持整行筛选的只读离线日志文档。</summary>
public partial class LogEditorViewModel : Document, ILogViewModel
{
    private IReadOnlyList<ILogLine> _allEntries = [];
    private CancellationTokenSource? _load;
    private Regex? _searchPattern;
    private bool _closed;
    Workspace.AdbLogViewModel? ILogViewModel.LiveModel => null;
    LogEditorViewModel? ILogViewModel.FileModel => this;

    /// <summary>Opened file path. 打开的文件路径。</summary>
    [ObservableProperty] private string _filePath = string.Empty;
    /// <summary>Whether the document is loading. 是否正在加载文档。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReloadCommand))]
    private bool _isLoading;
    /// <summary>Load or export error. 加载或导出错误。</summary>
    [ObservableProperty] private string? _errorMessage;
    /// <summary>Full-line query. 整行查询文本。</summary>
    [ObservableProperty] private string _searchText = string.Empty;
    /// <summary>Regular-expression search. 正则表达式搜索。</summary>
    [ObservableProperty] private bool _useRegex;
    /// <summary>Case-sensitive search. 区分大小写搜索。</summary>
    [ObservableProperty] private bool _matchCase;
    /// <summary>Exact severity filter. 精确等级筛选。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedLevelIndex))]
    private LogLevel? _levelFilter;
    /// <summary>Search validation error. 搜索验证错误。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchError))]
    private string? _searchError;

    /// <summary>Current matching entries. 当前匹配的条目。</summary>
    public IReadOnlyList<ILogLine> Entries { get; private set; } = [];
    /// <summary>Notifies the view after a filtered snapshot changes. 筛选快照变更通知。</summary>
    public event NotifyCollectionChangedEventHandler? EntriesChanged;
    /// <summary>Available severity filters. 可用等级筛选。</summary>
    public IReadOnlyList<LogLevel?> Levels { get; } = [null, LogLevel.Trace, LogLevel.Debug, LogLevel.Info, LogLevel.Warn, LogLevel.Error, LogLevel.Fatal];
    /// <summary>Index-based selection preserves the All item. 基于索引的选项，保留“全部”语义。</summary>
    public int SelectedLevelIndex
    {
        get => LevelFilter.HasValue ? (int)LevelFilter.Value + 1 : 0;
        set { if (value >= 0 && value < Levels.Count) LevelFilter = Levels[value]; }
    }
    /// <summary>Whether the query has an error. 查询是否有错误。</summary>
    public bool HasSearchError => SearchError is not null;
    bool ILogViewModel.AutoScroll { get => false; set { } }
    bool ILogViewModel.IsPaused => false;
    ICommand ILogViewModel.ClearSearchCommand => ClearSearchCommand;

    partial void OnLevelFilterChanged(LogLevel? value) => ReapplyFilter();
    partial void OnSearchTextChanged(string value) => UpdateSearchFilter();
    partial void OnUseRegexChanged(bool value) => UpdateSearchFilter();
    partial void OnMatchCaseChanged(bool value) => UpdateSearchFilter();

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    private bool CanReload() => !_closed && !IsLoading && !string.IsNullOrEmpty(FilePath);
    [RelayCommand(CanExecute = nameof(CanReload))]
    private Task Reload() => LoadAsync(FilePath);

    /// <summary>Loads all supported entries, replacing the document only on success. 加载全部受支持条目，仅成功后替换文档。</summary>
    public async Task LoadAsync(string path)
    {
        if (_closed) return;
        _load?.Cancel();
        var load = _load = new CancellationTokenSource();
        var fullPath = Path.GetFullPath(path);
        FilePath = fullPath;
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var entries = await Task.Run(() => LogFileService.ReadAsync(fullPath, load.Token), load.Token);
            if (_closed || !ReferenceEquals(_load, load)) return;
            _allEntries = entries;
            ReapplyFilter();
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_closed && ReferenceEquals(_load, load))
                ErrorMessage = LanguageService.Instance.GetString("Log_FileLoadFailed", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_load, load))
            {
                _load = null;
                IsLoading = false;
            }
            load.Dispose();
        }
    }

    private void UpdateSearchFilter()
    {
        _searchPattern = null;
        SearchError = null;
        if (UseRegex && !string.IsNullOrEmpty(SearchText))
        {
            try
            {
                var options = RegexOptions.CultureInvariant;
                if (!MatchCase) options |= RegexOptions.IgnoreCase;
                _searchPattern = new Regex(SearchText, options, TimeSpan.FromMilliseconds(50));
            }
            catch (ArgumentException ex) { SearchError = LanguageService.Instance.GetString("Log_RegexInvalid", ex.Message); }
        }
        ReapplyFilter();
    }

    private void ReapplyFilter()
    {
        List<ILogLine> matches = [];
        if (!HasSearchError)
            foreach (var entry in _allEntries)
            {
                if (LevelFilter.HasValue && entry.Level != LevelFilter) continue;
                try
                {
                    if (string.IsNullOrEmpty(SearchText) || (UseRegex ? _searchPattern?.IsMatch(entry.Text) == true
                        : entry.Text.Contains(SearchText, MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)))
                        matches.Add(entry);
                }
                catch (RegexMatchTimeoutException)
                {
                    SearchError = LanguageService.Instance.GetString("Log_RegexTimeout");
                    matches.Clear();
                    break;
                }
            }
        Entries = matches;
        EntriesChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>Cancels pending loading and releases file history on close. 关闭时取消加载并释放文件历史。</summary>
    public void Cleanup()
    {
        _closed = true;
        _load?.Cancel();
        _allEntries = [];
        ReapplyFilter();
        ReloadCommand.NotifyCanExecuteChanged();
    }
}