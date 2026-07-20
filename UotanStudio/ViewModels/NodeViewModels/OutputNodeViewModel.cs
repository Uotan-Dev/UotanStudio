using Avalonia;
using System.Threading;
using System.Threading.Tasks;

namespace UotanStudio.ViewModels.NodeViewModels
{
    /// <summary>
    /// <para>输出节点视图模型，提供日志输出、进度报告和结果保存功能。</para>
    /// Output node view model, providing log output, progress report, and result saving capabilities.
    /// <para>可选通过构造函数注入 <see cref="LogViewModel"/>，运行时将日志写入面板。</para>
    /// Optionally injects a <see cref="LogViewModel"/> via the constructor; runtime writes logs to the panel.
    /// </summary>
    public class OutputNodeViewModel : FlowNodeViewModel
    {
        private readonly LogViewModel? _logVm;

        /// <summary>
        /// <para>创建具有指定标题的输出节点。</para>
        /// Creates an output node with the specified title.
        /// </summary>
        /// <param name="title">
        /// <para>节点的显示标题。</para>
        /// The display title of the node.
        /// </param>
        /// <param name="logVm">
        /// <para>可选的日志视图模型，用于将输出写入日志面板。</para>
        /// Optional log view model, used to write output to the log panel.
        /// </param>
        public OutputNodeViewModel(string title, LogViewModel? logVm = null) : base(title, NodeCategory.Output)
        {
            _logVm = logVm;
        }

        /// <summary>
        /// <para>执行该输出节点的真实操作，依据节点标题分派到 Log/Progress/Save 逻辑。</para>
        /// Executes the real operation of this output node, dispatching to Log/Progress/Save logic based on the node title.
        /// </summary>
        /// <param name="messageOrData">日志消息（Log）、进度数值（Progress）或结果数据（Save）。</param>
        /// <param name="levelOrStatusOrPath">日志级别（Log）、状态文本（Progress）或保存路径（Save）。</param>
        /// <param name="cancellationToken">取消令牌。</param>
        /// <returns>执行结果摘要文本。</returns>
        public async Task<string> ExecuteAsync(string messageOrData, string levelOrStatusOrPath, CancellationToken cancellationToken)
        {
            switch (Title)
            {
                case "Log Output":
                    {
                        var level = string.IsNullOrEmpty(levelOrStatusOrPath) ? "INFO" : levelOrStatusOrPath.ToUpperInvariant();
                        AppendLog(level, "Node", messageOrData ?? string.Empty);
                        return $"[Log Output] OK: {messageOrData}";
                    }
                case "Progress Report":
                    {
                        var status = string.IsNullOrEmpty(levelOrStatusOrPath) ? "running" : levelOrStatusOrPath;
                        AppendLog("INFO", "Progress", $"{messageOrData}% — {status}");
                        return $"[Progress Report] OK: {messageOrData}% ({status})";
                    }
                case "Save Result":
                    {
                        var path = levelOrStatusOrPath;
                        if (string.IsNullOrEmpty(path))
                            return "[Save Result] FAIL: save path is empty.";
                        var data = messageOrData ?? string.Empty;
                        await System.IO.File.WriteAllTextAsync(path, data, cancellationToken).ConfigureAwait(false);
                        return $"[Save Result] OK: saved {data.Length} chars to {path}";
                    }
                default:
                    return $"[Output] Unknown node: {Title}";
            }
        }

        /// <summary>
        /// <para>将一条日志写入注入的 LogViewModel（若存在）。</para>
        /// Writes a log entry to the injected LogViewModel, if present.
        /// </summary>
        private void AppendLog(string level, string source, string message)
        {
            if (_logVm is null) return;
            _logVm.Entries.Add(new LogEntry
            {
                Level = level,
                Source = source,
                Message = message
            });
        }

        /// <summary>
        /// <para>创建默认的日志输出节点。</para>
        /// Creates a default log output node.
        /// </summary>
        /// <param name="logVm">可选的日志视图模型。</param>
        public static OutputNodeViewModel CreateLogNode(LogViewModel? logVm = null)
        {
            var node = new OutputNodeViewModel("Log Output", logVm)
            {
                Location = new Point(100, 100)
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Message" });
            node.Input.Add(new NodeConnectorViewModel { Title = "Level" });
            return node;
        }

        /// <summary>
        /// <para>创建默认的进度报告节点。</para>
        /// Creates a default progress report node.
        /// </summary>
        /// <param name="logVm">可选的日志视图模型。</param>
        public static OutputNodeViewModel CreateProgressNode(LogViewModel? logVm = null)
        {
            var node = new OutputNodeViewModel("Progress Report", logVm)
            {
                Location = new Point(100, 100)
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Progress" });
            node.Input.Add(new NodeConnectorViewModel { Title = "Status" });
            return node;
        }

        /// <summary>
        /// <para>创建默认的结果保存节点。</para>
        /// Creates a default result save node.
        /// </summary>
        /// <param name="logVm">可选的日志视图模型。</param>
        public static OutputNodeViewModel CreateSaveNode(LogViewModel? logVm = null)
        {
            var node = new OutputNodeViewModel("Save Result", logVm)
            {
                Location = new Point(100, 100)
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Data" });
            node.Input.Add(new NodeConnectorViewModel { Title = "Path" });
            return node;
        }
    }
}
