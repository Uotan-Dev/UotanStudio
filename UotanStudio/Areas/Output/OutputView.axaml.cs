using Avalonia.Controls;

namespace UotanStudio.Areas.Output;

/// <summary>
/// Output view: shows channeled logs (All/Build/Flash/Device).
/// <para>输出视图：展示分通道日志（全部/构建/烧录/设备）。</para>
/// </summary>
public partial class OutputView : UserControl
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OutputView"/> class.
    /// <para>初始化 <see cref="OutputView"/> 类的新实例。</para>
    /// </summary>
    public OutputView()
    {
        InitializeComponent();
    }
}