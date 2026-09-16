namespace UotanStudio.Services.Projects;

public sealed partial class ProjectSession
{
    /// <summary>Renames a solution reference, preserving its directory and editor file identities.</summary>
    public Task<bool> RenameSubProjectAsync(string name, string newName, CancellationToken cancellationToken = default)
    {
        ProjectValidation.ValidateName(newName);
        return EditSubProjectReferenceAsync(name, newName, cancellationToken);
    }

    /// <summary>Removes a reference without deleting any project files. The last reference cannot be removed.</summary>
    public Task<bool> RemoveSubProjectAsync(string name, CancellationToken cancellationToken = default)
        => EditSubProjectReferenceAsync(name, null, cancellationToken);

    private async Task<bool> EditSubProjectReferenceAsync(string name, string? newName, CancellationToken token)
    {
        var changed = false;
        await RunOperationAsync(async () =>
        {
            var path = CurrentProjectPath ?? throw new InvalidOperationException("No solution is open.");
            var project = Context.SubProjects.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (project is null) return;
            if (newName is null && Context.SubProjects.Count == 1)
                throw new InvalidOperationException("The last sub-project cannot be removed.");
            if (newName is not null && Context.SubProjects.Any(p => p != project && string.Equals(p.Name, newName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("The sub-project name already exists.");
            var solution = await SolutionPersistenceService.LoadSolutionAsync(path, token)
                ?? throw new InvalidDataException("Cannot read the current solution.");
            solution.FormatVersion = 2;
            solution.SubProjects = Context.SubProjects.Where(p => newName is not null || p != project).Select(p => new SubProjectDto
            {
                Name = p == project ? newName! : p.Name,
                RelativePath = Path.GetRelativePath(Context.RootDirectory!, p.FwProjectPath).Replace(Path.DirectorySeparatorChar, '/')
            }).ToList();
            ProjectValidation.ValidateSolution(solution);
            var active = Context.ActiveSubProject;
            // Validate the successor before publishing a removal of the active project.
            if (newName is null && active == project)
            {
                var reference = solution.SubProjects[0];
                _ = await SolutionPersistenceService.LoadFirmwareProjectAsync(Context.ResolvePath(reference.RelativePath)!, token)
                    ?? throw new InvalidDataException("Cannot read the replacement active sub-project.");
            }
            RequireSaved(await SolutionPersistenceService.SaveSolutionAsync(path, solution, token), path);
            if (newName is null)
            {
                // 删除子项目后与新增子项目一致：从磁盘完整重载解决方案，使镜像列表、设置、文档与
                // 交付面板都基于新的子项目集合重建，而不是只改 Context 留下残留状态。
                // Removing a sub-project reloads the whole solution from disk like adding one, so the image
                // list, settings, documents and delivery panel rebuild from the new sub-project set instead
                // of leaving state behind on a context-only mutation.
                await OpenProjectFromPathCoreAsync(path, token);
                changed = true;
                return;
            }
            // 重命名保持轻量路径：文档保持打开，只更换引用标识。
            // Rename keeps the lighter path: documents stay open, only the reference identity changes.
            Context.OpenSolution(path, solution);
            if (active is not null) Context.SetActiveSubProject(active == project ? newName! : active.Name);
            changed = true;
            Changed?.Invoke();
        }, changingProject: true, cancellationToken: token);
        return changed;
    }

    /// <summary>Executes history under the same gate as project persistence and changes.</summary>
    public async Task<bool> UndoAsync(CancellationToken cancellationToken = default)
    {
        var result = false;
        await RunOperationAsync(async () => result = await History.UndoAsync(cancellationToken), cancellationToken: cancellationToken);
        return result;
    }

    /// <summary>Executes redo under the same gate as project persistence and changes.</summary>
    public async Task<bool> RedoAsync(CancellationToken cancellationToken = default)
    {
        var result = false;
        await RunOperationAsync(async () => result = await History.RedoAsync(cancellationToken), cancellationToken: cancellationToken);
        return result;
    }
}