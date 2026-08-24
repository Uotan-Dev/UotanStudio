using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Mvvm.Controls;
using UotanStudio.Services;

namespace UotanStudio.ViewModels
{
    /// <summary>
    /// <para>输出视图模型，维护工作区特定日志集合，供 OutputView 显示。仅接收由工作区自身操作（打包/合成/分解等）通过 <see cref="Write"/> 推送的日志，不订阅全局 <see cref="LogService"/> 流；同时将每条日志转发到全局 <see cref="LogService"/>，使其出现在全局日志面板。</para>
    /// Output view model, maintaining a workspace-specific log collection for the OutputView. Only receives logs pushed via <see cref="Write"/> by workspace operations (pack/synthesis/extract etc.); it does NOT subscribe to the global <see cref="LogService"/> stream. Each entry is also forwarded to the global <see cref="LogService"/> so it appears in the global log panel.
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
        /// <para>构造输出视图模型。不再订阅全局日志流；仅接收由工作区操作通过 <see cref="Write"/> 显式推送的条目。</para>
        /// Constructs the output view model. Does not subscribe to the global log stream; only receives entries explicitly pushed via <see cref="Write"/> by workspace operations.
        /// </summary>
        public OutputViewModel()
        {
        }

        /// <summary>
        /// <para>写入一条工作区特定日志：追加到本面板集合并裁剪超限条目，同时转发到全局 <see cref="LogService"/> 使其出现在全局日志面板。</para>
        /// Writes a workspace-specific log entry: appends to this panel's collection (trimming over-limit entries) and forwards to the global <see cref="LogService"/> so it also appears in the global log panel.
        /// </summary>
        /// <param name="level"><para>日志级别。</para>Log level.</param>
        /// <param name="source"><para>日志来源。</para>Log source.</param>
        /// <param name="message"><para>日志文本。</para>Log text.</param>
        public void Write(LogLevel level, string source, string message)
        {
            AppendEntry(DateTime.Now, level, source, message);
            while (Entries.Count > MaxEntries && Entries.Count > 0)
            {
                Entries.RemoveAt(0);
            }
            // 转发到全局日志服务，使其出现在全局日志面板
            LogService.Instance.Write(level, source, message);
        }

        /// <summary>
        /// <para>追加一条日志到集合。</para>
        /// Appends a log entry to the collection.
        /// </summary>
        private void AppendEntry(DateTime timestamp, LogLevel level, string source, string message)
        {
            var ts = timestamp.ToString("HH:mm:ss");
            Entries.Add(new LogEntryViewModel(ts, level, source, message));
        }

        /// <summary>
        /// <para>清空当前输出集合（不影响 LogService 缓冲）。</para>
        /// Clears the current output collection (does not affect the LogService buffer).
        /// </summary>
        public void Clear()
        {
            Entries.Clear();
        }
    }
}