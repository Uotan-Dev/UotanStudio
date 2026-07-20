using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using UotanStudio.Services;

namespace UotanStudio.ViewModels
{
    /// <summary>
    /// <para>工具箱日志面板视图模型，订阅 LogAggregator 将日志按分类分发到独立集合。</para>
    /// Toolbox log panel view model, subscribing to LogAggregator and dispatching logs into separate collections by category.
    /// <para>提供分类切换、文本过滤与清空命令，供 NodeEditorView 左侧日志面板绑定。</para>
    /// Provides category switching, text filtering, and a clear command, for binding to the toolbox log panel on the left of NodeEditorView.
    /// </summary>
    public partial class ToolboxLogViewModel : ViewModelBase
    {
        /// <summary>
        /// <para>单个日志条目模型，对应 LogAggregator 的一次 Append。</para>
        /// Single log entry model, corresponding to one LogAggregator Append.
        /// </summary>
        public class Entry
        {
            /// <summary>日志级别（INFO/WARN/ERROR 等）。</summary>
            public string Level { get; init; } = "INFO";
            /// <summary>日志来源标签。</summary>
            public string Source { get; init; } = string.Empty;
            /// <summary>日志消息文本。</summary>
            public string Message { get; init; } = string.Empty;
            /// <summary>日志产生时间（本地时间）。</summary>
            public DateTime Timestamp { get; init; } = DateTime.Now;
        }

        private const int MaxEntriesPerCategory = 500;

        private ToolboxLogCategory _activeCategory = ToolboxLogCategory.Toolbox;
        private string _filter = string.Empty;
        private bool _autoScroll = true;
        private bool _isPaused;

        /// <summary>
        /// <para>获取或设置当前激活的日志分类。变更时刷新可见集合。</para>
        /// Gets or sets the currently active log category. Refreshes the visible collection on change.
        /// </summary>
        public ToolboxLogCategory ActiveCategory
        {
            get => _activeCategory;
            set
            {
                if (SetProperty(ref _activeCategory, value))
                    RefreshVisible();
            }
        }

        /// <summary>
        /// <para>获取或设置日志过滤文本。变更时刷新可见集合。</para>
        /// Gets or sets the log filter text. Refreshes the visible collection on change.
        /// </summary>
        public string Filter
        {
            get => _filter;
            set
            {
                if (SetProperty(ref _filter, value))
                    RefreshVisible();
            }
        }

        /// <summary>
        /// <para>获取或设置是否自动滚动到最新条目。</para>
        /// Gets or sets whether to auto-scroll to the latest entry.
        /// </summary>
        public bool AutoScroll
        {
            get => _autoScroll;
            set => SetProperty(ref _autoScroll, value);
        }

        /// <summary>
        /// <para>获取或设置是否暂停接收日志（暂停时新条目被丢弃，避免日志爆炸）。</para>
        /// Gets or sets whether log intake is paused (new entries are dropped while paused, avoiding log explosion).
        /// </summary>
        public bool IsPaused
        {
            get => _isPaused;
            set => SetProperty(ref _isPaused, value);
        }

        /// <summary>
        /// <para>获取可供切换的全部日志分类（供分类切换栏绑定）。</para>
        /// Gets all available log categories (for binding to the category switcher bar).
        /// </summary>
        public ObservableCollection<ToolboxLogCategory> Categories { get; } = [
            ToolboxLogCategory.Toolbox,
            ToolboxLogCategory.ADB,
            ToolboxLogCategory.Fastboot,
            ToolboxLogCategory.Firmware,
            ToolboxLogCategory.Node
        ];

        /// <summary>
        /// <para>获取工具箱分类的原始日志条目集合。</para>
        /// Gets the raw log entry collection for the Toolbox category.
        /// </summary>
        public ObservableCollection<Entry> ToolboxEntries { get; } = [];

        /// <summary>
        /// <para>获取 ADB 分类的原始日志条目集合。</para>
        /// Gets the raw log entry collection for the ADB category.
        /// </summary>
        public ObservableCollection<Entry> AdbEntries { get; } = [];

        /// <summary>
        /// <para>获取 Fastboot 分类的原始日志条目集合。</para>
        /// Gets the raw log entry collection for the Fastboot category.
        /// </summary>
        public ObservableCollection<Entry> FastbootEntries { get; } = [];

        /// <summary>
        /// <para>获取固件分类的原始日志条目集合。</para>
        /// Gets the raw log entry collection for the Firmware category.
        /// </summary>
        public ObservableCollection<Entry> FirmwareEntries { get; } = [];

        /// <summary>
        /// <para>获取节点流程分类的原始日志条目集合。</para>
        /// Gets the raw log entry collection for the Node category.
        /// </summary>
        public ObservableCollection<Entry> NodeEntries { get; } = [];

        /// <summary>
        /// <para>获取当前激活分类下经过滤的可见条目集合（供 UI 绑定）。</para>
        /// Gets the filtered visible entry collection for the active category (for UI binding).
        /// </summary>
        public ObservableCollection<Entry> VisibleEntries { get; } = [];

        /// <summary>
        /// <para>初始化工具箱日志视图模型，订阅 LogAggregator 事件。</para>
        /// Initializes the toolbox log view model, subscribing to the LogAggregator event.
        /// </summary>
        public ToolboxLogViewModel()
        {
            LogAggregator.LogAppended += OnLogAppended;
        }

        /// <summary>
        /// <para>处理 LogAggregator 推送：按分类分发到对应集合，并按需刷新可见集合。</para>
        /// Handles a LogAggregator dispatch: routes to the matching category collection, refreshing visible entries as needed.
        /// </summary>
        /// <param name="sender">事件发送者（未使用）。</param>
        /// <param name="e">日志条目事件参数。</param>
        private void OnLogAppended(object? sender, ToolboxLogEventArgs e)
        {
            if (IsPaused) return;
            // LogAggregator 可能在后台线程触发；UI 集合变更必须切回 UI 线程
            Dispatcher.UIThread.Post(() => AppendEntry(e));
        }

        /// <summary>
        /// <para>将一条日志追加到对应分类集合（UI 线程调用），超出上限时丢弃最旧条目。</para>
        /// Appends a log entry to the matching category collection (UI thread); drops the oldest entry when over the cap.
        /// </summary>
        /// <param name="e">日志条目事件参数。</param>
        private void AppendEntry(ToolboxLogEventArgs e)
        {
            var target = e.Category switch
            {
                ToolboxLogCategory.Toolbox => ToolboxEntries,
                ToolboxLogCategory.ADB => AdbEntries,
                ToolboxLogCategory.Fastboot => FastbootEntries,
                ToolboxLogCategory.Firmware => FirmwareEntries,
                ToolboxLogCategory.Node => NodeEntries,
                _ => ToolboxEntries
            };

            var entry = new Entry
            {
                Level = e.Level,
                Source = e.Source,
                Message = e.Message,
                Timestamp = e.Timestamp
            };

            target.Add(entry);
            // 上限控制：保持最近 MaxEntriesPerCategory 条
            if (target.Count > MaxEntriesPerCategory)
                target.RemoveAt(0);

            // 仅当当前可视分类匹配时才刷新可视集合，避免无关分类刷屏
            if (e.Category == _activeCategory)
                RefreshVisible(entry);
        }

        /// <summary>
        /// <para>根据当前过滤文本刷新可见集合，可选附加最新条目以减少全量重建。</para>
        /// Refreshes the visible collection based on the current filter text; optionally appends the latest entry to reduce full rebuilds.
        /// </summary>
        /// <param name="latest">刚刚追加的最新条目；若匹配过滤则直接附加而非全量重建。</param>
        private void RefreshVisible(Entry? latest = null)
        {
            var source = _activeCategory switch
            {
                ToolboxLogCategory.Toolbox => ToolboxEntries,
                ToolboxLogCategory.ADB => AdbEntries,
                ToolboxLogCategory.Fastboot => FastbootEntries,
                ToolboxLogCategory.Firmware => FirmwareEntries,
                ToolboxLogCategory.Node => NodeEntries,
                _ => ToolboxEntries
            };

            var keyword = _filter?.Trim() ?? string.Empty;

            // 快速路径：过滤为空且仅追加一条时直接附加
            if (latest is not null && string.IsNullOrEmpty(keyword))
            {
                VisibleEntries.Add(latest);
                if (VisibleEntries.Count > MaxEntriesPerCategory)
                    VisibleEntries.RemoveAt(0);
                return;
            }

            // 全量重建：过滤后重新填充
            VisibleEntries.Clear();
            foreach (var e in source)
            {
                if (string.IsNullOrEmpty(keyword)
                    || (e.Message?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (e.Source?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (e.Level?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false))
                {
                    VisibleEntries.Add(e);
                }
            }
        }

        /// <summary>
        /// <para>切换到指定日志分类。</para>
        /// Switches to the specified log category.
        /// </summary>
        /// <param name="category">目标分类。</param>
        [RelayCommand]
        private void SwitchCategory(ToolboxLogCategory category)
        {
            ActiveCategory = category;
        }

        /// <summary>
        /// <para>清空当前激活分类的日志条目。</para>
        /// Clears the log entries of the currently active category.
        /// </summary>
        [RelayCommand]
        private void ClearActive()
        {
            var target = _activeCategory switch
            {
                ToolboxLogCategory.Toolbox => ToolboxEntries,
                ToolboxLogCategory.ADB => AdbEntries,
                ToolboxLogCategory.Fastboot => FastbootEntries,
                ToolboxLogCategory.Firmware => FirmwareEntries,
                ToolboxLogCategory.Node => NodeEntries,
                _ => ToolboxEntries
            };
            target.Clear();
            VisibleEntries.Clear();
        }

        /// <summary>
        /// <para>切换暂停状态（暂停时新日志被丢弃）。</para>
        /// Toggles the paused state (new logs are dropped while paused).
        /// </summary>
        [RelayCommand]
        private void TogglePause()
        {
            IsPaused = !IsPaused;
        }
    }
}
