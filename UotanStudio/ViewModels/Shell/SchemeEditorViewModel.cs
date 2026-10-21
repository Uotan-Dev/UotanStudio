using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.Services;
using UotanStudio.Services.Projects;
using UotanStudio.Services.Projects.Schemes;
using UotanStudio.ViewModels.Nodes;
using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.ViewModels.Shell;

/// <summary>
/// Scheme editor view model: metadata, declared inputs, assets and ordered stages, with stage capture from
/// an open node project, the current canvas or the current selection, and persistence through <see cref="SchemeFile"/>.
/// <para>Scheme 编辑器视图模型：元数据、声明输入、资产与有序阶段，支持从已打开的节点项目、当前画布或当前选区捕获阶段，
/// 并通过 <see cref="SchemeFile"/> 持久化。</para>
/// </summary>
public sealed partial class SchemeEditorViewModel : ObservableObject
{
    private readonly Func<IReadOnlyList<string>> _workflowNames;
    private readonly Func<string, CancellationToken, Task<WorkflowSnapshot?>> _workflowLoader;
    private readonly Func<bool, WorkflowSnapshot?> _canvasSnapshot;

    /// <summary>
    /// Creates the editor view model using the active project and node editor as workflow sources.
    /// <para>创建以当前工程与节点编辑器为工作流来源的编辑器视图模型。</para>
    /// </summary>
    public SchemeEditorViewModel()
        : this(null, null, null)
    {
    }

    /// <summary>
    /// Creates the editor view model with optional workflow and canvas source overrides.
    /// <para>创建可替换工作流与画布来源的编辑器视图模型。</para>
    /// </summary>
    /// <param name="workflowNames">Workflow-name provider, or <see langword="null"/> for the active project.
    /// <para>工作流名称提供者；为 <see langword="null"/> 时读取当前工程。</para></param>
    /// <param name="workflowLoader">Workflow snapshot loader, or <see langword="null"/> for the active project.
    /// <para>工作流快照加载器；为 <see langword="null"/> 时读取当前工程。</para></param>
    /// <param name="canvasSnapshot">Canvas snapshot provider, or <see langword="null"/> for the node editor.
    /// <para>画布快照提供者；为 <see langword="null"/> 时读取节点编辑器。</para></param>
    internal SchemeEditorViewModel(
        Func<IReadOnlyList<string>>? workflowNames,
        Func<string, CancellationToken, Task<WorkflowSnapshot?>>? workflowLoader,
        Func<bool, WorkflowSnapshot?>? canvasSnapshot)
    {
        _workflowNames = workflowNames ?? LoadWorkflowNames;
        _workflowLoader = workflowLoader ?? LoadProjectWorkflowAsync;
        _canvasSnapshot = canvasSnapshot ?? LoadCanvasSnapshot;
        NewScheme();
    }

    /// <summary>
    /// Scheme file last loaded or saved by the editor.
    /// <para>编辑器最近加载或保存的 Scheme 文件。</para>
    /// </summary>
    [ObservableProperty]
    private string _filePath = string.Empty;

    /// <summary>
    /// Stable scheme identifier.
    /// <para>稳定的 Scheme 标识符。</para>
    /// </summary>
    [ObservableProperty]
    private string _id = string.Empty;

    /// <summary>
    /// Display name of the scheme.
    /// <para>Scheme 的显示名称。</para>
    /// </summary>
    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>
    /// Author-declared scheme version.
    /// <para>作者声明的 Scheme 版本。</para>
    /// </summary>
    [ObservableProperty]
    private string _version = "1.0.0";

    /// <summary>
    /// Optional scheme description.
    /// <para>可选的 Scheme 说明。</para>
    /// </summary>
    [ObservableProperty]
    private string _description = string.Empty;

    /// <summary>
    /// Optional author name.
    /// <para>可选的作者名称。</para>
    /// </summary>
    [ObservableProperty]
    private string _author = string.Empty;

    /// <summary>
    /// Last load, save or capture error; empty when the last operation succeeded.
    /// <para>最近一次加载、保存或捕获错误；上次操作成功时为空。</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _lastError = string.Empty;

    /// <summary>
    /// Structural validation messages produced by the last save attempt.
    /// <para>最近一次保存尝试产生的结构校验消息。</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValidationMessages))]
    private string _validationText = string.Empty;

    /// <summary>
    /// Editable input definitions in declaration order.
    /// <para>按声明顺序排列的可编辑输入定义。</para>
    /// </summary>
    public ObservableCollection<SchemeInputEditorViewModel> Inputs { get; } = [];

    /// <summary>
    /// Editable assets in declaration order.
    /// <para>按声明顺序排列的可编辑资产。</para>
    /// </summary>
    public ObservableCollection<SchemeAssetEditorViewModel> Assets { get; } = [];

    /// <summary>
    /// Editable stages in execution order.
    /// <para>按执行顺序排列的可编辑阶段。</para>
    /// </summary>
    public ObservableCollection<SchemeStageEditorViewModel> Stages { get; } = [];

    /// <summary>
    /// Project workflow names available for embedding or reference.
    /// <para>可嵌入或引用的项目工作流名称。</para>
    /// </summary>
    public ObservableCollection<string> AvailableWorkflows { get; } = [];

    /// <summary>
    /// Stage selected for detail editing.
    /// <para>选中用于详情编辑的阶段。</para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedStage))]
    private SchemeStageEditorViewModel? _selectedStage;

    /// <summary>
    /// Whether a stage is selected for detail editing.
    /// <para>是否已选中用于详情编辑的阶段。</para>
    /// </summary>
    public bool HasSelectedStage => SelectedStage is not null;

    /// <summary>
    /// Whether the editor currently declares any stage.
    /// <para>编辑器当前是否声明了任何阶段。</para>
    /// </summary>
    public bool HasStages => Stages.Count > 0;

    /// <summary>
    /// Whether the last operation reported an error.
    /// <para>最近一次操作是否报告错误。</para>
    /// </summary>
    public bool HasError => !string.IsNullOrEmpty(LastError);

    /// <summary>
    /// Whether the last save produced structural validation messages.
    /// <para>最近一次保存是否产生结构校验消息。</para>
    /// </summary>
    public bool HasValidationMessages => !string.IsNullOrEmpty(ValidationText);

    /// <summary>
    /// Resets the editor to a new, unsaved scheme with a generated identifier.
    /// <para>把编辑器重置为新的未保存 Scheme，并生成标识符。</para>
    /// </summary>
    public void NewScheme()
    {
        ApplyPackage(new SchemePackage
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = string.Empty,
            Version = "1.0.0"
        }, null);
    }

    /// <summary>
    /// Loads a scheme file and rebuilds every editor collection.
    /// <para>加载 Scheme 文件并重建全部编辑集合。</para>
    /// </summary>
    /// <param name="path">The scheme file to read. <para>要读取的 Scheme 文件。</para></param>
    /// <param name="cancellationToken">Cancels the bounded read. <para>取消有界读取。</para></param>
    /// <returns><see langword="true"/> when a package was loaded. <para>成功加载软件包时返回 <see langword="true"/>。</para></returns>
    public async Task<bool> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var package = await SchemeFile.LoadAsync(path, cancellationToken).ConfigureAwait(true);
        if (package is null)
        {
            LastError = LanguageService.Instance.GetString("Menu_OpenFailed");
            return false;
        }

        ApplyPackage(package, path);
        return true;
    }

    /// <summary>
    /// Saves the edited package to the supplied path, reporting structural validation messages without blocking the write.
    /// <para>把编辑后的软件包保存到给定路径，报告结构校验消息但不阻止写入。</para>
    /// </summary>
    /// <param name="path">Destination scheme file. <para>目标 Scheme 文件。</para></param>
    /// <param name="cancellationToken">Cancels the atomic write. <para>取消原子写入。</para></param>
    /// <returns><see langword="true"/> when the package was persisted. <para>软件包已持久化时返回 <see langword="true"/>。</para></returns>
    public async Task<bool> SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            LastError = LanguageService.Instance.GetString("Dialog_SaveFailed");
            return false;
        }

        var package = BuildPackage();
        var errors = package.Validate();
        ValidationText = errors.Count == 0 ? string.Empty : string.Join(Environment.NewLine, errors);
        var saved = await SchemeFile.SaveAsync(path, package, cancellationToken).ConfigureAwait(true);
        if (!saved)
        {
            LastError = LanguageService.Instance.GetString("Dialog_SaveFailed");
            return false;
        }

        FilePath = path;
        LastError = string.Empty;
        return true;
    }

    /// <summary>
    /// Replaces every editor collection with the supplied package.
    /// <para>用给定软件包替换全部编辑集合。</para>
    /// </summary>
    /// <param name="package">The package to edit; must not be <see langword="null"/>.
    /// <para>要编辑的软件包；不能为 <see langword="null"/>。</para></param>
    /// <param name="filePath">The originating file path, or <see langword="null"/> for a new scheme.
    /// <para>来源文件路径；新建 Scheme 时为 <see langword="null"/>。</para></param>
    public void ApplyPackage(SchemePackage package, string? filePath)
    {
        ArgumentNullException.ThrowIfNull(package);
        package.Normalize();
        FilePath = filePath ?? string.Empty;
        Id = package.Id ?? string.Empty;
        Name = package.Name ?? string.Empty;
        Version = string.IsNullOrWhiteSpace(package.Version) ? "1.0.0" : package.Version;
        Description = package.Description ?? string.Empty;
        Author = package.Author ?? string.Empty;

        Inputs.Clear();
        foreach (var input in package.Inputs)
        {
            Inputs.Add(new SchemeInputEditorViewModel(input));
        }

        Assets.Clear();
        foreach (var asset in package.Assets)
        {
            Assets.Add(new SchemeAssetEditorViewModel(asset));
        }

        Stages.Clear();
        foreach (var stage in package.Stages)
        {
            Stages.Add(new SchemeStageEditorViewModel(stage));
        }
        OnPropertyChanged(nameof(HasStages));

        SelectedStage = Stages.FirstOrDefault();
        LastError = string.Empty;
        ValidationText = string.Empty;
        RefreshAvailableWorkflows();
    }

    /// <summary>
    /// Builds the persistence package from the current editor state.
    /// <para>由当前编辑器状态构建持久化软件包。</para>
    /// </summary>
    /// <returns>The edited package. <para>编辑后的软件包。</para></returns>
    public SchemePackage BuildPackage()
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            Id = Guid.NewGuid().ToString("N");
        }

        return new SchemePackage
        {
            FormatVersion = SchemePackage.CurrentFormatVersion,
            Id = Id.Trim(),
            Name = Name.Trim(),
            Version = string.IsNullOrWhiteSpace(Version) ? "1.0.0" : Version.Trim(),
            Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
            Author = string.IsNullOrWhiteSpace(Author) ? null : Author.Trim(),
            Inputs = [.. Inputs.Select(input => input.ToDefinition())],
            Assets = [.. Assets.Select(asset => asset.ToAsset())],
            Stages = [.. Stages.Select(stage => stage.ToStage())]
        };
    }

    /// <summary>
    /// Adds a blank input definition and selects it.
    /// <para>添加空白输入定义并选中。</para>
    /// </summary>
    /// <returns>The added row. <para>新增的行。</para></returns>
    public SchemeInputEditorViewModel AddInput()
    {
        var input = new SchemeInputEditorViewModel(new SchemeInputDefinition
        {
            Id = NextIdentifier("input", Inputs.Select(item => item.Id)),
            Type = SchemeInputType.Text
        });
        Inputs.Add(input);
        return input;
    }

    /// <summary>
    /// Removes an input definition.
    /// <para>移除输入定义。</para>
    /// </summary>
    /// <param name="input">The row to remove; ignored when <see langword="null"/>.
    /// <para>要移除的行；为 <see langword="null"/> 时忽略。</para></param>
    public void RemoveInput(SchemeInputEditorViewModel? input)
    {
        if (input is not null)
        {
            Inputs.Remove(input);
        }
    }

    /// <summary>
    /// Adds a blank asset and selects it.
    /// <para>添加空白资产并选中。</para>
    /// </summary>
    /// <returns>The added row. <para>新增的行。</para></returns>
    public SchemeAssetEditorViewModel AddAsset()
    {
        var asset = new SchemeAssetEditorViewModel(new SchemeAsset
        {
            Id = NextIdentifier("asset", Assets.Select(item => item.Id)),
            RelativePath = string.Empty
        });
        Assets.Add(asset);
        return asset;
    }

    /// <summary>
    /// Removes an asset declaration.
    /// <para>移除资产声明。</para>
    /// </summary>
    /// <param name="asset">The row to remove; ignored when <see langword="null"/>.
    /// <para>要移除的行；为 <see langword="null"/> 时忽略。</para></param>
    public void RemoveAsset(SchemeAssetEditorViewModel? asset)
    {
        if (asset is not null)
        {
            Assets.Remove(asset);
        }
    }

    /// <summary>
    /// Adds an empty stage and selects it.
    /// <para>添加空白阶段并选中。</para>
    /// </summary>
    /// <returns>The added row. <para>新增的行。</para></returns>
    public SchemeStageEditorViewModel AddStage()
    {
        var stage = new SchemeStageEditorViewModel(new SchemeStage
        {
            Id = NextIdentifier("stage", Stages.Select(item => item.Id)),
            Kind = SchemeStageKind.Generic,
            FailurePolicy = SchemeStageFailurePolicy.Stop
        });
        Stages.Add(stage);
        OnPropertyChanged(nameof(HasStages));
        SelectedStage = stage;
        return stage;
    }

    /// <summary>
    /// Removes a stage.
    /// <para>移除阶段。</para>
    /// </summary>
    /// <param name="stage">The row to remove; ignored when <see langword="null"/>.
    /// <para>要移除的行；为 <see langword="null"/> 时忽略。</para></param>
    public void RemoveStage(SchemeStageEditorViewModel? stage)
    {
        if (stage is null || !Stages.Remove(stage))
        {
            return;
        }

        OnPropertyChanged(nameof(HasStages));
        if (ReferenceEquals(SelectedStage, stage))
        {
            SelectedStage = Stages.FirstOrDefault();
        }
    }

    /// <summary>
    /// Moves a stage one position earlier in execution order.
    /// <para>把阶段在执行顺序中前移一位。</para>
    /// </summary>
    /// <param name="stage">The stage to move. <para>要移动的阶段。</para></param>
    /// <returns><see langword="true"/> when the order changed. <para>顺序已改变时返回 <see langword="true"/>。</para></returns>
    public bool MoveStageUp(SchemeStageEditorViewModel? stage) => MoveStage(stage, -1);

    /// <summary>
    /// Moves a stage one position later in execution order.
    /// <para>把阶段在执行顺序中后移一位。</para>
    /// </summary>
    /// <param name="stage">The stage to move. <para>要移动的阶段。</para></param>
    /// <returns><see langword="true"/> when the order changed. <para>顺序已改变时返回 <see langword="true"/>。</para></returns>
    public bool MoveStageDown(SchemeStageEditorViewModel? stage) => MoveStage(stage, 1);

    /// <summary>
    /// Adds a stage embedding the named project workflow snapshot.
    /// <para>添加一个内嵌指定项目工作流快照的阶段。</para>
    /// </summary>
    /// <param name="workflowName">The workflow to load; empty is rejected. <para>要加载的工作流；空值会被拒绝。</para></param>
    /// <param name="cancellationToken">Cancels the workflow load. <para>取消工作流加载。</para></param>
    /// <returns><see langword="true"/> when a stage was added. <para>添加了阶段时返回 <see langword="true"/>。</para></returns>
    public async Task<bool> AddEmbeddedStageAsync(string workflowName, CancellationToken cancellationToken = default)
    {
        var snapshot = await LoadWorkflowAsync(workflowName, cancellationToken).ConfigureAwait(true);
        if (snapshot is null)
        {
            return false;
        }

        var stage = AddStage();
        stage.Embed(snapshot, workflowName);
        return true;
    }

    /// <summary>
    /// Adds a stage that references a project workflow by name instead of embedding it.
    /// <para>添加按名称引用项目工作流而非内嵌其内容的阶段。</para>
    /// </summary>
    /// <param name="workflowName">The workflow name to reference. <para>要引用的工作流名称。</para></param>
    /// <returns>The added row. <para>新增的行。</para></returns>
    public SchemeStageEditorViewModel AddReferenceStage(string workflowName)
    {
        var stage = AddStage();
        stage.Reference(workflowName);
        return stage;
    }

    /// <summary>
    /// Embeds the named workflow into the selected stage, replacing its current source.
    /// <para>把指定工作流内嵌到选中的阶段，替换其当前来源。</para>
    /// </summary>
    /// <param name="workflowName">The workflow to load. <para>要加载的工作流。</para></param>
    /// <param name="cancellationToken">Cancels the workflow load. <para>取消工作流加载。</para></param>
    /// <returns><see langword="true"/> when the selected stage now embeds the workflow.
    /// <para>选中阶段已内嵌该工作流时返回 <see langword="true"/>。</para></returns>
    public async Task<bool> EmbedSelectedStageAsync(string workflowName, CancellationToken cancellationToken = default)
    {
        if (SelectedStage is not { } stage)
        {
            LastError = LanguageService.Instance.GetString("Node_Graph_Empty");
            return false;
        }

        var snapshot = await LoadWorkflowAsync(workflowName, cancellationToken).ConfigureAwait(true);
        if (snapshot is null)
        {
            return false;
        }

        stage.Embed(snapshot, workflowName);
        return true;
    }

    /// <summary>
    /// Replaces the stage list with one stage captured from the current canvas or the current selection.
    /// <para>用从当前画布或当前选区捕获的单个阶段替换阶段列表。</para>
    /// </summary>
    /// <param name="selectionOnly">Whether to capture only selected nodes when a selection exists.
    /// <para>存在选区时是否仅捕获选中的节点。</para></param>
    /// <returns><see langword="true"/> when a canvas with at least one node was captured.
    /// <para>捕获到至少包含一个节点的画布时返回 <see langword="true"/>。</para></returns>
    public bool CreateSingleStageFromCurrentCanvas(bool selectionOnly)
    {
        var snapshot = _canvasSnapshot(selectionOnly);
        if (snapshot is null || snapshot.Nodes.Count == 0)
        {
            LastError = LanguageService.Instance.GetString("Node_Graph_Empty");
            return false;
        }

        Stages.Clear();
        var stage = new SchemeStageEditorViewModel(new SchemeStage
        {
            Id = NextIdentifier("stage", []),
            Kind = SchemeStageKind.Generic,
            FailurePolicy = SchemeStageFailurePolicy.Stop
        });
        stage.Embed(snapshot, string.Empty);
        Stages.Add(stage);
        OnPropertyChanged(nameof(HasStages));
        SelectedStage = stage;
        return true;
    }

    /// <summary>
    /// Rebuilds the available workflow list from the configured provider.
    /// <para>从配置的提供者重建可用工作流列表。</para>
    /// </summary>
    public void RefreshAvailableWorkflows()
    {
        AvailableWorkflows.Clear();
        foreach (var name in _workflowNames())
        {
            if (!string.IsNullOrWhiteSpace(name)
                && !AvailableWorkflows.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                AvailableWorkflows.Add(name);
            }
        }
    }

    private async Task<WorkflowSnapshot?> LoadWorkflowAsync(string workflowName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workflowName))
        {
            LastError = LanguageService.Instance.GetString("Node_Graph_Empty");
            return null;
        }

        try
        {
            var snapshot = await _workflowLoader(workflowName, cancellationToken).ConfigureAwait(true);
            if (snapshot is null || snapshot.Nodes.Count == 0)
            {
                LastError = LanguageService.Instance.GetString("Node_Graph_Empty");
                return null;
            }

            LastError = string.Empty;
            return snapshot;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LastError = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                LanguageService.Instance.GetString("Node_OperationFailed"), ex.Message);
            return null;
        }
    }

    private bool MoveStage(SchemeStageEditorViewModel? stage, int offset)
    {
        if (stage is null)
        {
            return false;
        }

        var index = Stages.IndexOf(stage);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= Stages.Count)
        {
            return false;
        }

        Stages.Move(index, target);
        SelectedStage = stage;
        return true;
    }

    private static string NextIdentifier(string prefix, IEnumerable<string> existing)
    {
        HashSet<string> used = new(existing, StringComparer.OrdinalIgnoreCase);
        var index = 1;
        while (used.Contains($"{prefix}{index}"))
        {
            index++;
        }
        return $"{prefix}{index}";
    }

    private static IReadOnlyList<string> LoadWorkflowNames()
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (ProjectOperationsService.Instance.NodeEditorVM is { } editor)
            {
                foreach (var document in editor.OpenDocuments)
                {
                    names.Add(document.Name);
                }
            }
        }
        catch (InvalidOperationException)
        {
        }

        foreach (var project in ProjectContextService.Instance.NodeProjects)
        {
            names.Add(project.Name);
        }
        return [.. names.Where(name => !string.IsNullOrWhiteSpace(name)).OrderBy(name => name, StringComparer.OrdinalIgnoreCase)];
    }

    private static async Task<WorkflowSnapshot?> LoadProjectWorkflowAsync(string name,
        CancellationToken cancellationToken)
    {
        try
        {
            if (ProjectOperationsService.Instance.NodeEditorVM?.FindDocument(name) is { } document)
            {
                return new WorkflowSnapshot
                {
                    Nodes = WorkflowPersistenceService.BuildNodeDtos(document.Nodes),
                    Connections = WorkflowPersistenceService.BuildConnectionDtos(document.Nodes, document.Connections)
                };
            }
        }
        catch (InvalidOperationException)
        {
        }

        var context = ProjectContextService.Instance;
        var registration = context.NodeProjects.FirstOrDefault(node =>
            string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase));
        if (registration is not null && context.ResolveNodeProjectPath(registration) is { } path && File.Exists(path))
        {
            return await WorkflowPersistenceService.LoadAsync(path, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    private static WorkflowSnapshot? LoadCanvasSnapshot(bool selectionOnly)
    {
        NodeEditorViewModel? editor;
        try
        {
            editor = ProjectOperationsService.Instance.NodeEditorVM;
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        if (editor is null)
        {
            return null;
        }

        IReadOnlyList<FlowNodeViewModel> nodes = selectionOnly && editor.SelectedNodes.Count > 0
            ? [.. editor.SelectedNodes]
            : [.. editor.Nodes];
        if (nodes.Count == 0)
        {
            return null;
        }

        HashSet<string> identifiers = new(nodes.Select(node => node.InstanceId), StringComparer.Ordinal);
        var connections = WorkflowPersistenceService.BuildConnectionDtos(nodes, editor.Connections)
            .Where(edge => edge.SourceInstanceId is { } source && identifiers.Contains(source)
                && edge.TargetInstanceId is { } target && identifiers.Contains(target))
            .ToList();
        return new WorkflowSnapshot
        {
            Nodes = WorkflowPersistenceService.BuildNodeDtos(nodes),
            Connections = connections
        };
    }
}