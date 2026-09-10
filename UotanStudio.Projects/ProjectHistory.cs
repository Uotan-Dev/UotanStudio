using System.Text.Json;
using System.Text.Json.Serialization;

namespace UotanStudio.Services.Projects;

/// <summary>
/// Kinds of workspace operations that can be reconstructed across sessions.
/// <para>可在跨会话中重建的工作区操作类型。</para>
/// </summary>
public enum HistoryOperationKind
{
    /// <summary><para>导入镜像。</para>Import images.</summary>
    ImportImages,

    /// <summary><para>解包镜像。</para>Extract an image.</summary>
    ExtractImage,

    /// <summary><para>合成 boot 镜像。</para>Synthesize a boot image.</summary>
    SynthesizeImg,

    /// <summary><para>打包 CPIO。</para>Pack a CPIO archive.</summary>
    SynthesizeBr,

    /// <summary><para>删除镜像。</para>Delete an image.</summary>
    DeleteImage,

    /// <summary><para>合成 super 镜像。</para>Synthesize a super image.</summary>
    SynthesizeSuper,

    /// <summary><para>生成 DAT 镜像对。</para>Generate a DAT image pair.</summary>
    SynthesizeDat
}

/// <summary>
/// Serializable description of a workspace operation, used to reconstruct undo/redo actions after a restart.
/// <para>工作区操作的可序列化描述，用于重启后重建撤销/重做动作。</para>
/// </summary>
public sealed class HistoryOperationDto
{
    /// <summary>
    /// The operation kind.
    /// <para>操作类型。</para>
    /// </summary>
    public HistoryOperationKind Kind { get; set; }

    /// <summary>
    /// The operation description shown in undo/redo menus.
    /// <para>撤销/重做菜单中显示的操作描述。</para>
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// The display name of the affected image.
    /// <para>受影响镜像的显示名称。</para>
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// The number of affected items (e.g. imported images).
    /// <para>受影响项的数量（如导入的镜像数）。</para>
    /// </summary>
    public int Count { get; set; }

    /// <summary>
    /// Project-relative paths of the affected items (e.g. imported images).
    /// <para>受影响项的项目相对路径（如导入的镜像）。</para>
    /// </summary>
    public List<string> RelativePaths { get; set; } = [];

    /// <summary>
    /// Project-relative path of a single affected item (e.g. the deleted image).
    /// <para>单个受影响项的项目相对路径（如被删除的镜像）。</para>
    /// </summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>
    /// Absolute destination path (e.g. where a deleted image was moved).
    /// <para>目标绝对路径（如被删除镜像的移动位置）。</para>
    /// </summary>
    public string DestinationPath { get; set; } = string.Empty;

    /// <summary>
    /// Output file/directory paths produced by the operation (deleted on undo).
    /// <para>操作产出的输出文件/目录路径（撤销时删除）。</para>
    /// </summary>
    public List<string> OutputPaths { get; set; } = [];
}

/// <summary>
/// Operation-history JSON serialization context (AOT source-generated).
/// <para>操作历史 JSON 序列化上下文（AOT 源生成）。</para>
/// </summary>
[JsonSerializable(typeof(List<HistoryOperationDto>))]
[JsonSerializable(typeof(HistoryOperationDto))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(JsonStringEnumConverter<HistoryOperationKind>)])]
internal partial class HistoryJsonContext : JsonSerializerContext;

/// <summary>
/// Per-session operation history, maintaining in-memory undo/redo stacks in command pattern.
/// Entries recorded with a <see cref="HistoryOperationDto"/> can be persisted to a history file and
/// reconstructed across sessions via <see cref="ReconstructOperation"/>.
/// <para>会话级操作历史，以命令模式维护内存撤销/重做栈。带 <see cref="HistoryOperationDto"/>
/// 登记的条目可持久化到历史文件，并经 <see cref="ReconstructOperation"/> 跨会话重建。</para>
/// </summary>
public sealed class ProjectHistory
{
    /// <summary>
    /// Maximum history capacity; the oldest undo entry is dropped when exceeded.
    /// <para>历史容量上限，超出后丢弃最旧的撤销条目。</para>
    /// </summary>
    public const int Capacity = 50;


    /// <summary>
    /// Factory used to reconstruct undo/redo actions from a persisted operation; registered by the workspace
    /// (e.g. the image panel). <c>null</c> disables history reconstruction.
    /// <para>从持久化操作重建撤销/重做动作的工厂，由工作区（如镜像面板）注册；为 <c>null</c> 时不重建历史。</para>
    /// </summary>
    public Func<HistoryOperationDto, (Action Undo, Action Redo)>? ReconstructOperation { get; set; }

    /// <summary>True while an undo/redo callback is running. Use this object on its owning synchronization context.</summary>
    public bool IsExecuting { get; private set; }

    private void EnsureIdle()
    {
        if (IsExecuting) throw new InvalidOperationException("A history action is running.");
    }

    /// <summary>Records cancellable asynchronous actions. Failed/cancelled execution retains the original stack entry.</summary>
    public void RecordAsync(string description, HistoryOperationDto? operation,
        Func<CancellationToken, Task> undo, Func<CancellationToken, Task> redo)
    {
        ArgumentNullException.ThrowIfNull(undo);
        ArgumentNullException.ThrowIfNull(redo);
        Record(description, operation, () => { }, () => { });
        var entry = _undoStack.Pop();
        _undoStack.Push(new(entry.Description, entry.Operation, null, null) { UndoAsync = undo, RedoAsync = redo });
    }

    /// <summary>Awaits undo before moving the entry to the redo stack.</summary>
    public Task<bool> UndoAsync(CancellationToken cancellationToken = default) => ExecuteAsync(true, cancellationToken);
    /// <summary>Awaits redo before moving the entry to the undo stack.</summary>
    public Task<bool> RedoAsync(CancellationToken cancellationToken = default) => ExecuteAsync(false, cancellationToken);

    private async Task<bool> ExecuteAsync(bool undo, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        EnsureIdle();
        if (undo ? !CanUndo : !CanRedo) return false;
        var source = undo ? _undoStack : _redoStack;
        var destination = undo ? _redoStack : _undoStack;
        var entry = source.Peek();
        IsExecuting = true;
        try
        {
            var callback = undo ? entry.UndoAsync : entry.RedoAsync;
            if (callback is not null) await callback(token);
            else (undo ? entry.UndoAction : entry.RedoAction)!();
            source.Pop();
            destination.Push(entry);
            return true;
        }
        finally { IsExecuting = false; }
    }

    /// <summary>Optional asynchronous action factory; takes precedence over the legacy synchronous factory.</summary>
    public Func<HistoryOperationDto, (Func<CancellationToken, Task> Undo, Func<CancellationToken, Task> Redo)>? ReconstructAsyncOperation { get; set; }

    private HistoryEntry Reconstruct(HistoryOperationDto operation)
    {
        if (ReconstructAsyncOperation is { } asyncFactory)
        {
            var (undo, redo) = asyncFactory(operation);
            return new(operation.Description, operation, null, null) { UndoAsync = undo, RedoAsync = redo };
        }
        (Action? undoAction, Action? redoAction) = ReconstructOperation is { } factory ? factory(operation) : (null, null);
        return new(operation.Description, operation, undoAction, redoAction);
    }

    private readonly Stack<HistoryEntry> _undoStack = new();
    private readonly Stack<HistoryEntry> _redoStack = new();
    private readonly List<HistoryEntry> _trimBuffer = new(Capacity);

    /// <summary>Creates independent history with no default executable actions.</summary>
    public ProjectHistory()
    {
    }

    /// <summary>
    /// Whether an undo operation can be executed.
    /// <para>是否可以执行撤销。</para>
    /// </summary>
    public bool CanUndo => !IsExecuting && _undoStack.TryPeek(out var entry) && (entry.UndoAction is not null || entry.UndoAsync is not null);

    /// <summary>
    /// Whether a redo operation can be executed.
    /// <para>是否可以执行重做。</para>
    /// </summary>
    public bool CanRedo => !IsExecuting && _redoStack.TryPeek(out var entry) && (entry.RedoAction is not null || entry.RedoAsync is not null);

    /// <summary>
    /// Description of the next undo operation; <c>null</c> when nothing can be undone.
    /// <para>下一次撤销操作的描述文本；无可撤销项时为 <c>null</c>。</para>
    /// </summary>
    public string? NextUndoDescription => CanUndo ? _undoStack.Peek().Description : null;

    /// <summary>
    /// Description of the next redo operation; <c>null</c> when nothing can be redone.
    /// <para>下一次重做操作的描述文本；无可重做项时为 <c>null</c>。</para>
    /// </summary>
    public string? NextRedoDescription => CanRedo ? _redoStack.Peek().Description : null;

    /// <summary>
    /// Records an operation without a persistence descriptor.
    /// <para>登记一条不带持久化描述符的操作。</para>
    /// </summary>
    public void Record(string description, Action undo, Action redo) => Record(description, null, undo, redo);

    /// <summary>
    /// Records an operation with an optional persistence descriptor; pushes it onto the undo stack and
    /// clears the redo stack; drops the oldest entry when the capacity is exceeded.
    /// <para>登记一条操作（可带持久化描述符）：推入撤销栈并清空重做栈；超出容量时丢弃最旧条目。</para>
    /// </summary>
    /// <param name="description">Operation description. <para>操作描述。</para></param>
    /// <param name="operation">Optional serializable descriptor for cross-session reconstruction. <para>可选的可序列化描述符，用于跨会话重建。</para></param>
    /// <param name="undo">The undo action. <para>撤销动作。</para></param>
    /// <param name="redo">The redo action. <para>重做动作。</para></param>
    public void Record(string description, HistoryOperationDto? operation, Action undo, Action redo)
    {
        EnsureIdle();
        _undoStack.Push(new HistoryEntry(description, operation, undo, redo));
        _redoStack.Clear();
        if (_undoStack.Count > Capacity)
        {
            // 丢弃最旧的超额条目：弹出全部，保留最新 Capacity 条再按原顺序还原。
            // Drop the oldest excess entries: pop everything, keep the newest Capacity entries,
            // then restore them in order (buffer[0] is the newest, buffer[^1] the oldest).
            _trimBuffer.Clear();
            while (_undoStack.Count > 0)
            {
                _trimBuffer.Add(_undoStack.Pop());
            }

            var keep = Math.Min(_trimBuffer.Count, Capacity);
            for (var i = keep - 1; i >= 0; i--)
            {
                _undoStack.Push(_trimBuffer[i]);
            }

            _trimBuffer.Clear();
        }
    }

    /// <summary>
    /// Executes one undo; returns <c>false</c> when nothing can be undone.
    /// <para>执行一次撤销；无可撤销项时返回 <c>false</c>。</para>
    /// </summary>
    public bool Undo()
    {
        if (!CanUndo)
        {
            return false;
        }

        var entry = _undoStack.Peek();
        if (entry.UndoAction is null) throw new InvalidOperationException("Use UndoAsync for this history entry.");
        IsExecuting = true;
        try { entry.UndoAction(); } finally { IsExecuting = false; }
        _undoStack.Pop();
        _redoStack.Push(entry);
        return true;
    }

    /// <summary>
    /// Executes one redo; returns <c>false</c> when nothing can be redone.
    /// <para>执行一次重做；无可重做项时返回 <c>false</c>。</para>
    /// </summary>
    public bool Redo()
    {
        if (!CanRedo)
        {
            return false;
        }

        var entry = _redoStack.Peek();
        if (entry.RedoAction is null) throw new InvalidOperationException("Use RedoAsync for this history entry.");
        IsExecuting = true;
        try { entry.RedoAction(); } finally { IsExecuting = false; }
        _redoStack.Pop();
        _undoStack.Push(entry);
        return true;
    }

    /// <summary>
    /// Clears all history (both undo and redo stacks).
    /// <para>清空全部历史（撤销与重做栈）。</para>
    /// </summary>
    public void Clear()
    {
        EnsureIdle();
        _undoStack.Clear();
        _redoStack.Clear();
    }

    internal void CopyFrom(ProjectHistory history)
    {
        Clear();
        foreach (var entry in history._undoStack.Reverse())
        {
            if (entry.Operation is { } operation && (ReconstructOperation is not null || ReconstructAsyncOperation is not null))
                _undoStack.Push(Reconstruct(operation));
            else _undoStack.Push(entry);
        }
        foreach (var entry in history._redoStack.Reverse()) _redoStack.Push(entry);
    }

    /// <summary>
    /// Persists the undo stack's serializable operations to the specified path (newest first).
    /// <para>将撤销栈中可序列化的操作持久化到指定路径（最新在前）。</para>
    /// </summary>
    public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var operations = CaptureSnapshot();

        try
        {
            await AtomicJsonFile.WriteIfChangedAsync(path, operations, HistoryJsonContext.Default.ListHistoryOperationDto, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ProjectDiagnostics.Write(ProjectLogLevel.Error, "History", $"Save history failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>Captures serializable history without performing I/O or swallowing write errors.</summary>
    public List<HistoryOperationDto> CaptureSnapshot()
    {
        var operations = new List<HistoryOperationDto>();
        foreach (var entry in _undoStack)
        {
            if (entry.Operation is not null)
            {
                operations.Add(entry.Operation);
            }
        }

        return operations;
    }

    /// <summary>
    /// Reconstructs the undo stack from a persisted history file via <see cref="ReconstructOperation"/>.
    /// Missing files clear history. Without a reconstructor, descriptors are retained but undo/redo are unavailable.
    /// <para>经 <see cref="ReconstructOperation"/> 重建撤销栈。文件缺失时清空；无工厂时保留描述但不提供撤销/重做。</para>
    /// </summary>
    public async Task LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        EnsureIdle();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Clear();
            return;
        }

        List<HistoryOperationDto>? operations;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > 32 * 1024 * 1024) throw new InvalidDataException("Project state exceeds 32 MiB.");
            operations = await JsonSerializer.DeserializeAsync(stream, HistoryJsonContext.Default.ListHistoryOperationDto, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ProjectDiagnostics.Write(ProjectLogLevel.Error, "History", $"Load history failed: {ex.Message}");
            return;
        }

        EnsureIdle();
        _undoStack.Clear();
        _redoStack.Clear();
        if (operations is null || operations.Count == 0)
        {
            return;
        }

        _undoStack.Clear();
        _redoStack.Clear();
        // operations[0] 为最新；逆序重建使栈顶（Peek）为最新条目。
        // operations[0] is the newest; rebuild in reverse so the stack top (Peek) is the newest entry.
        for (var i = Math.Min(operations.Count, Capacity) - 1; i >= 0; i--)
        {
            var operation = operations[i];
            if (operation is null) continue;
            _undoStack.Push(Reconstruct(operation));
        }
    }

    /// <summary>
    /// History entry carrying the description, optional persistence descriptor and undo/redo actions.
    /// <para>历史条目，承载描述、可选持久化描述符与撤销/重做动作。</para>
    /// </summary>
    private sealed class HistoryEntry
    {
        internal Func<CancellationToken, Task>? UndoAsync { get; init; }
        internal Func<CancellationToken, Task>? RedoAsync { get; init; }
        /// <summary>
        /// Operation description.
        /// <para>操作描述。</para>
        /// </summary>
        public string Description { get; }

        /// <summary>
        /// Optional serializable descriptor.
        /// <para>可选的可序列化描述符。</para>
        /// </summary>
        public HistoryOperationDto? Operation { get; }

        /// <summary>
        /// The undo action.
        /// <para>撤销动作。</para>
        /// </summary>
        public Action? UndoAction { get; }

        /// <summary>
        /// The redo action.
        /// <para>重做动作。</para>
        /// </summary>
        public Action? RedoAction { get; }

        /// <summary>
        /// Creates a history entry.
        /// <para>创建历史条目。</para>
        /// </summary>
        public HistoryEntry(string description, HistoryOperationDto? operation, Action? undoAction, Action? redoAction)
        {
            Description = description;
            Operation = operation;
            UndoAction = undoAction;
            RedoAction = redoAction;
        }
    }
}
