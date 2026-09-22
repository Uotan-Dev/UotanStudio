using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using AvaloniaEdit;
using UotanStudio.Converters;
using UotanStudio.Services;
using UotanStudio.ViewModels.Workspace;
using UotanStudio.Views.Workspace;
using Xunit;

namespace UotanStudio.Tests.Views;

[Collection("Node editor UI")]
public sealed class WorkspaceLogPerformanceTests(NodeEditorUiFixture fixture)
{
    [Fact]
    public async Task WorkspaceLogView_BoundedBatches_RenderAndClearActualEditor()
    {
        await fixture.Session.Dispatch(() =>
        {
            Application.Current!.Resources["LogFilterDisplayConverter"] = new LogFilterDisplayConverter();
            using var model = new WorkspaceLogViewModel(false);
            var view = new WorkspaceLogView { DataContext = model };
            var window = new Window { Content = view, Width = 800, Height = 500 };
            window.Show();
            try
            {
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                model.Enqueue(Entry(123));
                model.FlushPending();
                window.UpdateLayout();
                var editor = view.FindControl<TextEditor>("LogEditor")!;
                Assert.Contains("Firmware - 123", editor.Text);
                model.Clear();
                Assert.Equal(string.Empty, editor.Text);
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FlushPending_Burst_RetainsNewestBoundedHistoryInSmallBatches()
    {
        await fixture.Session.Dispatch(() =>
        {
            using var model = new WorkspaceLogViewModel(false);
            for (var i = 0; i < 100_000; i++)
            {
                model.Enqueue(Entry(i));
            }
            model.FlushPending();
            Assert.Equal(250, model.Entries.Count);
            Assert.Equal("95000", model.Entries[0].Message);
            for (var i = 0; i < 25; i++)
            {
                model.FlushPending();
            }
            Assert.Equal(5000, model.Entries.Count);
            Assert.Equal("99999", model.Entries[^1].Message);
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FlushPending_FilteredEviction_RemovesOnlyVisibleExpiredRows()
    {
        await fixture.Session.Dispatch(() =>
        {
            using var model = new WorkspaceLogViewModel(false) { LevelFilter = LogLevel.Error };
            for (var i = 0; i < 5000; i++)
            {
                model.Enqueue(Entry(i, i == 4999 ? LogLevel.Error : LogLevel.Info));
                if (i % 250 == 249)
                {
                    model.FlushPending();
                }
            }
            model.Enqueue(Entry(5000));
            model.FlushPending();
            Assert.Equal("4999", Assert.Single(model.Entries).Message);
            model.LevelFilter = null;
            Assert.Equal(5000, model.Entries.Count);
            Assert.Equal("1", model.Entries[0].Message);
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FlushPending_LongMessages_BoundsHistoryAndSources()
    {
        await fixture.Session.Dispatch(() =>
        {
            using var model = new WorkspaceLogViewModel(false);
            for (var i = 0; i < 100; i++)
            {
                model.Enqueue(new(DateTime.UnixEpoch, LogLevel.Info, $"Firmware{i}", new string('x', 100_000)));
                model.FlushPending();
            }
            Assert.InRange(model.Entries.Sum(e => (long)e.Text.Length), 1, WorkspaceLogViewModel.MaximumCharacters);
            Assert.Equal(model.Entries.Count + 1, model.Sources.Count);
            Assert.DoesNotContain("Firmware0", model.Sources);
            Assert.Equal("Firmware99", model.Entries[^1].Source);
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Clear_PendingEntries_DoesNotRepopulateOnNextFlush()
    {
        await fixture.Session.Dispatch(() =>
        {
            using var model = new WorkspaceLogViewModel(false);
            model.Enqueue(Entry(1));
            model.Clear();
            model.FlushPending();
            Assert.Empty(model.Entries);
            Assert.Equal(string.Empty, Assert.Single(model.Sources));
            model.Dispose();
            model.Enqueue(Entry(2));
            model.FlushPending();
            Assert.Empty(model.Entries);
        }, TestContext.Current.CancellationToken);
    }

    private static LogEntry Entry(int number, LogLevel level = LogLevel.Info)
        => new(DateTime.UnixEpoch, level, "Firmware", number.ToString());

    [Fact]
    public void Write_SubscriptionChangesDuringDispatch_PreservesSnapshotAndExceptionIsolation()
    {
        var calls = new List<string>();
        var service = LogService.Instance;
        Action? removeSecond = null;
        Action? removeThird = null;
        var removeFirst = service.Subscribe((_, _) =>
        {
            calls.Add("first");
            removeSecond?.Invoke();
            removeThird ??= service.Subscribe((_, _) => calls.Add("third"));
        });
        removeSecond = service.Subscribe((_, _) =>
        {
            calls.Add("second");
            throw new InvalidOperationException("Subscriber failure");
        });
        try
        {
            service.Write(LogLevel.Fatal, "PerformanceTest", "one");
            service.Write(LogLevel.Fatal, "PerformanceTest", "two");
            Assert.Equal(new[] { "first", "second", "first", "third" }, calls);
        }
        finally
        {
            removeFirst();
            removeSecond();
            removeThird?.Invoke();
        }
    }
}