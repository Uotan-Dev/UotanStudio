using UotanStudio.Services;
using UotanStudio.Services.Firmware;
using UotanStudio.Services.Projects;

namespace UotanStudio.ViewModels.Workspace;

internal partial class ImageViewModel
{
    private static string? ResolveHistoryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
        return ProjectContextService.Instance.ResolvePath(path);
    }

    private static void MoveHistoryPath(string source, string destination)
    {
        if (File.Exists(source))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (File.Exists(destination) || Directory.Exists(destination)) return;
            File.Move(source, destination);
        }
        else if (Directory.Exists(source))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (File.Exists(destination) || Directory.Exists(destination)) return;
            Directory.Move(source, destination);
        }
    }

    internal static void UndoOutput(HistoryOperationDto operation)
    {
        for (var index = 0; index < operation.OutputPaths.Count; index++)
        {
            var output = ResolveHistoryPath(operation.OutputPaths[index]);
            var backup = index < operation.BackupPaths.Count ? ResolveHistoryPath(operation.BackupPaths[index]) : null;
            if (output is null || backup is null) continue;
            MoveHistoryPath(output, backup);
        }
    }

    internal static void RedoOutput(HistoryOperationDto operation)
    {
        for (var index = 0; index < operation.OutputPaths.Count; index++)
        {
            var output = ResolveHistoryPath(operation.OutputPaths[index]);
            var backup = index < operation.BackupPaths.Count ? ResolveHistoryPath(operation.BackupPaths[index]) : null;
            if (output is null || backup is null) continue;
            MoveHistoryPath(backup, output);
        }
    }

    internal static HistoryOperationDto? CreateOutputOperation(
        HistoryOperationKind kind, string description, IEnumerable<string> outputPaths)
    {
        var context = ProjectContextService.Instance;
        var outputs = outputPaths.Select(context.RelativePath).ToList();
        if (outputs.Any(path => path is null)) return null;

        var historyId = Guid.NewGuid().ToString("N");
        var backups = outputs.Select(path =>
        {
            var relative = path!.Replace('/', Path.DirectorySeparatorChar);
            return context.RelativePath(Path.Combine(context.ActiveSubProject!.RootPath,
                ".deleted", ".history", historyId, relative));
        }).ToList();
        if (backups.Any(path => path is null)) return null;

        return new HistoryOperationDto
        {
            Kind = kind,
            Description = description,
            OutputPaths = [.. outputs.Select(path => path!)],
            BackupPaths = [.. backups.Select(path => path!)]
        };
    }

    /// <summary>
    /// Reconstructs undo/redo actions from a persisted operation descriptor (for cross-session history restore).
    /// <para>从持久化操作描述符重建撤销/重做动作（供操作历史跨会话恢复）。</para>
    /// </summary>
    private static (Action Undo, Action Redo) ReconstructOperation(HistoryOperationDto operation)
    {
        switch (operation.Kind)
        {
            case HistoryOperationKind.ImportImages:
                return ReconstructImport(operation);
            case HistoryOperationKind.DeleteImage:
                return ReconstructDelete(operation);
            case HistoryOperationKind.ExtractImage:
                return (() => UndoOutput(operation), () => RedoOutput(operation));
            case HistoryOperationKind.SynthesizeImg:
            case HistoryOperationKind.SynthesizeBr:
            case HistoryOperationKind.SynthesizeSuper:
                return (() => UndoOutput(operation), () => RedoOutput(operation));
            case HistoryOperationKind.SynthesizeDat:
                return (() => UndoOutput(operation), () => RedoOutput(operation));
            default:
                LogService.Instance.Write(LogLevel.Warn, "History",
                    $"Unknown history operation kind: {operation.Kind}");
                return (
                    () => LogService.Instance.Write(LogLevel.Warn, "History", $"Undo: unknown operation '{operation.Kind}'"),
                    () => LogService.Instance.Write(LogLevel.Warn, "History", $"Redo: unknown operation '{operation.Kind}'")
                );
        }
    }

    private static (Action Undo, Action Redo) ReconstructImport(HistoryOperationDto operation)
    {
        var vm = EnsureInstance();
        return (
            () =>
            {
                if (operation.OutputPaths.Count > 0 && operation.BackupPaths.Count == operation.OutputPaths.Count)
                    UndoOutput(operation);
                foreach (var relative in operation.RelativePaths)
                {
                    var entry = vm.Images.FirstOrDefault(i => i.RelativePath == relative);
                    if (entry is not null)
                    {
                        vm.Images.Remove(entry);
                    }
                }
            },
            () =>
            {
                if (operation.OutputPaths.Count > 0 && operation.BackupPaths.Count == operation.OutputPaths.Count)
                    RedoOutput(operation);
                foreach (var relative in operation.RelativePaths)
                {
                    if (vm.Images.Any(i => i.RelativePath == relative))
                    {
                        continue;
                    }

                    var absolute = ProjectContextService.Instance.ResolvePath(relative);
                    if (absolute is null || !File.Exists(absolute))
                    {
                        continue;
                    }

                    vm.Images.Add(new ImageEntryViewModel
                    {
                        DisplayName = Path.GetFileName(relative),
                        RelativePath = relative,
                        SizeBytes = new FileInfo(absolute).Length,
                        Format = ImageInputService.DetectFormat(absolute)
                    });
                }
            }
        );
    }

    private static (Action Undo, Action Redo) ReconstructDelete(HistoryOperationDto operation)
    {
        var vm = EnsureInstance();
        var source = ResolveHistoryPath(operation.RelativePath);
        var destination = ResolveHistoryPath(operation.DestinationPath);
        return (
            () =>
            {
                if (source is null || destination is null || !File.Exists(destination))
                {
                    return;
                }

                File.Move(destination, source);
                vm.Images.Add(new ImageEntryViewModel
                {
                    DisplayName = Path.GetFileName(operation.RelativePath),
                    RelativePath = operation.RelativePath,
                    SizeBytes = new FileInfo(source).Length,
                    Format = ImageInputService.DetectFormat(source)
                });
            },
            () =>
            {
                if (source is null || destination is null || !File.Exists(source))
                {
                    return;
                }

                File.Move(source, destination);
                var entry = vm.Images.FirstOrDefault(i => i.RelativePath == operation.RelativePath);
                if (entry is not null)
                {
                    vm.Images.Remove(entry);
                }
            }
        );
    }
}