using System.Text;
using UotanStudio.ViewModels.Shell;
using Xunit;

namespace UotanStudio.Tests.Views;

[Collection("Node editor UI")]
public sealed class FlashViewLogBatchingTests(NodeEditorUiFixture fixture)
{
    private static string Joined(params string[] lines)
    {
        var text = new StringBuilder();
        foreach (var line in lines)
        {
            text.Append(line).Append(Environment.NewLine);
        }

        return text.ToString();
    }

    private static string[] PublishedLines(string text)
        => text.Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries);

    private Task OnUiThread(Func<Task> action, CancellationToken token)
    {
        Task dispatch;
        using (ExecutionContext.SuppressFlow())
        {
            dispatch = fixture.DispatchAsync(action, token);
        }

        return dispatch.WaitAsync(TimeSpan.FromSeconds(20), token);
    }

    [Fact]
    public async Task LogText_Initially_EmptyWithoutNotifications()
    {
        await OnUiThread(() =>
        {
            var model = new FlashViewModel();
            var notifications = 0;
            model.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(FlashViewModel.LogText))
                {
                    notifications++;
                }
            };

            Assert.Equal(string.Empty, model.LogText);
            model.FlushPendingLog();
            Assert.Equal(string.Empty, model.LogText);
            Assert.Equal(0, notifications);
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AppendLog_BelowThreshold_PublishesNothingUntilFlush()
    {
        await OnUiThread(() =>
        {
            var model = new FlashViewModel();
            var notifications = 0;
            model.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(FlashViewModel.LogText))
                {
                    notifications++;
                }
            };

            for (var index = 0; index < 5; index++)
            {
                model.AppendLog($"pending {index}");
            }

            Assert.Equal(string.Empty, model.LogText);
            Assert.Equal(0, notifications);

            model.FlushPendingLog();
            Assert.Equal(1, notifications);
            Assert.Equal(Joined("pending 0", "pending 1", "pending 2", "pending 3", "pending 4"), model.LogText);
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AppendLog_TenThousandLines_PublishesFarFewerNotificationsThanLines()
    {
        await OnUiThread(() =>
        {
            var model = new FlashViewModel();
            var notifications = 0;
            model.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(FlashViewModel.LogText))
                {
                    notifications++;
                }
            };

            for (var index = 0; index < 10_000; index++)
            {
                model.AppendLog($"row {index}");
            }

            model.FlushPendingLog();
            var lines = PublishedLines(model.LogText);
            Assert.Equal(10_000, lines.Length);
            Assert.Equal("row 9999", lines[^1]);
            Assert.Equal("row 0", lines[0]);
            Assert.InRange(notifications, 1, 400);
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AppendLog_BeyondRetainedWindow_KeepsTailAndDropsOnlyPrefix()
    {
        await OnUiThread(() =>
        {
            var model = new FlashViewModel();
            var expected = new StringBuilder();
            for (var index = 0; index < 5_000; index++)
            {
                var line = $"row {index} · {new string('x', 60)}";
                model.AppendLog(line);
                expected.Append(line).Append(Environment.NewLine);
            }

            model.FlushPendingLog();
            var full = expected.ToString();
            Assert.True(full.Length > 200_000, $"built {full.Length} characters");
            Assert.EndsWith(model.LogText, full, StringComparison.Ordinal);
            Assert.InRange(model.LogText.Length, 190_000, 200_000);
            Assert.DoesNotContain("row 0 ·", model.LogText, StringComparison.Ordinal);
            Assert.Contains("row 4999 ·", model.LogText, StringComparison.Ordinal);
            Assert.Equal(PublishedLines(model.LogText)[^1], "row 4999 · " + new string('x', 60));
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ClearLog_DropsPendingLinesSoTheNextPublishStartsFromEmpty()
    {
        await OnUiThread(() =>
        {
            var model = new FlashViewModel();
            model.AppendLog("before");
            model.ClearLog();
            Assert.Equal(string.Empty, model.LogText);

            model.AppendLog("after");
            model.FlushPendingLog();
            Assert.Equal(Joined("after"), model.LogText);
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Partitions_ApplyBatch_RebuildsVisibleRowsWithOneCollectionEvent()
    {
        await OnUiThread(() =>
        {
            var model = new FlashViewModel();
            var partitionEvents = 0;
            var visibleEvents = 0;
            model.Partitions.CollectionChanged += (_, _) => partitionEvents++;
            model.VisiblePartitions.CollectionChanged += (_, _) => visibleEvents++;
            var batch = Enumerable.Range(0, 400)
                .Select(index => new FlashPartitionEntry { Name = $"p{index}" })
                .ToList();

            model.Partitions.Apply(0, batch);

            Assert.Equal(400, model.Partitions.Count);
            Assert.Equal(400, model.VisiblePartitions.Count);
            Assert.True(model.HasVisiblePartitions);
            Assert.Equal(1, partitionEvents);
            Assert.Equal(1, visibleEvents);
            model.SearchText = "p1";
            Assert.Equal(111, model.VisiblePartitions.Count);
            model.SearchText = string.Empty;
            Assert.Equal(400, model.VisiblePartitions.Count);
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Partitions_ClearThenApplyBatch_ReselectsReplacementRowsAndReleasesOldOnes()
    {
        await OnUiThread(() =>
        {
            var model = new FlashViewModel();
            var stale = new FlashPartitionEntry { Name = "stale" };
            model.Partitions.Apply(0, [stale]);
            stale.IsSelected = true;
            Assert.Equal(1, model.SelectedCount);

            model.Partitions.Clear();
            model.Partitions.Apply(0, [new FlashPartitionEntry { Name = "fresh" }]);
            var fresh = Assert.Single(model.Partitions);
            fresh.IsSelected = true;
            Assert.Equal(1, model.SelectedCount);

            stale.IsSelected = false;
            Assert.Equal(1, model.SelectedCount);
            Assert.Single(model.VisiblePartitions);
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);
    }
}