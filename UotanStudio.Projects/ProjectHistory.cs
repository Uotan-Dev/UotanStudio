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

    /// <summary><para>打包 Brotli。</para>Pack a Brotli archive.</summary>
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
    /// <value>Read/write enum serialized as its name; <c>ImportImages</c> by default.
    /// <para>可读写枚举，按名称序列化；默认为 <c>ImportImages</c>。</para></value>
    public HistoryOperationKind Kind { get; set; }

    /// <summary>
    /// The operation description shown in undo/redo menus.
    /// <para>撤销/重做菜单中显示的操作描述。</para>
    /// </summary>
    /// <value>Read/write text; empty by default. <para>可读写文本；默认为空。</para></value>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// The display name of the affected image.
    /// <para>受影响镜像的显示名称。</para>
    /// </summary>
    /// <value>Read/write text; empty by default. <para>可读写文本；默认为空。</para></value>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// The number of affected items (e.g. imported images).
    /// <para>受影响项的数量（如导入的镜像数）。</para>
    /// </summary>
    /// <value>Read/write count; <c>0</c> by default. <para>可读写数量；默认为 <c>0</c>。</para></value>
    public int Count { get; set; }

    /// <summary>
    /// Project-relative paths of the affected items (e.g. imported images).
    /// <para>受影响项的项目相对路径（如导入的镜像）。</para>
    /// </summary>
    /// <value>Read/write list; empty by default. <para>可读写列表；默认为空。</para></value>
    public List<string> RelativePaths { get; set; } = [];

    /// <summary>
    /// Project-relative path of a single affected item (e.g. the deleted image).
    /// <para>单个受影响项的项目相对路径（如被删除的镜像）。</para>
    /// </summary>
    /// <value>Read/write text; empty by default. <para>可读写文本；默认为空。</para></value>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>
    /// Absolute destination path (e.g. where a deleted image was moved).
    /// <para>目标绝对路径（如被删除镜像的移动位置）。</para>
    /// </summary>
    /// <value>Read/write text; empty by default. <para>可读写文本；默认为空。</para></value>
    public string DestinationPath { get; set; } = string.Empty;

    /// <summary>
    /// Output file/directory paths produced by the operation (deleted on undo).
    /// <para>操作产出的输出文件/目录路径（撤销时删除）。</para>
    /// </summary>
    /// <value>Read/write list; empty by default. <para>可读写列表；默认为空。</para></value>
    public List<string> OutputPaths { get; set; } = [];

    /// <summary>
    /// Project-relative paths used to stage outputs while an operation is undone.
    /// <para>撤销操作期间用于暂存产物的项目相对路径。</para>
    /// </summary>
    /// <value>Read/write list; empty by default. <para>可读写列表；默认为空。</para></value>
    public List<string> BackupPaths { get; set; } = [];
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
    /// <value>Read/write factory, <see langword="null"/> by default.
    /// <para>可读写工厂，默认为 <see langword="null"/>。</para></value>
    public Func<HistoryOperationDto, (Action Undo, Action Redo)>? ReconstructOperation { get; set; }

    /// <summary>
    /// True while an undo/redo callback is running. Use this object on its owning synchronization context.
    /// <para>撤销/重做回调执行期间为真。请在其所属同步上下文上使用本对象。</para>
    /// </summary>
    /// <value>Set by this class around callbacks and cleared in a finally block; not writable from callers.
    /// <para>由本类在回调前后设置，并在 finally 块中清除；调用方不可写入。</para></value>
    public bool IsExecuting { get; private set; }

    private void EnsureIdle()
    {
        if (IsExecuting) throw new InvalidOperationException("A history action is running.");
    }

    /// <summary>
    /// Records cancellable asynchronous actions. Failed/cancelled execution retains the original stack entry.
    /// <para>登记可取消的异步操作。执行失败或取消时保留原栈内条目。</para>
    /// </summary>
    /// <param name="description">Operation description. <para>操作描述。</para></param>
    /// <param name="operation">Optional serializable descriptor for cross-session reconstruction. <para>可选的可序列化描述符，用于跨会话重建。</para></param>
    /// <param name="undo">Async undo callback. <para>异步撤销回调。</para></param>
    /// <param name="redo">Async redo callback. <para>异步重做回调。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="undo"/> or <paramref name="redo"/> is <see langword="null"/>. <para><paramref name="undo"/> 或 <paramref name="redo"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidOperationException">Another history action is running. <para>另一个历史动作正在执行。</para></exception>
    public void RecordAsync(string description, HistoryOperationDto? operation,
        Func<CancellationToken, Task> undo, Func<CancellationToken, Task> redo)
    {
        ArgumentNullException.ThrowIfNull(undo);
        ArgumentNullException.ThrowIfNull(redo);
        Record(description, operation, () => { }, () => { });
        var entry = _undoStack.Pop();
        _undoStack.Push(new(entry.Description, entry.Operation, null, null) { UndoAsync = undo, RedoAsync = redo });
    }

    /// <summary>
    /// Awaits undo before moving the entry to the redo stack.
    /// <para>先等待撤销完成，再把条目移入重做栈。</para>
    /// </summary>
    /// <param name="cancellationToken">
    /// Checked before starting and passed to the callback; a cancelled or failing callback leaves the
    /// entry on the undo stack.
    /// <para>开始前检查并传给回调；回调被取消或抛出时条目留在撤销栈上。</para>
    /// </param>
    /// <returns><see langword="true"/> when the entry was undone; <see langword="false"/> when nothing can be undone.
    /// <para>撤销成功返回 <see langword="true"/>；无可撤销项返回 <see langword="false"/>。</para></returns>
    /// <exception cref="InvalidOperationException">Another history action is running. <para>另一个历史动作正在执行。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested. <para>请求了取消。</para></exception>
    public Task<bool> UndoAsync(CancellationToken cancellationToken = default) => ExecuteAsync(true, cancellationToken);
    /// <summary>
    /// Awaits redo before moving the entry to the undo stack.
    /// <para>先等待重做完成，再把条目移入撤销栈。</para>
    /// </summary>
    /// <param name="cancellationToken">
    /// Checked before starting and passed to the callback; a cancelled or failing callback leaves the
    /// entry on the redo stack.
    /// <para>开始前检查并传给回调；回调被取消或抛出时条目留在重做栈上。</para>
    /// </param>
    /// <returns><see langword="true"/> when the entry was redone; <see langword="false"/> when nothing can be redone.
    /// <para>重做成功返回 <see langword="true"/>；无可重做项返回 <see langword="false"/>。</para></returns>
    /// <exception cref="InvalidOperationException">Another history action is running. <para>另一个历史动作正在执行。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested. <para>请求了取消。</para></exception>
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

    /// <summary>
    /// Optional asynchronous action factory; takes precedence over the synchronous factory when set.
    /// <para>可选的异步动作工厂；设置后优先于同步工厂。</para>
    /// </summary>
    /// <value>Read/write factory, <see langword="null"/> by default.
    /// <para>可读写工厂，默认为 <see langword="null"/>。</para></value>
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

    /// <summary>
    /// Creates independent history with no default executable actions.
    /// <para>创建独立的历史记录，不提供默认的可执行动作。</para>
    /// </summary>
    public ProjectHistory()
    {
    }

    /// <summary>
    /// Whether an undo operation can be executed.
    /// <para>是否可以执行撤销。</para>
    /// </summary>
    /// <value><see langword="true"/> when no callback is running and the undo top has an executable
    /// action; <see langword="false"/> otherwise.
    /// <para>无回调运行且撤销栈顶具有可执行动作时为 <see langword="true"/>；否则为 <see langword="false"/>。</para></value>
    public bool CanUndo => !IsExecuting && _undoStack.TryPeek(out var entry) && (entry.UndoAction is not null || entry.UndoAsync is not null);

    /// <summary>
    /// Whether a redo operation can be executed.
    /// <para>是否可以执行重做。</para>
    /// </summary>
    /// <value><see langword="true"/> when no callback is running and the redo top has an executable
    /// action; <see langword="false"/> otherwise.
    /// <para>无回调运行且重做栈顶具有可执行动作时为 <see langword="true"/>；否则为 <see langword="false"/>。</para></value>
    public bool CanRedo => !IsExecuting && _redoStack.TryPeek(out var entry) && (entry.RedoAction is not null || entry.RedoAsync is not null);

    /// <summary>
    /// Description of the next undo operation; <c>null</c> when nothing can be undone.
    /// <para>下一次撤销操作的描述文本；无可撤销项时为 <c>null</c>。</para>
    /// </summary>
    /// <value>The top undo entry's description when <see cref="CanUndo"/> holds; otherwise <see langword="null"/>.
    /// <para><see cref="CanUndo"/> 成立时为撤销栈顶条目的描述；否则为 <see langword="null"/>。</para></value>
    public string? NextUndoDescription => CanUndo ? _undoStack.Peek().Description : null;

    /// <summary>
    /// Description of the next redo operation; <c>null</c> when nothing can be redone.
    /// <para>下一次重做操作的描述文本；无可重做项时为 <c>null</c>。</para>
    /// </summary>
    /// <value>The top redo entry's description when <see cref="CanRedo"/> holds; otherwise <see langword="null"/>.
    /// <para><see cref="CanRedo"/> 成立时为重做栈顶条目的描述；否则为 <see langword="null"/>。</para></value>
    public string? NextRedoDescription => CanRedo ? _redoStack.Peek().Description : null;

    /// <summary>
    /// Records an operation without a persistence descriptor.
    /// <para>登记一条不带持久化描述符的操作。</para>
    /// </summary>
    /// <param name="description">Operation description. <para>操作描述。</para></param>
    /// <param name="undo">The undo action. <para>撤销动作。</para></param>
    /// <param name="redo">The redo action. <para>重做动作。</para></param>
    /// <exception cref="InvalidOperationException">Another history action is running. <para>另一个历史动作正在执行。</para></exception>
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
    /// <returns><see langword="true"/> when the entry was undone and moved to the redo stack; a throwing
    /// action leaves the entry on the undo stack.
    /// <para>撤销成功并移入重做栈时返回 <see langword="true"/>；动作抛出异常时条目留在撤销栈上。</para></returns>
    /// <exception cref="InvalidOperationException">The top entry only has an asynchronous action; use
    /// <see cref="UndoAsync"/> instead. <para>栈顶条目仅有异步动作；请改用 <see cref="UndoAsync"/>。</para></exception>
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
    /// <returns><see langword="true"/> when the entry was redone and moved to the undo stack; a throwing
    /// action leaves the entry on the redo stack.
    /// <para>重做成功并移入撤销栈时返回 <see langword="true"/>；动作抛出异常时条目留在重做栈上。</para></returns>
    /// <exception cref="InvalidOperationException">The top entry only has an asynchronous action; use
    /// <see cref="RedoAsync"/> instead. <para>栈顶条目仅有异步动作；请改用 <see cref="RedoAsync"/>。</para></exception>
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
    /// <param name="path">Destination history file path; a blank value skips all I/O. <para>目标历史文件路径；空白值会跳过全部 I/O。</para></param>
    /// <param name="cancellationToken">Cancels the staged write before it commits. <para>在暂存写入提交前取消。</para></param>
    /// <returns>
    /// Completes once the staged file has been committed or the existing file was found identical.
    /// <para>暂存文件提交完成，或发现现有文件内容一致时结束。</para>
    /// </returns>
    /// <exception cref="OperationCanceledException">Cancellation was requested. <para>请求了取消。</para></exception>
    /// <exception cref="Exception">
    /// A write failure is reported through <see cref="ProjectDiagnostics"/> and rethrown; the previous
    /// file content survives.
    /// <para>写入失败会经 <see cref="ProjectDiagnostics"/> 记录后重新抛出；原有文件内容保持不变。</para>
    /// </exception>
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

    /// <summary>
    /// Captures serializable history without performing I/O or swallowing write errors.
    /// <para>捕获可序列化历史，不执行 I/O，也不吞掉写入错误。</para>
    /// </summary>
    /// <returns>
    /// A new newest-first list of the descriptors attached to undo-stack entries; entries without a
    /// descriptor are skipped and the list shares the descriptor instances with the live stack.
    /// <para>由撤销栈条目所附描述符组成的新列表（最新在前）；无描述符的条目被跳过，列表与活动栈共享描述符实例。</para>
    /// </returns>
    public List<HistoryOperationDto> CaptureSnapshot()
    {
        List<HistoryOperationDto> operations = new(_undoStack.Count);
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
    /// Copies a history snapshot for Save As and rebases legacy absolute paths into the destination root.
    /// Relative paths are already portable and are retained unchanged.
    /// </summary>
    internal static List<HistoryOperationDto> RebaseSnapshot(
        IReadOnlyList<HistoryOperationDto> operations, string sourceRoot, string destinationRoot)
    {
        var source = Path.GetFullPath(sourceRoot);
        var destination = Path.GetFullPath(destinationRoot);
        return [.. operations.Select(operation => new HistoryOperationDto
        {
            Kind = operation.Kind,
            Description = operation.Description,
            DisplayName = operation.DisplayName,
            Count = operation.Count,
            RelativePath = RebasePath(operation.RelativePath, source, destination),
            DestinationPath = RebasePath(operation.DestinationPath, source, destination),
            RelativePaths = [.. operation.RelativePaths.Select(path => RebasePath(path, source, destination))],
            OutputPaths = [.. operation.OutputPaths.Select(path => RebasePath(path, source, destination))],
            BackupPaths = [.. operation.BackupPaths.Select(path => RebasePath(path, source, destination))]
        })];
    }

    private static string RebasePath(string path, string sourceRoot, string destinationRoot)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return path;
        var full = Path.GetFullPath(path);
        var prefix = Path.TrimEndingDirectorySeparator(sourceRoot) + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            ? Path.GetRelativePath(destinationRoot, Path.Combine(destinationRoot, Path.GetRelativePath(sourceRoot, full)))
                .Replace(Path.DirectorySeparatorChar, '/')
            : path;
    }

    /// <summary>
    /// Reconstructs the undo stack from a persisted history file via <see cref="ReconstructOperation"/>.
    /// Missing files clear history. Without a reconstructor, descriptors are retained but undo/redo are unavailable.
    /// <para>经 <see cref="ReconstructOperation"/> 重建撤销栈。文件缺失时清空；无工厂时保留描述但不提供撤销/重做。</para>
    /// </summary>
    /// <param name="path">History file path; blank or missing files clear history. <para>历史文件路径；为空白或文件缺失时清空历史。</para></param>
    /// <param name="cancellationToken">Cancels the read in progress. <para>取消进行中的读取。</para></param>
    /// <returns>
    /// Completes once the stacks have been rebuilt, cleared, or left untouched after a logged failure;
    /// at most the newest <see cref="Capacity"/> entries are kept and the redo stack ends empty.
    /// <para>栈重建完成、清空，或在记录失败后保持原样时结束；至多保留最新 <see cref="Capacity"/> 条，重做栈最终为空。</para>
    /// </returns>
    /// <exception cref="InvalidOperationException">Another history action is running. <para>另一个历史动作正在执行。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested. <para>请求了取消。</para></exception>
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
        /// <param name="description">Operation description shown in the history list. <para>历史列表中显示的操作描述。</para></param>
        /// <param name="operation">
        /// Optional serializable descriptor; entries without one stay in memory and are not written to
        /// the history file.
        /// <para>可选的可序列化描述符；缺少它的条目只留在内存中，不会写入历史文件。</para>
        /// </param>
        /// <param name="undoAction">
        /// Synchronous undo callback; <see langword="null"/> when the entry only carries an asynchronous one.
        /// <para>同步撤销回调；条目只有异步回调时为 <see langword="null"/>。</para>
        /// </param>
        /// <param name="redoAction">
        /// Synchronous redo callback; <see langword="null"/> when the entry only carries an asynchronous one.
        /// <para>同步重做回调；条目只有异步回调时为 <see langword="null"/>。</para>
        /// </param>
        public HistoryEntry(string description, HistoryOperationDto? operation, Action? undoAction, Action? redoAction)
        {
            Description = description;
            Operation = operation;
            UndoAction = undoAction;
            RedoAction = redoAction;
        }
    }
}