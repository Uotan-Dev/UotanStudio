using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace UotanStudio.Views.Nodes;

public partial class NodeEditorView
{
    /// <summary>
    /// Replaces the editor's pending-connection template with one that reuses the control built the first time, so
    /// detaching and re-attaching the view cannot leave a stale pending-connection visual subscribed to the editor.
    /// <para>把编辑器的待处理连线模板换成复用首次构建控件的模板，避免视图卸载再挂载后残留旧的待处理连线控件继续订阅编辑器事件。</para>
    /// </summary>
    private void PreservePendingConnectionVisual()
    {
        if (Editor.PendingConnectionTemplate is { } template and not CachedPendingConnectionTemplate)
        {
            Editor.PendingConnectionTemplate = new CachedPendingConnectionTemplate(template);
        }
    }

    /// <summary>
    /// Wraps a template and returns its first control on every build. NodifyM's pending-connection control subscribes
    /// to the editor's connector events when its template is applied and never unsubscribes; a second template
    /// application would therefore keep the old control handling those events while the visible new control stays
    /// hidden, which stops connection dragging after the view is re-hosted.
    /// <para>包装模板并在每次构建时返回第一次创建的控件。NodifyM 的待处理连线控件在应用模板时订阅编辑器的连接器事件且从不退订；
    /// 再次应用模板会让旧控件继续处理这些事件，而新的可见控件始终不显示，导致视图重新挂载后无法拖拽连线。</para>
    /// </summary>
    /// <param name="inner">The template that builds the pending-connection control.<para>构建待处理连线控件的模板。</para></param>
    internal sealed class CachedPendingConnectionTemplate(IDataTemplate inner) : IDataTemplate
    {
        private Control? _visual;

        /// <summary>
        /// Builds the pending-connection control once and returns that same instance afterwards.
        /// <para>只构建一次待处理连线控件，之后返回同一实例。</para>
        /// </summary>
        /// <param name="param">The data item the editor presents through this template.<para>编辑器经此模板呈现的数据项。</para></param>
        /// <returns>The cached control, or <c>null</c> when the data item is <c>null</c>.<para>缓存的控件；数据项为 <c>null</c> 时返回 <c>null</c>。</para></returns>
        public Control? Build(object? param)
        {
            if (param is null)
            {
                return null;
            }

            return _visual ??= inner.Build(param);
        }

        /// <summary>
        /// Defers matching to the wrapped template.
        /// <para>匹配判断委托给被包装的模板。</para>
        /// </summary>
        /// <param name="data">The data item to test.<para>待判断的数据项。</para></param>
        /// <returns>The wrapped template's match result.<para>被包装模板的匹配结果。</para></returns>
        public bool Match(object? data) => inner.Match(data);
    }
}