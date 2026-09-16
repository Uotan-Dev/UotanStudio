namespace UotanStudio.Services.Projects;

public sealed partial class ProjectSession
{
    /// <summary>
    /// <para>另存为新的解决方案。平铺模型下目标子项目是目标目录（目标解决方案所在目录）的直接子级：
    /// 目标目录与源目录不同时，把每个源子项目文件夹复制为目标目录的直接子级；未打开工程时在目标目录
    /// 落出一个仅含单个子项目的新工程。源上下文在失败时保持原样，源子项目文件不会被改写。</para>
    /// Publishes a copy under the flat model: destination sub-projects are direct children of the destination
    /// directory (the directory holding the target solution). When the destination differs from the source
    /// directory, each source sub-project folder is copied as a direct child; when no project is open, a fresh
    /// single-sub-project solution is published there. The source context stays intact on failure and source
    /// sub-project files are never rewritten.
    /// </summary>
    private async Task SaveAsCoreAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var destinationName = Path.GetFileNameWithoutExtension(fullPath);
        if (string.IsNullOrWhiteSpace(destinationName) || destinationName is "." or "..")
            throw new ArgumentException("A non-empty project name is required.", nameof(path));
        if (Path.GetExtension(fullPath).Length == 0)
            throw new ArgumentException("A solution file extension is required.", nameof(path));

        var context = Context;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (context.CurrentSolutionPath is { } current && string.Equals(current, fullPath, comparison))
        {
            await PersistCoreAsync(fullPath, true, cancellationToken);
            return;
        }

        var destinationDirectory = Path.GetDirectoryName(fullPath)!;
        var node = _workspace.CaptureWorkflow(destinationName);
        var images = _workspace.CaptureImages();
        var packing = _workspace.CaptureSettings();
        // Existing editors keep their original file identity. Store those paths relative to the new root.
        var documents = _workspace.CaptureDocuments(destinationDirectory);
        var history = History.CaptureSnapshot();

        if (context.RootDirectory is null || context.SubProjects.Count == 0)
        {
            // 未打开工程：在目标目录发布一个仅含单个同名子项目的新工程。
            // No project open: publish a fresh single-sub-project solution at the destination.
            var fresh = new SubProjectDto { Name = destinationName, RelativePath = $"{destinationName}/{destinationName}.uotanfw" };
            var freshSettings = new SolutionSettings { RecentDocuments = documents.Select(d => d.RelativePath).Where(p => p.Length > 0).ToList() };
            await PublishCopyAsync(fullPath, destinationDirectory, destinationName, [fresh], fresh, node, images, packing, documents, freshSettings, history, cancellationToken);
            return;
        }

        // 目标目录不得落在任一源子项目文件夹内，避免把新解决方案写进子项目区域。
        // The destination directory must not sit inside a source sub-project folder.
        if (context.SubProjects.Any(sp => ProjectSaveAsTransaction.IsWithin(sp.RootPath, destinationDirectory)))
            throw new IOException("The source and destination project directories must not overlap.");

        var subProjects = context.SubProjects.ToList();
        var references = subProjects.Select(sp => new SubProjectDto
        {
            Name = sp.Name,
            RelativePath = $"{sp.Name}/{sp.Name}.uotanfw"
        }).ToList();
        var active = references.FirstOrDefault(sp => string.Equals(sp.Name, context.ActiveSubProject?.Name, StringComparison.OrdinalIgnoreCase))
            ?? references.FirstOrDefault(sp => string.Equals(sp.Name, destinationName, StringComparison.OrdinalIgnoreCase))
            ?? references[0];
        var sourceSettingsPath = context.StateDirectory is { } state ? Path.Combine(state, "settings.json") : null;
        var settings = sourceSettingsPath is null ? new SolutionSettings()
            : await SolutionSettingsService.LoadAsync(sourceSettingsPath, cancellationToken)
                ?? (File.Exists(sourceSettingsPath) ? throw new InvalidDataException("Cannot read the source project settings.") : new SolutionSettings());
        settings.RecentDocuments = documents.Select(d => d.RelativePath).Where(p => p.Length > 0).ToList();

        await PublishCopyAsync(fullPath, destinationDirectory, destinationName, references, active, node, images, packing, documents, settings, history, cancellationToken);
    }

    /// <summary>
    /// <para>把源子项目文件夹复制为目标目录的直接子级，并写入固件/节点/状态与解决方案文件，随后打开新工程。
    /// 目标子项目与源子项目为同一文件夹时不复制（同目录另存即引用源子项目）。</para>
    /// Copies source sub-project folders as direct children of the destination directory, writes the firmware /
    /// node / state and solution files, then opens the published project. A destination sub-project that already
    /// is the source sub-project folder is not copied (same-directory Save As references the source sub-project).
    /// </summary>
    private async Task PublishCopyAsync(
        string fullPath, string destinationDirectory, string destinationName,
        IReadOnlyList<SubProjectDto> references, SubProjectDto active,
        NodeProjectSnapshot node, List<ImageEntryDto> images, ProjectSettingsDto packing, List<OpenDocumentDto> documents,
        SolutionSettings settings, List<HistoryOperationDto> history, CancellationToken cancellationToken)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var reference in references)
        {
            var sourceSub = Context.SubProjects.FirstOrDefault(sp => string.Equals(sp.Name, reference.Name, StringComparison.OrdinalIgnoreCase));
            if (sourceSub is null) continue;
            var destinationRoot = Path.Combine(destinationDirectory, reference.Name);
            if (string.Equals(Path.GetFullPath(sourceSub.RootPath), Path.GetFullPath(destinationRoot), comparison)) continue;
            Directory.CreateDirectory(destinationRoot);
            CopyDirectoryContents(sourceSub.RootPath, destinationRoot, cancellationToken);
        }

        var activeFolder = Path.Combine(destinationDirectory, active.Name);
        Directory.CreateDirectory(Path.Combine(activeFolder, "images"));
        Directory.CreateDirectory(Path.Combine(activeFolder, "output"));
        Directory.CreateDirectory(Path.Combine(activeFolder, "delivery"));
        await AtomicJsonFile.WriteIfChangedAsync(Path.Combine(activeFolder, active.Name + ".uotanfw"), new FirmwareProjectSnapshot
        {
            FormatVersion = 1,
            ProjectName = active.Name,
            Images = images,
            Settings = packing,
            OpenDocuments = documents.Count > 0 ? documents : null
        }, SolutionJsonContext.Default.FirmwareProjectSnapshot, cancellationToken);
        await AtomicJsonFile.WriteIfChangedAsync(Path.Combine(activeFolder, destinationName + ".uotannode"), node,
            SolutionJsonContext.Default.NodeProjectSnapshot, cancellationToken);
        await AtomicJsonFile.WriteIfChangedAsync(Path.Combine(activeFolder, ".uotan", "settings.json"), settings,
            SolutionSettingsJsonContext.Default.SolutionSettings, cancellationToken);
        await AtomicJsonFile.WriteIfChangedAsync(Path.Combine(activeFolder, ".uotan", "history.json"), history,
            HistoryJsonContext.Default.ListHistoryOperationDto, cancellationToken);
        var solution = new SolutionSnapshot
        {
            SolutionName = destinationName,
            NodeProjectPath = $"{active.Name}/{destinationName}.uotannode",
            SubProjects = [.. references],
            State = CaptureState?.Invoke()
        };
        await AtomicJsonFile.WriteIfChangedAsync(fullPath, solution, SolutionJsonContext.Default.SolutionSnapshot, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        Context.OpenSolution(fullPath, solution);
        Context.SetActiveSubProject(active.Name);
        CurrentProjectPath = fullPath;
        _workspace.ChangeProjectRoot(Context.RootDirectory!, preserveDocuments: true);
        Changed?.Invoke();
    }

    /// <summary>
    /// <para>递归复制目录内容（跳过符号链接/联接指向，避免复制工程外路径）。</para>
    /// Recursively copies a directory's contents, rejecting symbolic links and junctions.
    /// </summary>
    private static void CopyDirectoryContents(string source, string destination, CancellationToken cancellationToken)
    {
        var pending = new Stack<(string Source, string Destination)>();
        pending.Push((source, destination));
        while (pending.TryPop(out var pair))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectLink(pair.Source);
            Directory.CreateDirectory(pair.Destination);
            foreach (var entry in Directory.EnumerateFileSystemEntries(pair.Source))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = RejectLink(entry);
                var target = Path.Combine(pair.Destination, Path.GetFileName(entry));
                if ((attributes & FileAttributes.Directory) != 0) pending.Push((entry, target));
                else
                {
                    using var input = File.OpenRead(entry);
                    using var output = File.Create(target);
                    input.CopyTo(output);
                }
            }
        }
    }

    private static FileAttributes RejectLink(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Save As cannot copy a symbolic link or junction: {path}");
        return attributes;
    }
}