using UotanStudio.Services;
using UotanStudio.Services.Firmware;
using UotanStudio.Services.Projects;

namespace UotanStudio.ViewModels.Workspace;

internal partial class ImageViewModel
{
    /// <summary>
    /// <para>从持久化操作描述符重建撤销/重做动作（供操作历史跨会话恢复）。</para>
    /// Reconstructs undo/redo actions from a persisted operation descriptor (for cross-session history restore).
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
                return (() => DeleteDirectorySafe(operation.OutputPaths.FirstOrDefault() ?? string.Empty), () => { });
            case HistoryOperationKind.SynthesizeImg:
            case HistoryOperationKind.SynthesizeBr:
            case HistoryOperationKind.SynthesizeSuper:
                return (() => DeleteFileSafe(operation.OutputPaths.FirstOrDefault() ?? string.Empty), () => { });
            case HistoryOperationKind.SynthesizeDat:
                return (() =>
                {
                    foreach (var path in operation.OutputPaths)
                    {
                        DeleteFileSafe(path);
                    }
                }, () => { }
                );
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
        var source = ProjectContextService.Instance.ResolvePath(operation.RelativePath);
        return (
            () =>
            {
                if (source is null || !File.Exists(operation.DestinationPath))
                {
                    return;
                }

                File.Move(operation.DestinationPath, source);
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
                if (source is null || !File.Exists(source))
                {
                    return;
                }

                File.Move(source, operation.DestinationPath);
                var entry = vm.Images.FirstOrDefault(i => i.RelativePath == operation.RelativePath);
                if (entry is not null)
                {
                    vm.Images.Remove(entry);
                }
            }
        );
    }
}
