using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Abstractions.Logging;
using UotanStudio.Services;

namespace UotanStudio.ViewModels
{
    /// <summary>
    /// <para>输出视图模型，订阅 <see cref="LogService"/> 日志条目推送并维护可绑定集合，供 OutputView 显示。</para>
    /// Output view model, subscribing to <see cref="LogService"/> log entry pushes and maintaining a bindable collection for the OutputView to display.
    /// </summary>
    internal partial class OutputViewModel : Tool
    {
        /// <summary>
        /// <para>日志条目视图模型，承载单条日志的时间戳、级别、来源与文本。</para>
        /// Log entry view model, carrying a single log entry's timestamp, level, source and text.
        /// </summary>
        public sealed partial class LogEntryViewModel : ObservableObject
        {
            /// <summary>
            /// <para>本地时间戳文本（HH:mm:ss）。</para>
            /// Local timestamp text (HH:mm:ss).
            /// </summary>
            [ObservableProperty] private string _timestamp = string.Empty;

            /// <summary>
            /// <para>日志级别。</para>
            /// Log level.
            /// </summary>
            [ObservableProperty] private LogLevel _level = LogLevel.Info;

            /// <summary>
            /// <para>日志来源。</para>
            /// Log source.
            /// </summary>
            [ObservableProperty] private string _source = string.Empty;

            /// <summary>
            /// <para>日志文本。</para>
            /// Log text.
            /// </summary>
            [ObservableProperty] private string _message = string.Empty;

            /// <summary>
            /// <para>创建日志条目视图模型。</para>
            /// Creates a log entry view model.
            /// </summary>
            public LogEntryViewModel(string timestamp, LogLevel level, string source, string message)
            {
                _timestamp = timestamp;
                _level = level;
                _source = source;
                _message = message;
            }
        }

        /// <summary>
        /// <para>当前输出窗口的日志条目集合，UI 绑定此项。</para>
        /// Collection of log entries in the current output window, bound by the UI.
        /// </summary>
        public ObservableCollection<LogEntryViewModel> Entries { get; } = new();

        /// <summary>
        /// <para>累计条目数，用于自动裁剪防止无限增长。</para>
        /// Cumulative entry count, used for automatic trimming to prevent unbounded growth.
        /// </summary>
        private const int MaxEntries = 2000;

        /// <summary>
        /// <para>取消订阅委托。</para>
        /// Unsubscribe delegate.
        /// </summary>
        private Action? _unsubscribe;

        /// <summary>
        /// <para>构造输出视图模型，订阅统一日志服务。</para>
        /// Constructs the output view model, subscribing to the unified log service.
        /// </summary>
        public OutputViewModel()
        {
            _unsubscribe = LogService.Instance.Subscribe(OnLogEnqueued);
            // 回填当前缓冲区中的历史条目
            foreach (var e in LogService.Instance.Entries)
            {
                AppendEntry(e);
            }
        }

        /// <summary>
        /// <para>日志条目入队回调，追加到集合并裁剪超限条目。</para>
        /// Log entry enqueue callback, appending to the collection and trimming over-limit entries.
        /// </summary>
        /// <param name="sender">
        /// <para>事件源。</para>
        /// Event source.
        /// </param>
        /// <param name="entry">
        /// <para>日志条目记录。</para>
        /// Log entry record.
        /// </param>
        private void OnLogEnqueued(object? sender, LogEntry entry)
        {
            AppendEntry(entry);
            // 裁剪超限条目
            while (Entries.Count > MaxEntries && Entries.Count > 0)
            {
                Entries.RemoveAt(0);
            }
        }

        /// <summary>
        /// <para>追加一条日志到集合。</para>
        /// Appends a log entry to the collection.
        /// </summary>
        private void AppendEntry(LogEntry entry)
        {
            var ts = entry.Timestamp.ToString("HH:mm:ss");
            Entries.Add(new LogEntryViewModel(ts, entry.Level, entry.Source, entry.Message));
        }

        /// <summary>
        /// <para>清空当前输出集合（不影响 LogService 缓冲）。</para>
        /// Clears the current output collection (does not affect the LogService buffer).
        /// </summary>
        public void Clear()
        {
            Entries.Clear();
        }

        /// <summary>
        /// <para>析构时取消订阅，防止内存泄漏。</para>
        /// Unsubscribes on finalization to prevent memory leaks.
        /// </summary>
        ~OutputViewModel()
        {
            try { _unsubscribe?.Invoke(); } catch { /* 忽略析构异常 */ }
        }
    }
}
