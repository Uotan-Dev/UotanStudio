using Avalonia.Controls;

namespace UotanStudio.Areas.Workspace;

/// <summary>
/// Workspace page view.
/// <para>工作区页面视图。</para>
/// </summary>
public partial class WorkspaceView : UserControl
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WorkspaceView"/> class.
    /// <para>初始化 <see cref="WorkspaceView"/> 类的新实例。</para>
    /// </summary>
    public WorkspaceView()
    {
        // Dock 主题的模板/样式绑定（Layout.Id、Layout.Title 等）在控件挂载时即求值，
        // 若布局为空会输出 "Value is null" 绑定错误。因此布局必须在 DockControl 模板
        // 实例化之前就绪：先设置 DataContext，再在 InitializeComponent 后显式赋值
        // DockControl.Layout，确保挂载前 Layout 已非空。
        // The dock theme's template/style bindings (Layout.Id, Layout.Title, etc.) evaluate
        // as soon as the control attaches; a null layout logs "Value is null" binding errors.
        // So the layout must be ready before the DockControl template instantiates: set the
        // DataContext first, then assign DockControl.Layout after InitializeComponent, so the
        // layout is non-null before the view attaches to the visual tree.
        // 每个工作区视图都实例化自己的布局并登记为 Instance，确保 DockControl 绑定的是
        // 全新加载的布局并正常渲染；窗口管理器读取实例来操作同一个停靠布局。
        // Each workspace view builds its own layout and registers itself as Instance so the
        // DockControl binds a freshly-loaded layout; the window manager reads it to operate
        // the same dock layout.
        var vm = new WorkspaceViewModel();
        DataContext = vm;
        InitializeComponent();
        DockControl.Layout = vm.Layout;
    }
}