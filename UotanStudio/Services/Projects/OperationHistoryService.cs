using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

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
/// Operation history service (singleton), maintaining in-memory undo/redo stacks in command pattern.
/// Entries recorded with a <see cref="HistoryOperationDto"/> can be persisted to a history file and
/// reconstructed across sessions via <see cref="ReconstructOperation"/>.
/// <para>操作历史服务（单例），以命令模式维护内存撤销/重做栈。带 <see cref="HistoryOperationDto"/>
/// 登记的条目可持久化到历史文件，并经 <see cref="ReconstructOperation"/> 跨会话重建。</para>
/// </summary>
public sealed class OperationHistoryService
{
    /// <summary>
    /// Maximum history capacity; the oldest undo entry is dropped when exceeded.
    /// <para>历史容量上限，超出后丢弃最旧的撤销条目。</para>
    /// </summary>
    public const int Capacity = 50;

    private static readonly Lazy<OperationHistoryService> LazyInstance = new(() => new OperationHistoryService());

    /// <summary>
    /// Gets the operation history singleton instance.
    /// <para>获取操作历史单例实例。</para>
    /// </summary>
    public static OperationHistoryService Instance => LazyInstance.Value;

    /// <summary>
    /// Factory used to reconstruct undo/redo actions from a persisted operation; registered by the workspace
    /// (e.g. the image panel). <c>null</c> disables history reconstruction.
    /// <para>从持久化操作重建撤销/重做动作的工厂，由工作区（如镜像面板）注册；为 <c>null</c> 时不重建历史。</para>
    /// </summary>
    public static Func<HistoryOperationDto, (Action Undo, Action Redo)>? ReconstructOperation { get; set; }

    private readonly Stack<HistoryEntry> _undoStack = new();
    private readonly Stack<HistoryEntry> _redoStack = new();

    private OperationHistoryService()
    {
    }

    /// <summary>
    /// Whether an undo operation can be executed.
    /// <para>是否可以执行撤销。</para>
    /// </summary>
    public bool CanUndo => _undoStack.Count > 0;

    /// <summary>
    /// Whether a redo operation can be executed.
    /// <para>是否可以执行重做。</para>
    /// </summary>
    public bool CanRedo => _redoStack.Count > 0;

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
        _undoStack.Push(new HistoryEntry(description, operation, undo, redo));
        _redoStack.Clear();
        if (_undoStack.Count > Capacity)
        {
            // 丢弃最旧的超额条目：弹出全部，保留最新 Capacity 条再按原顺序还原。
            // Drop the oldest excess entries: pop everything, keep the newest Capacity entries,
            // then restore them in order (all[0] is the newest, all[^1] the oldest).
            var all = new List<HistoryEntry>(_undoStack.Count);
            while (_undoStack.Count > 0)
            {
                all.Add(_undoStack.Pop());
            }

            var keep = Math.Min(all.Count, Capacity);
            for (var i = keep - 1; i >= 0; i--)
            {
                _undoStack.Push(all[i]);
            }
        }
    }

    /// <summary>
    /// Executes one undo; returns <c>false</c> when nothing can be undone.
    /// <para>执行一次撤销；无可撤销项时返回 <c>false</c>。</para>
    /// </summary>
    public bool Undo()
    {
        if (_undoStack.Count == 0)
        {
            return false;
        }

        var entry = _undoStack.Pop();
        entry.UndoAction();
        _redoStack.Push(entry);
        return true;
    }

    /// <summary>
    /// Executes one redo; returns <c>false</c> when nothing can be redone.
    /// <para>执行一次重做；无可重做项时返回 <c>false</c>。</para>
    /// </summary>
    public bool Redo()
    {
        if (_redoStack.Count == 0)
        {
            return false;
        }

        var entry = _redoStack.Pop();
        entry.RedoAction();
        _undoStack.Push(entry);
        return true;
    }

    /// <summary>
    /// Clears all history (both undo and redo stacks).
    /// <para>清空全部历史（撤销与重做栈）。</para>
    /// </summary>
    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
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

        var operations = new List<HistoryOperationDto>();
        foreach (var entry in _undoStack)
        {
            if (entry.Operation is not null)
            {
                operations.Add(entry.Operation);
            }
        }

        try
        {
            await using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(stream, operations, HistoryJsonContext.Default.ListHistoryOperationDto, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "History", $"Save history failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Reconstructs the undo stack from a persisted history file via <see cref="ReconstructOperation"/>.
    /// No-op when the file is missing or the reconstructor is not registered.
    /// <para>经 <see cref="ReconstructOperation"/> 从持久化历史文件重建撤销栈。文件缺失或未注册重建工厂时无操作。</para>
    /// </summary>
    public async Task LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (ReconstructOperation is null || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        List<HistoryOperationDto>? operations;
        try
        {
            await using var stream = File.OpenRead(path);
            operations = await JsonSerializer.DeserializeAsync(stream, HistoryJsonContext.Default.ListHistoryOperationDto, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, "History", $"Load history failed: {ex.Message}");
            return;
        }

        if (operations is null || operations.Count == 0)
        {
            return;
        }

        _undoStack.Clear();
        _redoStack.Clear();
        // operations[0] 为最新；逆序重建使栈顶（Peek）为最新条目。
        // operations[0] is the newest; rebuild in reverse so the stack top (Peek) is the newest entry.
        for (var i = operations.Count - 1; i >= 0; i--)
        {
            var operation = operations[i];
            var (undo, redo) = ReconstructOperation(operation);
            _undoStack.Push(new HistoryEntry(operation.Description, operation, undo, redo));
        }
    }

    /// <summary>
    /// History entry carrying the description, optional persistence descriptor and undo/redo actions.
    /// <para>历史条目，承载描述、可选持久化描述符与撤销/重做动作。</para>
    /// </summary>
    private sealed class HistoryEntry
    {
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
        public Action UndoAction { get; }

        /// <summary>
        /// The redo action.
        /// <para>重做动作。</para>
        /// </summary>
        public Action RedoAction { get; }

        /// <summary>
        /// Creates a history entry.
        /// <para>创建历史条目。</para>
        /// </summary>
        public HistoryEntry(string description, HistoryOperationDto? operation, Action undoAction, Action redoAction)
        {
            Description = description;
            Operation = operation;
            UndoAction = undoAction;
            RedoAction = redoAction;
        }
    }
}