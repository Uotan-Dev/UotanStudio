using System;
using System.Collections.Generic;

namespace UotanStudio.Services;

/// <summary>
/// <para>操作历史服务（单例），以命令模式维护内存撤销/重做栈：每次工作区操作通过
/// <see cref="Record"/> 登记撤销与重做动作，供后续撤销/重做功能消费。历史不持久化，会话结束即清空。</para>
/// Operation history service (singleton), maintaining in-memory undo/redo stacks in command pattern:
/// every workspace operation registers undo and redo actions via <see cref="Record"/> for later
/// undo/redo consumption. History is not persisted and is cleared when the session ends.
/// </summary>
public sealed class OperationHistoryService
{
    /// <summary>
    /// <para>历史容量上限，超出后丢弃最旧的撤销条目。</para>
    /// Maximum history capacity; the oldest undo entry is dropped when exceeded.
    /// </summary>
    public const int Capacity = 50;

    private static readonly Lazy<OperationHistoryService> LazyInstance = new(() => new OperationHistoryService());

    /// <summary>
    /// <para>获取操作历史单例实例。</para>
    /// Gets the singleton instance of the operation history service.
    /// </summary>
    public static OperationHistoryService Instance => LazyInstance.Value;

    private readonly Stack<HistoryEntry> _undoStack = new();
    private readonly Stack<HistoryEntry> _redoStack = new();

    private OperationHistoryService()
    {
    }

    /// <summary>
    /// <para>是否可以执行撤销。</para>
    /// Whether an undo operation can be executed.
    /// </summary>
    public bool CanUndo => _undoStack.Count > 0;

    /// <summary>
    /// <para>是否可以执行重做。</para>
    /// Whether a redo operation can be executed.
    /// </summary>
    public bool CanRedo => _redoStack.Count > 0;

    /// <summary>
    /// <para>下一次撤销操作的描述文本；无可撤销项时为 <c>null</c>。</para>
    /// Description of the next undo operation; <c>null</c> when nothing can be undone.
    /// </summary>
    public string? NextUndoDescription => CanUndo ? _undoStack.Peek().Description : null;

    /// <summary>
    /// <para>下一次重做操作的描述文本；无可重做项时为 <c>null</c>。</para>
    /// Description of the next redo operation; <c>null</c> when nothing can be redone.
    /// </summary>
    public string? NextRedoDescription => CanRedo ? _redoStack.Peek().Description : null;

    /// <summary>
    /// <para>登记一条操作历史：推入撤销栈并清空重做栈；超出容量时丢弃最旧条目。</para>
    /// Records an operation: pushes it onto the undo stack and clears the redo stack;
    /// drops the oldest entry when the capacity is exceeded.
    /// </summary>
    /// <param name="description">
    /// <para>操作描述（用于撤销/重做菜单显示）。</para>
    /// Operation description (shown in undo/redo menus).
    /// </param>
    /// <param name="undo">
    /// <para>撤销动作。</para>
    /// The undo action.
    /// </param>
    /// <param name="redo">
    /// <para>重做动作。</para>
    /// The redo action.
    /// </param>
    public void Record(string description, Action undo, Action redo)
    {
        _undoStack.Push(new HistoryEntry(description, undo, redo));
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
    /// <para>执行一次撤销；无可撤销项时返回 <c>false</c>。</para>
    /// Executes one undo; returns <c>false</c> when nothing can be undone.
    /// </summary>
    /// <returns>
    /// <para>是否实际执行了撤销。</para>
    /// Whether an undo was actually executed.
    /// </returns>
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
    /// <para>执行一次重做；无可重做项时返回 <c>false</c>。</para>
    /// Executes one redo; returns <c>false</c> when nothing can be redone.
    /// </summary>
    /// <returns>
    /// <para>是否实际执行了重做。</para>
    /// Whether a redo was actually executed.
    /// </returns>
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
    /// <para>清空全部历史（撤销与重做栈）。</para>
    /// Clears all history (both undo and redo stacks).
    /// </summary>
    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
    }

    /// <summary>
    /// <para>历史条目，承载描述与撤销/重做动作。</para>
    /// History entry carrying the description and undo/redo actions.
    /// </summary>
    private sealed class HistoryEntry
    {
        /// <summary>
        /// <para>操作描述。</para>
        /// Operation description.
        /// </summary>
        public string Description { get; }

        /// <summary>
        /// <para>撤销动作。</para>
        /// The undo action.
        /// </summary>
        public Action UndoAction { get; }

        /// <summary>
        /// <para>重做动作。</para>
        /// The redo action.
        /// </summary>
        public Action RedoAction { get; }

        /// <summary>
        /// <para>创建历史条目。</para>
        /// Creates a history entry.
        /// </summary>
        public HistoryEntry(string description, Action undoAction, Action redoAction)
        {
            Description = description;
            UndoAction = undoAction;
            RedoAction = redoAction;
        }
    }
}