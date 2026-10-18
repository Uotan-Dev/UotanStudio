using Avalonia;
using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.Services.Projects;
using UotanStudio.ViewModels.Workspace;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// Project image node view model: selects an image from the project image panel and outputs its absolute path.
/// <para>工程镜像节点视图模型：从工程镜像面板选择一张镜像，并输出其绝对路径。</para>
/// </summary>
public class ProjectImageNodeViewModel : FlowNodeViewModel
{
    private IReadOnlyList<string>? _cachedAvailableImages;

    /// <summary>
    /// Creates a project image node with the specified title.
    /// <para>创建具有指定标题的工程镜像节点。</para>
    /// </summary>
    /// <param name="title">
    /// The display title of the node.
    /// <para>节点的显示标题。</para>
    /// </param>
    public ProjectImageNodeViewModel(string title) : base(title, NodeCategory.Data)
    {
        if (ImageViewModel.Instance?.Images is { } images)
        {
            images.CollectionChanged += (_, _) => _cachedAvailableImages = null;
        }
    }

    /// <summary>
    /// Creates a default project image node that outputs the absolute path of the selected image.
    /// <para>创建默认的工程镜像节点，输出选中镜像的绝对路径。</para>
    /// </summary>
    public static ProjectImageNodeViewModel CreateProjectImageNode()
    {
        var node = new ProjectImageNodeViewModel("Project Image")
        {
            Location = new Point(100, 100),
            TypeKey = "Resource.Project.Image"
        };
        node.Output.Add(new NodeConnectorViewModel { Title = "Path", PortType = NodePortType.Path });
        return node;
    }

    /// <summary>
    /// Gets whether the node is a project-image reference node; the properties panel shows the image picker for it.
    /// <para>获取该节点为工程镜像引用节点，属性面板据此显示镜像选择编辑区。</para>
    /// </summary>
    public override bool IsProjectImageNode => true;

    /// <summary>
    /// Gets or sets the relative path of the selected image.
    /// <para>获取或设置选中的镜像相对路径。</para>
    /// </summary>
    public override string SelectedImage
    {
        get;
        set
        {
            if (!IsEditingLocked)
            {
                SetProperty(ref field, value);
            }
        }
    } = string.Empty;

    /// <summary>
    /// Gets the list of relative image paths available in the project image panel (each taken from
    /// <see cref="ImageEntryViewModel.RelativePath"/>).
    /// <para>获取工程镜像面板中的可用镜像相对路径列表（每项取自 <see cref="ImageEntryViewModel.RelativePath"/>）。</para>
    /// </summary>
    public override IReadOnlyList<string> AvailableImages
        => _cachedAvailableImages ??= ComputeAvailableImages();

    /// <summary>Dispatches execution logic by the node type key (TypeKey): Resource.Project.Image outputs the absolute path of the selected image.
    /// <para>按节点类型键（TypeKey）分发执行逻辑：Resource.Project.Image 输出选中镜像的绝对路径。</para></summary>
    /// <param name="context">The execution context.<para>执行上下文。</para></param>
    /// <returns>A task representing the asynchronous execution.<para>表示异步执行的任务。</para></returns>
    public override Task ExecuteAsync(INodeExecutionContext context)
    {
        switch (TypeKey)
        {
            case "Resource.Project.Image":
                ExecuteProjectImage(context);
                break;
            default:
                return base.ExecuteAsync(context);
        }

        context.CancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private IReadOnlyList<string> ComputeAvailableImages()
        => ImageViewModel.Instance?.Images.Select(image => image.RelativePath).ToList() ?? [];

    private void ExecuteProjectImage(INodeExecutionContext context)
    {
        if (string.IsNullOrWhiteSpace(SelectedImage))
        {
            throw new InvalidOperationException("No project image was selected.");
        }

        var root = ProjectContextService.Instance.RootDirectory;
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("No project is open.");
        }

        var absolutePath = Path.Combine(root, SelectedImage);
        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException("The selected project image was not found.", absolutePath);
        }
        WriteOutput("Path", absolutePath);
        context.Log.Write(LogLevel.Info, Title, $"Image path: {absolutePath}  已输出镜像路径。");
    }
}