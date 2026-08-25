using UotanStudio.Services;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Operation history service tests: verify undo/redo stack semantics, redo clearing on record, and
/// capacity trimming.
/// <para>操作历史服务测试：验证撤销/重做栈语义、记录时清空重做栈与容量裁剪。</para>
/// </summary>
public sealed class OperationHistoryTests
{
    [Fact]
    public void RecordUndoRedo_FollowsStackSemantics()
    {
        History.Clear();
        var log = new List<string>();
        History.Record("A", () => log.Add("undo-A"), () => log.Add("redo-A"));
        History.Record("B", () => log.Add("undo-B"), () => log.Add("redo-B"));

        Assert.True(History.CanUndo);
        Assert.False(History.CanRedo);
        Assert.Equal("B", History.NextUndoDescription);

        Assert.True(History.Undo());
        Assert.Equal("undo-B", Assert.Single(log));
        Assert.True(History.CanRedo);
        Assert.Equal("B", History.NextRedoDescription);
        Assert.Equal("A", History.NextUndoDescription);

        Assert.True(History.Redo());
        Assert.Equal(["undo-B", "redo-B"], log);

        Assert.True(History.Undo());
        Assert.True(History.Undo());
        Assert.False(History.Undo());
        Assert.True(History.CanRedo);
        Assert.Equal(["undo-B", "redo-B", "undo-B", "undo-A"], log);
    }

    [Fact]
    public void Record_ClearsRedoStack()
    {
        History.Clear();
        var log = new List<string>();
        History.Record("A", () => log.Add("uA"), () => log.Add("rA"));
        Assert.True(History.Undo());
        Assert.True(History.CanRedo);

        History.Record("B", () => log.Add("uB"), () => log.Add("rB"));
        Assert.False(History.CanRedo);
        Assert.False(History.Redo());
    }

    [Fact]
    public void Record_TrimsOldestBeyondCapacity()
    {
        History.Clear();
        var log = new List<string>();
        for (var i = 0; i < OperationHistoryService.Capacity + 5; i++)
        {
            var index = i;
            History.Record($"op{index}", () => log.Add($"u{index}"), () => log.Add($"r{index}"));
        }

        // 最旧的 5 条被裁剪，撤销栈顶应为最后一次记录。
        // The 5 oldest entries are trimmed; the undo peek should be the last recorded op.
        Assert.Equal($"op{OperationHistoryService.Capacity + 4}", History.NextUndoDescription);

        var undone = 0;
        while (History.Undo())
        {
            undone++;
        }

        Assert.Equal(OperationHistoryService.Capacity, undone);
    }

    [Fact]
    public void Clear_ResetsBothStacks()
    {
        History.Clear();
        History.Record("A", () => { }, () => { });
        History.Undo();
        Assert.True(History.CanRedo);

        History.Clear();
        Assert.False(History.CanUndo);
        Assert.False(History.CanRedo);
        Assert.Null(History.NextUndoDescription);
        Assert.Null(History.NextRedoDescription);
    }

    private static OperationHistoryService History => OperationHistoryService.Instance;
}