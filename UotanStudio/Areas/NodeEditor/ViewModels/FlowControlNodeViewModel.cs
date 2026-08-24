using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using UotanStudio.Services;

namespace UotanStudio.Areas.NodeEditor.ViewModels;

/// <summary>
/// <para>流程控制节点视图模型，提供条件分支、循环和等待功能。</para>
/// Flow control node view model, providing conditional branch, loop, and wait capabilities.
/// </summary>
/// <remarks>
/// <para>创建具有指定固定键的流程控制节点，显示标题按键本地化。</para>
/// Creates a flow control node with the specified fixed key; the display title is localized by the key.
/// </remarks>
/// <param name="key">
/// <para>节点的固定键（本地化资源键）。</para>
/// The fixed key of the node (localization resource key).
/// </param>
public class FlowControlNodeViewModel(string key) : FlowNodeViewModel(LanguageService.Instance.GetString(key), NodeCategory.Flow, key)
{
    /// <summary>
    /// <para>执行该流程控制节点的真实操作，依据节点固定键分派到 If/Loop/Wait 逻辑。</para>
    /// Executes the real operation of this flow control node, dispatching to If/Loop/Wait logic based on the node key.
    /// </summary>
    /// <param name="conditionOrCountOrDelay">条件值（If）、循环次数（Loop）或等待毫秒（Wait）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>执行结果摘要文本；结果数据也会写回节点的 Output 端口 Value。</returns>
    public async Task<string> ExecuteAsync(string conditionOrCountOrDelay, CancellationToken cancellationToken)
    {
        try
        {
            switch (Key)
            {
                case NodeKeys.FlowIf:
                    {
                        var cond = (conditionOrCountOrDelay ?? string.Empty).Trim();
                        var isTrue = cond.Length > 0 &&
                                     cond != "0" &&
                                     !string.Equals(cond, "false", System.StringComparison.OrdinalIgnoreCase);
                        for (var i = 0; i < Output.Count; i++)
                        {
                            Output[i].Value = i == 0 == isTrue ? "1" : "0";
                        }
                        return isTrue ? "[If Branch] OK: condition is TRUE." : "[If Branch] OK: condition is FALSE.";
                    }
                case NodeKeys.FlowLoop:
                    {
                        if (!int.TryParse(conditionOrCountOrDelay, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count < 0)
                        {
                            return "[Loop] FAIL: invalid count.";
                        }

                        for (var i = 0; i <= count; i++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (Output.Count > 0)
                            {
                                Output[0].Value = i.ToString(CultureInfo.InvariantCulture);
                            }

                            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                        }
                        return $"[Loop] OK: iterated {count} times.";
                    }
                case NodeKeys.FlowWait:
                    {
                        if (!int.TryParse(conditionOrCountOrDelay, NumberStyles.Integer, CultureInfo.InvariantCulture, out var delayMs) || delayMs < 0)
                        {
                            return "[Wait] FAIL: invalid delay.";
                        }

                        await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                        if (Output.Count > 0)
                        {
                            Output[0].Value = "1";
                        }

                        return $"[Wait] OK: waited {delayMs} ms.";
                    }
                default:
                    return $"[Flow] Unknown node: {Title}";
            }
        }
        catch (System.OperationCanceledException)
        {
            throw;
        }
        catch (System.Exception ex)
        {
            return $"[{Title}] FAIL: {ex.Message}";
        }
    }

    /// <summary>创建默认的条件分支节点。</summary>
    public static FlowControlNodeViewModel CreateIfNode()
    {
        var node = new FlowControlNodeViewModel(NodeKeys.FlowIf)
        {
            Location = new Point(100, 100)
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Condition" });
        node.Output.Add(new NodeConnectorViewModel { Title = "True" });
        node.Output.Add(new NodeConnectorViewModel { Title = "False" });
        return node;
    }

    /// <summary>创建默认的循环节点。</summary>
    public static FlowControlNodeViewModel CreateLoopNode()
    {
        var node = new FlowControlNodeViewModel(NodeKeys.FlowLoop)
        {
            Location = new Point(100, 100)
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Count" });
        node.Output.Add(new NodeConnectorViewModel { Title = "Index" });
        return node;
    }

    /// <summary>创建默认的等待节点。</summary>
    public static FlowControlNodeViewModel CreateWaitNode()
    {
        var node = new FlowControlNodeViewModel(NodeKeys.FlowWait)
        {
            Location = new Point(100, 100)
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Delay" });
        node.Output.Add(new NodeConnectorViewModel { Title = "Done" });
        return node;
    }
}