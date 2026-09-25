namespace UotanStudio.Services.Projects;

public sealed partial class ProjectSession
{
    /// <summary>
    /// Renames a solution reference, preserving its directory and editor file identities.
    /// <para>重命名解决方案中的引用，其目录与编辑器文件身份保持不变。</para>
    /// </summary>
    /// <param name="name">Current display name of the sub-project, matched case-insensitively.
    /// <para>子项目当前显示名，按大小写不敏感匹配。</para></param>
    /// <param name="newName">Replacement display name; must pass <see cref="ProjectValidation.ValidateName"/>.
    /// <para>替换后的显示名，需通过 <see cref="ProjectValidation.ValidateName"/>。</para></param>
    /// <param name="cancellationToken">Cancels the gate wait and the solution rewrite.
    /// <para>取消门等待与解决方案重写。</para></param>
    /// <returns><see langword="true"/> when the reference was renamed and persisted; <see langword="false"/>
    /// when no sub-project matches <paramref name="name"/> or the operation was gated.
    /// <para>引用成功重命名并持久化时返回 <see langword="true"/>；没有匹配 <paramref name="name"/> 的
    /// 子项目或操作被门拦截时返回 <see langword="false"/>。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="newName"/> is blank, longer than 255
    /// characters, or contains control or path-separator characters.
    /// <para><paramref name="newName"/> 为空白、超过 255 个字符，或包含控制字符/路径分隔符。</para></exception>
    /// <exception cref="InvalidOperationException">No solution is open, or the new name is already used
    /// by another sub-project. <para>尚未打开解决方案，或新名称已被其他子项目占用。</para></exception>
    /// <exception cref="InvalidDataException">The current solution file cannot be read or fails
    /// validation. <para>当前解决方案文件无法读取或未通过校验。</para></exception>
    public Task<bool> RenameSubProjectAsync(string name, string newName, CancellationToken cancellationToken = default)
    {
        ProjectValidation.ValidateName(newName);
        return EditSubProjectReferenceAsync(name, newName, cancellationToken);
    }

    /// <summary>
    /// Removes a reference without deleting any project files. The last reference cannot be removed.
    /// <para>仅移除引用，不删除任何工程文件；最后一个引用不可移除。</para>
    /// </summary>
    /// <param name="name">Display name of the sub-project to remove, matched case-insensitively.
    /// <para>待移除子项目的显示名，按大小写不敏感匹配。</para></param>
    /// <param name="cancellationToken">Cancels the gate wait and the solution rewrite.
    /// <para>取消门等待与解决方案重写。</para></param>
    /// <returns><see langword="true"/> when the reference has been removed and the solution reloaded;
    /// <see langword="false"/> when no sub-project matches <paramref name="name"/> or the operation was
    /// gated. <para>引用成功移除并重新加载解决方案时返回 <see langword="true"/>；没有匹配
    /// <paramref name="name"/> 的子项目或操作被门拦截时返回 <see langword="false"/>。</para></returns>
    /// <exception cref="InvalidOperationException">No solution is open, or this is the last remaining
    /// reference. <para>尚未打开解决方案，或这是最后一个引用。</para></exception>
    /// <exception cref="InvalidDataException">The current solution, the replacement active sub-project,
    /// or the reloaded solution cannot be read.
    /// <para>当前解决方案、替换后的活动子项目或重新加载的解决方案无法读取。</para></exception>
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
            if (newName is null && active == project)
            {
                var reference = solution.SubProjects[0];
                _ = await SolutionPersistenceService.LoadFirmwareProjectAsync(Context.ResolvePath(reference.RelativePath)!, token)
                    ?? throw new InvalidDataException("Cannot read the replacement active sub-project.");
            }
            RequireSaved(await SolutionPersistenceService.SaveSolutionAsync(path, solution, token), path);
            if (newName is null)
            {
                await OpenProjectFromPathCoreAsync(path, token);
                changed = true;
                return;
            }
            Context.OpenSolution(path, solution);
            if (active is not null) Context.SetActiveSubProject(active == project ? newName! : active.Name);
            changed = true;
            Changed?.Invoke();
        }, changingProject: true, cancellationToken: token);
        return changed;
    }

    /// <summary>
    /// Executes history under the same gate as project persistence and changes.
    /// <para>在与工程持久化和切换相同的门控下执行历史撤销。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the gate wait and the undo action.
    /// <para>取消门等待与撤销动作。</para></param>
    /// <returns><see langword="true"/> when an entry was undone; <see langword="false"/> when the history
    /// is empty. <para>成功撤销一项时返回 <see langword="true"/>；历史为空时返回 <see langword="false"/>。</para></returns>
    public async Task<bool> UndoAsync(CancellationToken cancellationToken = default)
    {
        var result = false;
        await RunOperationAsync(async () => result = await History.UndoAsync(cancellationToken), cancellationToken: cancellationToken);
        return result;
    }

    /// <summary>
    /// Executes redo under the same gate as project persistence and changes.
    /// <para>在与工程持久化和切换相同的门控下执行历史重做。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the gate wait and the redo action.
    /// <para>取消门等待与重做动作。</para></param>
    /// <returns><see langword="true"/> when an entry was redone; <see langword="false"/> when the redo
    /// stack is empty. <para>成功重做一项时返回 <see langword="true"/>；重做栈为空时返回
    /// <see langword="false"/>。</para></returns>
    public async Task<bool> RedoAsync(CancellationToken cancellationToken = default)
    {
        var result = false;
        await RunOperationAsync(async () => result = await History.RedoAsync(cancellationToken), cancellationToken: cancellationToken);
        return result;
    }
}