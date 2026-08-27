using UotanStudio.Services.Projects;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.ViewModels;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// <para>工程镜像节点视图模型：从工程镜像面板选择一张镜像，并输出其绝对路径。</para>
/// Project image node view model: selects an image from the project image panel and outputs its absolute path.
/// </summary>
public class ProjectImageNodeViewModel : FlowNodeViewModel
{
    private string _selectedImage = string.Empty;

    /// <summary>
    /// <para>创建具有指定标题的工程镜像节点。</para>
    /// Creates a project image node with the specified title.
    /// </summary>
    /// <param name="title">
    /// <para>节点的显示标题。</para>
    /// The display title of the node.
    /// </param>
    public ProjectImageNodeViewModel(string title) : base(title, NodeCategory.Data)
    {
    }

    /// <summary>
    /// <para>创建默认的工程镜像节点，输出选中镜像的绝对路径。</para>
    /// Creates a default project image node that outputs the absolute path of the selected image.
    /// </summary>
    public static ProjectImageNodeViewModel CreateProjectImageNode()
    {
        var node = new ProjectImageNodeViewModel("Project Image")
        {
            Location = new Point(100, 100),
            TypeKey = "Data.ProjectImage"
        };
        node.Output.Add(new NodeConnectorViewModel { Title = "Path", PortType = NodePortType.Path });
        return node;
    }

    /// <summary>
    /// <para>获取该节点为工程镜像引用节点，属性面板据此显示镜像选择编辑区。</para>
    /// Gets whether the node is a project-image reference node; the properties panel shows the image picker for it.
    /// </summary>
    public override bool IsProjectImageNode => true;

    /// <summary>
    /// <para>获取或设置选中的镜像相对路径。</para>
    /// Gets or sets the relative path of the selected image.
    /// </summary>
    public override string SelectedImage
    {
        get => _selectedImage;
        set => SetProperty(ref _selectedImage, value);
    }

    /// <summary>
    /// <para>获取工程镜像面板中的可用镜像相对路径列表（每项取自 <see cref="ImageEntryViewModel.RelativePath"/>）。</para>
    /// Gets the list of relative image paths available in the project image panel (each taken from
    /// <see cref="ImageEntryViewModel.RelativePath"/>).
    /// </summary>
    public override IReadOnlyList<string> AvailableImages
        => ImageViewModel.Instance?.Images.Select(image => image.RelativePath).ToList() ?? [];

    /// <summary>
    /// <para>按节点类型键（TypeKey）分发执行逻辑：Data.ProjectImage 输出选中镜像的绝对路径。</para>
    /// Dispatches execution logic by the node type key (TypeKey): Data.ProjectImage outputs the absolute
    /// path of the selected image.
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
        switch (TypeKey)
        {
            case "Data.ProjectImage":
                ExecuteProjectImage(context);
                break;
            default:
                return base.ExecuteAsync(context);
        }

        context.CancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private void ExecuteProjectImage(INodeExecutionContext context)
    {
        if (string.IsNullOrWhiteSpace(SelectedImage))
        {
            context.Log.Write(LogLevel.Warn, Title, "No image selected.  未选择镜像。");
            return;
        }

        var root = ProjectContextService.Instance.RootDirectory;
        if (string.IsNullOrWhiteSpace(root))
        {
            context.Log.Write(LogLevel.Warn, Title, "No project is open.  尚未打开工程。");
            return;
        }

        var absolutePath = Path.Combine(root, SelectedImage);
        WriteOutput("Path", absolutePath);
        context.Log.Write(LogLevel.Info, Title, $"Image path: {absolutePath}  已输出镜像路径。");
    }
}