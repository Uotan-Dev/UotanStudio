using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using UotanStudio.ViewModels.Editors;

namespace UotanStudio.Views.Editors;

/// <summary>
/// QCN editor view: NV item list on the left, structured value and raw data editing on the right.
/// The view only wires events (hex edits → decoded refresh and the document dirty flag).
/// <para>QCN 编辑器视图：左侧 NV 条目列表，右侧结构化值与原始数据编辑。
/// 视图层仅做事件接线（十六进制编辑 → 解码刷新与文档脏标记）。</para>
/// </summary>
public partial class QcnEditorView : UserControl
{
    /// <summary>
    /// Creates the QCN editor view and initializes its components.
    /// <para>创建 QCN 编辑器视图并初始化组件。</para>
    /// </summary>
    public QcnEditorView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// HexViewer byte-edit event: refreshes the decoded display and marks the document dirty.
    /// <para>HexViewer 字节编辑事件：刷新解码显示并把文档标记为已修改。</para>
    /// </summary>
    private void OnItemBytesEdited(object? sender, EventArgs e)
    {
        if (DataContext is QcnEditorViewModel vm)
        {
            vm.MarkHexEdited();
        }
    }
}

/// <summary>
/// Structured-kind visibility converter: returns <c>true</c> for any kind other than
/// <see cref="QcnStructuredKind.None"/>.
/// <para>结构化值类型可见性转换器：<see cref="QcnStructuredKind.None"/> 之外的类型返回 <c>true</c>。</para>
/// </summary>
public sealed class QcnStructuredKindVisibilityConverter : IValueConverter
{
    /// <summary>
    /// The converter singleton.
    /// <para>转换器单例。</para>
    /// </summary>
    public static readonly QcnStructuredKindVisibilityConverter Instance = new();

    /// <summary>
    /// Converts the structured kind into its pane visibility.
    /// <para>将结构化值类型转换为面板可见性。</para>
    /// </summary>
    /// <param name="value">The structured kind value. <para>结构化值类型。</para></param>
    /// <param name="targetType">The target type. <para>目标类型。</para></param>
    /// <param name="parameter">Unused. <para>未使用。</para></param>
    /// <param name="culture">The culture. <para>区域信息。</para></param>
    /// <returns>
    /// <c>true</c> when the kind is not <see cref="QcnStructuredKind.None"/>.
    /// <para>类型不为 <see cref="QcnStructuredKind.None"/> 时为 <c>true</c>。</para>
    /// </returns>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is QcnStructuredKind kind && kind != QcnStructuredKind.None;

    /// <summary>
    /// Not supported.
    /// <para>不支持。</para>
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// Always thrown.
    /// <para>总是抛出。</para>
    /// </exception>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}