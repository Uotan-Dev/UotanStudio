namespace UotanStudio.Services.Projects;

public sealed partial class ProjectSession
{
    /// <summary>
    /// Registers and writes an empty node project (<c>nodes/&lt;name&gt;.uotannode</c>) in the open solution and
    /// opens it as the active canvas. A duplicate name throws; an existing active canvas hands the active role
    /// to the new project.
    /// <para>在当前解决方案中登记并写入一个空的节点项目（<c>nodes/&lt;name&gt;.uotannode</c>），把它作为活动
    /// 画布打开。名称重复时抛出异常；解决方案中已有其他活动画布时会被移交给新项目。</para>
    /// </summary>
    /// <param name="name">Node project name, also the file base name; must pass
    /// <see cref="ProjectValidation.ValidateName"/>. <para>节点项目名，同时作为文件基本名，需通过
    /// <see cref="ProjectValidation.ValidateName"/>。</para></param>
    /// <param name="cancellationToken">Cancels the gate wait and the staged writes.
    /// <para>取消门等待与暂存写入。</para></param>
    /// <returns><see langword="true"/> when the project was written and opened; <see langword="false"/> when
    /// the operation was gated. <para>项目成功写入并打开时返回 <see langword="true"/>；操作被门拦截时返回
    /// <see langword="false"/>。</para></returns>
    /// <exception cref="InvalidOperationException">No solution is open, or the name is already registered.
    /// <para>尚未打开解决方案，或该名称已登记。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> fails
    /// <see cref="ProjectValidation.ValidateName"/>. <para><paramref name="name"/> 未通过
    /// <see cref="ProjectValidation.ValidateName"/>。</para></exception>
    /// <exception cref="IOException">The staged node project or solution file could not be committed.
    /// <para>暂存的节点项目或解决方案文件无法提交。</para></exception>
    public async Task<bool> CreateNodeProjectAsync(string name, CancellationToken cancellationToken = default)
    {
        ProjectValidation.ValidateName(name);
        var created = false;
        await RunOperationAsync(async () =>
        {
            var path = CurrentProjectPath ?? throw new InvalidOperationException("No solution is open.");
            if (Context.NodeProjects.Any(node => string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("The node project name already exists.");
            var relativePath = $"nodes/{name}.uotannode";
            var filePath = Path.Combine(Context.NodeProjectsDirectory!, name + ".uotannode");
            var snapshot = new NodeProjectSnapshot
            {
                FormatVersion = NodeProjectSnapshot.CurrentFormatVersion,
                ProjectName = name,
                Nodes = [],
                Connections = []
            };

            var references = NodeProjectReferences();
            references.Add(new NodeProjectDto
            {
                Name = name,
                RelativePath = relativePath,
                IsOpen = true,
                IsActive = true
            });
            foreach (var dto in references)
                dto.IsActive = string.Equals(dto.Name, name, StringComparison.OrdinalIgnoreCase);

            var solution = await ReadSolutionForEditAsync(path, cancellationToken);
            solution.NodeProjects = references;
            ProjectValidation.ValidateSolution(solution);

            using var transaction = new ProjectFileTransaction();
            await transaction.StageAsync(filePath, snapshot, SolutionJsonContext.Default.NodeProjectSnapshot, cancellationToken);
            await transaction.StageAsync(path, solution, SolutionJsonContext.Default.SolutionSnapshot, cancellationToken);
            transaction.Commit(cancellationToken);

            Context.AddNodeProject(new NodeProjectContext(name, relativePath, IsOpen: true, IsActive: true));
            await _workspace.RestoreWorkflowAsync(name, snapshot);
            _workspace.ActivateWorkflow(name);
            created = true;
            Changed?.Invoke();
            ProjectDiagnostics.Write(ProjectLogLevel.Info, "Menu",
                $"Created node project: {name} | file={filePath} | relative={relativePath} | directory={Context.NodeProjectsDirectory}");
        }, changingProject: true, cancellationToken: cancellationToken);
        return created;
    }

    /// <summary>
    /// Loads a registered node project into the editor as an open document and makes it the active canvas.
    /// <para>把已登记的节点项目载入编辑器成为打开的文档，并设为活动画布。</para>
    /// </summary>
    /// <param name="name">Registered node project name. <para>已登记的节点项目名。</para></param>
    /// <param name="cancellationToken">Cancels the gate wait and the reads.
    /// <para>取消门等待与各项读取。</para></param>
    /// <returns><see langword="true"/> when the document was opened; <see langword="false"/> when no node
    /// project matches <paramref name="name"/> or the operation was gated.
    /// <para>文档成功打开时返回 <see langword="true"/>；没有匹配 <paramref name="name"/> 的节点项目或
    /// 操作被门拦截时返回 <see langword="false"/>。</para></returns>
    /// <exception cref="InvalidDataException">The registered node project file cannot be read.
    /// <para>已登记的节点项目文件无法读取。</para></exception>
    public async Task<bool> OpenNodeProjectAsync(string name, CancellationToken cancellationToken = default)
    {
        var opened = false;
        await RunOperationAsync(async () =>
        {
            if (Context.NodeProjects.FirstOrDefault(node =>
                    string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } descriptor)
                return;
            var path = CurrentProjectPath ?? throw new InvalidOperationException("No solution is open.");
            var snapshot = await SolutionPersistenceService.LoadNodeProjectAsync(
                Context.ResolveNodeProjectPath(descriptor)!, cancellationToken)
                ?? throw new InvalidDataException("Cannot read the node project.");
            await _workspace.RestoreWorkflowAsync(descriptor.Name, snapshot);
            _workspace.ActivateWorkflow(descriptor.Name);
            Context.UpdateNodeProject(descriptor.Name, isOpen: true, isActive: true);
            await PersistNodeProjectReferencesAsync(path, cancellationToken);
            opened = true;
            Changed?.Invoke();
        }, cancellationToken: cancellationToken);
        return opened;
    }

    /// <summary>
    /// Discards the editor document of a registered node project, keeping its file and registration so it
    /// becomes a not-open entry.
    /// <para>关闭已登记节点项目的编辑器文档，保留其磁盘文件与登记，使其成为待打开项。</para>
    /// </summary>
    /// <param name="name">Registered node project name. <para>已登记的节点项目名。</para></param>
    /// <param name="cancellationToken">Cancels the gate wait and the solution rewrite.
    /// <para>取消门等待与解决方案重写。</para></param>
    /// <returns><see langword="true"/> when the document was closed and the solution rewritten;
    /// <see langword="false"/> when no node project matches <paramref name="name"/>.
    /// <para>文档成功关闭并重写解决方案时返回 <see langword="true"/>；没有匹配项时返回
    /// <see langword="false"/>。</para></returns>
    public async Task<bool> CloseNodeProjectAsync(string name, CancellationToken cancellationToken = default)
    {
        var closed = false;
        await RunOperationAsync(async () =>
        {
            if (Context.NodeProjects.FirstOrDefault(node =>
                    string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } descriptor)
                return;
            var path = CurrentProjectPath ?? throw new InvalidOperationException("No solution is open.");
            _workspace.CloseWorkflow(descriptor.Name);
            Context.UpdateNodeProject(descriptor.Name, isOpen: false, isActive: false);
            if (_workspace.ActiveWorkflowName is { } next) Context.SetActiveNodeProject(next);
            await PersistNodeProjectReferencesAsync(path, cancellationToken);
            closed = true;
            Changed?.Invoke();
        }, cancellationToken: cancellationToken);
        return closed;
    }

    /// <summary>
    /// Unregisters a node project: closes its editor document and removes it from the solution list while
    /// leaving the <c>.uotannode</c> file on disk untouched.
    /// <para>注销一个节点项目：关闭其编辑器文档并从解决方案清单移除，磁盘上的 <c>.uotannode</c> 文件保持
    /// 不变。</para>
    /// </summary>
    /// <param name="name">Registered node project name. <para>已登记的节点项目名。</para></param>
    /// <param name="cancellationToken">Cancels the gate wait and the solution rewrite.
    /// <para>取消门等待与解决方案重写。</para></param>
    /// <returns><see langword="true"/> when the registration was removed and the solution rewritten;
    /// <see langword="false"/> when no node project matches <paramref name="name"/>.
    /// <para>登记成功移除并重写解决方案时返回 <see langword="true"/>；没有匹配项时返回
    /// <see langword="false"/>。</para></returns>
    public async Task<bool> RemoveNodeProjectAsync(string name, CancellationToken cancellationToken = default)
    {
        var removed = false;
        await RunOperationAsync(async () =>
        {
            if (Context.NodeProjects.FirstOrDefault(node =>
                    string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } descriptor)
                return;
            var path = CurrentProjectPath ?? throw new InvalidOperationException("No solution is open.");
            _workspace.CloseWorkflow(descriptor.Name);
            Context.RemoveNodeProject(descriptor.Name);
            var solution = await ReadSolutionForEditAsync(path, cancellationToken);
            solution.NodeProjects = NodeProjectReferences();
            ProjectValidation.ValidateSolution(solution);
            RequireSaved(await SolutionPersistenceService.SaveSolutionAsync(path, solution, cancellationToken), path);
            removed = true;
            Changed?.Invoke();
        }, cancellationToken: cancellationToken);
        return removed;
    }

    /// <summary>
    /// Makes an open node project the active canvas.
    /// <para>把已打开的节点项目设为活动画布。</para>
    /// </summary>
    /// <param name="name">Registered node project name. <para>已登记的节点项目名。</para></param>
    /// <param name="cancellationToken">Cancels the gate wait and the solution rewrite.
    /// <para>取消门等待与解决方案重写。</para></param>
    /// <returns><see langword="true"/> when the canvas became active (including when it already was);
    /// <see langword="false"/> when no open node project matches <paramref name="name"/>.
    /// <para>画布成功活动（含原本就是活动项）时返回 <see langword="true"/>；没有匹配的已打开节点项目时
    /// 返回 <see langword="false"/>。</para></returns>
    public async Task<bool> SetActiveNodeProjectAsync(string name, CancellationToken cancellationToken = default)
    {
        var activated = false;
        await RunOperationAsync(async () =>
        {
            if (Context.NodeProjects.FirstOrDefault(node =>
                    string.Equals(node.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } descriptor)
                return;
            var path = CurrentProjectPath ?? throw new InvalidOperationException("No solution is open.");
            _workspace.ActivateWorkflow(descriptor.Name);
            Context.SetActiveNodeProject(descriptor.Name);
            await PersistNodeProjectReferencesAsync(path, cancellationToken);
            activated = true;
            Changed?.Invoke();
        }, cancellationToken: cancellationToken);
        return activated;
    }

    /// <summary>
    /// Reads the current solution file for in-place editing of its node project list; throws when the file
    /// cannot be read.
    /// <para>读取当前解决方案文件用于就地编辑其节点项目清单；文件不可读时抛出异常。</para>
    /// </summary>
    /// <param name="path">Solution file path. <para>解决方案文件路径。</para></param>
    /// <param name="cancellationToken">Cancels the read. <para>取消读取。</para></param>
    /// <returns>The stored snapshot with its sub-project references and extension state preserved.
    /// <para>已存储的快照，其子项目引用与扩展状态保持不变。</para></returns>
    /// <exception cref="InvalidDataException">The solution file cannot be read.
    /// <para>解决方案文件无法读取。</para></exception>
    private async Task<SolutionSnapshot> ReadSolutionForEditAsync(string path, CancellationToken cancellationToken)
        => await SolutionPersistenceService.LoadSolutionAsync(path, cancellationToken)
            ?? throw new InvalidDataException("Cannot read the current solution.");

    /// <summary>
    /// Rewrites the solution file's node project list from the editor's current open state.
    /// <para>按编辑器当前打开状态重写解决方案文件中的节点项目清单。</para>
    /// </summary>
    /// <param name="path">Solution file path. <para>解决方案文件路径。</para></param>
    /// <param name="cancellationToken">Cancels the read and the rewrite.
    /// <para>取消读取与重写。</para></param>
    /// <exception cref="InvalidDataException">The solution file cannot be read or the rewritten list fails
    /// validation. <para>解决方案文件无法读取，或重写后的清单未通过校验。</para></exception>
    /// <exception cref="IOException">The solution file could not be written.
    /// <para>解决方案文件无法写入。</para></exception>
    private async Task PersistNodeProjectReferencesAsync(string path, CancellationToken cancellationToken)
    {
        var solution = await ReadSolutionForEditAsync(path, cancellationToken);
        solution.NodeProjects = NodeProjectReferences();
        ProjectValidation.ValidateSolution(solution);
        RequireSaved(await SolutionPersistenceService.SaveSolutionAsync(path, solution, cancellationToken), path);
    }

    /// <summary>
    /// Derives the node project list to write from the registered projects and the documents the editor
    /// actually holds open: only open documents are open entries, and the active entry must be one of them.
    /// <para>由解决方案登记的节点项目和编辑器实际打开的文档推导出要写出的节点项目清单：只有编辑器打开的
    /// 文档才是打开项，活动项必须是打开项之一。</para>
    /// </summary>
    /// <returns>One reference per registered node project, empty when the solution registers none.
    /// <para>每个已登记节点项目一条引用；解决方案未登记时为空。</para></returns>
    private List<NodeProjectDto> NodeProjectReferences()
    {
        var openNames = _workspace.OpenWorkflowNames;
        var activeName = _workspace.ActiveWorkflowName;
        var references = Context.NodeProjects.Select(node => new NodeProjectDto
        {
            Name = node.Name,
            RelativePath = node.RelativePath,
            IsOpen = IsWorkflowOpen(openNames, node.Name),
            IsActive = false
        }).ToList();
        var active = references.FirstOrDefault(dto => dto.IsOpen
            && string.Equals(dto.Name, activeName, StringComparison.OrdinalIgnoreCase))
            ?? references.FirstOrDefault(dto => dto.IsOpen);
        if (active is not null) active.IsActive = true;
        return references;
    }
}