namespace UotanStudio.Services.Projects;

/// <summary>Desktop history compatibility facade; execution delegates are provided through the reconstruction hook.
/// <para>桌面历史兼容门面；执行委托经由重建钩子提供。</para></summary>
public sealed class OperationHistoryService
{
    public const int Capacity = ProjectHistory.Capacity;
    public static OperationHistoryService Instance { get; } = new();
    internal ProjectHistory Core { get; } = new();
    private OperationHistoryService() { }
    public static Func<HistoryOperationDto, (Action Undo, Action Redo)>? ReconstructOperation
    {
        get => Instance.Core.ReconstructOperation;
        set => Instance.Core.ReconstructOperation = value;
    }
    public bool CanUndo => Core.CanUndo;
    public bool CanRedo => Core.CanRedo;
    public string? NextUndoDescription => Core.NextUndoDescription;
    public string? NextRedoDescription => Core.NextRedoDescription;
    public void Record(string description, Action undo, Action redo) => Core.Record(description, undo, redo);
    public void Record(string description, HistoryOperationDto? operation, Action undo, Action redo) => Core.Record(description, operation, undo, redo);
    public Task<bool> UndoAsync(CancellationToken cancellationToken = default) => Core.UndoAsync(cancellationToken);
    public Task<bool> RedoAsync(CancellationToken cancellationToken = default) => Core.RedoAsync(cancellationToken);
    public void RecordAsync(string description, HistoryOperationDto? operation, Func<CancellationToken, Task> undo, Func<CancellationToken, Task> redo)
        => Core.RecordAsync(description, operation, undo, redo);
    public bool Undo() => Core.Undo();
    public bool Redo() => Core.Redo();
    public void Clear() => Core.Clear();
    public Task SaveAsync(string path, CancellationToken cancellationToken = default) => Core.SaveAsync(path, cancellationToken);
    public Task LoadAsync(string path, CancellationToken cancellationToken = default) => Core.LoadAsync(path, cancellationToken);
    internal List<HistoryOperationDto> CaptureSnapshot() => Core.CaptureSnapshot();
}