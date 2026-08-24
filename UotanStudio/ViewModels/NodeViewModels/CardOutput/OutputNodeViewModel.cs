using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Execution;

namespace UotanStudio.ViewModels.NodeViewModels
{
    /// <summary>
    /// <para>输出节点视图模型，提供日志输出、进度报告和结果保存功能。</para>
    /// Output node view model, providing log output, progress report, and result saving capabilities.
    /// </summary>
    public class OutputNodeViewModel : FlowNodeViewModel
    {
        /// <summary>
        /// <para>创建具有指定标题的输出节点。</para>
        /// Creates an output node with the specified title.
        /// </summary>
        /// <param name="title">
        /// <para>节点的显示标题。</para>
        /// The display title of the node.
        /// </param>
        public OutputNodeViewModel(string title) : base(title, NodeCategory.Output)
        {
        }

        /// <summary>
        /// <para>创建默认的日志输出节点。</para>
        /// Creates a default log output node.
        /// </summary>
        public static OutputNodeViewModel CreateLogNode()
        {
            var node = new OutputNodeViewModel("Log Output")
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
        public static OutputNodeViewModel CreateProgressNode()
        {
            var node = new OutputNodeViewModel("Progress Report")
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
        public static OutputNodeViewModel CreateSaveNode()
        {
            var node = new OutputNodeViewModel("Save Result")
            {
                Location = new Point(100, 100)
            };
            node.Input.Add(new NodeConnectorViewModel { Title = "Data" });
            node.Input.Add(new NodeConnectorViewModel { Title = "Path" });
            return node;
        }

        /// <summary>
        /// <para>执行与节点标题对应的输出逻辑：Log Output 写日志、Progress Report 上报进度、Save Result 保存文件。</para>
        /// Executes the output logic matching the node title: Log Output writes a log entry, Progress Report reports progress,
        /// and Save Result writes a file.
        /// </summary>
        /// <param name="context">
        /// <para>执行上下文。</para>
        /// The execution context.
        /// </param>
        /// <returns>
        /// <para>表示异步执行的任务。</para>
        /// A task representing the asynchronous execution.
        /// </returns>
        public override Task ExecuteAsync(INodeExecutionContext context)
        {
            switch (Title)
            {
                case "Log Output":
                    ExecuteLog(context);
                    break;
                case "Progress Report":
                    ExecuteProgress(context);
                    break;
                case "Save Result":
                    ExecuteSave(context);
                    break;
                default:
                    return base.ExecuteAsync(context);
            }

            context.CancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        private void ExecuteLog(INodeExecutionContext context)
        {
            var message = NodeDataUtil.ToDisplay(ReadInput("Message"));
            var levelText = (Convert.ToString(ReadInput("Level"), CultureInfo.InvariantCulture) ?? "info").Trim().ToLowerInvariant();
            var level = ParseLogLevel(levelText) ?? LogLevel.Info;
            context.Log.Write(level, Title, message);
        }

        private void ExecuteProgress(INodeExecutionContext context)
        {
            var progress = NodeDataUtil.ToDouble(ReadInput("Progress"), 0);
            context.ReportProgress(Math.Clamp(progress, 0.0, 1.0));
            var status = NodeDataUtil.ToDisplay(ReadInput("Status"));
            var percent = Math.Clamp(progress, 0.0, 1.0) * 100;
            context.Log.Write(LogLevel.Info, Title,
                string.IsNullOrEmpty(status) ? $"Progress {percent:0.0}%  进度。" : $"Progress {percent:0.0}% — {status}  进度。");
        }

        private void ExecuteSave(INodeExecutionContext context)
        {
            var data = NodeDataUtil.ToBytes(ReadInput("Data")) ?? [];
            var path = Convert.ToString(ReadInput("Path"), CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(path))
            {
                context.Log.Write(LogLevel.Warn, Title, "No output Path.  缺少输出路径。");
                return;
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(path, data);
            context.Log.Write(LogLevel.Info, Title, $"Saved {data.Length} bytes to '{path}'  已保存。");
        }

        private static LogLevel? ParseLogLevel(string text)
        {
            return text switch
            {
                "trace" => LogLevel.Trace,
                "debug" => LogLevel.Debug,
                "info" or "information" => LogLevel.Info,
                "warn" or "warning" => LogLevel.Warn,
                "error" => LogLevel.Error,
                "fatal" or "critical" => LogLevel.Fatal,
                _ => null
            };
        }
    }
}