using UotanStudio.Services;
using Xunit;

namespace UotanStudio.Tests.Services;

/// <summary>
/// Retention policy of the unified log store: the newest output survives until the entry guard or the character
/// budget is exceeded, and clearing resets both counters.
/// <para>统一日志存储的保留策略：在新输出超过条目数护栏或字符预算前保留最新内容，清空后两个计数一起归零。</para>
/// </summary>
public sealed class LogServiceRetentionTests
{
    [Fact]
    public void Write_OverEntryGuard_KeepsNewestEntries()
    {
        var service = new LogService { MaxQueueSize = 100, MaxQueueCharacters = long.MaxValue };

        for (var i = 0; i < 150; i++)
        {
            service.Write(LogLevel.Info, "Retention", $"m{i:D3}");
        }

        var entries = service.Entries;
        Assert.Equal(100, entries.Count);
        Assert.Equal("m050", entries[0].Message);
        Assert.Equal("m149", entries[^1].Message);
    }

    [Fact]
    public void Write_OverCharacterBudget_EvictsOldestUntilItFits()
    {
        var service = new LogService { MaxQueueSize = int.MaxValue, MaxQueueCharacters = 64 * 1024 };
        var message = new string('x', 16 * 1024);

        for (var i = 0; i < 10; i++)
        {
            service.Write(LogLevel.Info, "S", $"{i}:{message}");
        }

        var entries = service.Entries;
        Assert.Equal(3, entries.Count);
        Assert.Equal($"7:{message}", entries[0].Message);
        Assert.Equal($"9:{message}", entries[^1].Message);
    }

    [Fact]
    public void MaxQueueCharacters_Lowering_TrimsExistingEntries()
    {
        var service = new LogService { MaxQueueSize = int.MaxValue, MaxQueueCharacters = 1024 * 1024 };
        var message = new string('y', 20 * 1024);
        for (var i = 0; i < 5; i++)
        {
            service.Write(LogLevel.Info, "S", $"{i}:{message}");
        }
        Assert.Equal(5, service.Entries.Count);

        service.MaxQueueCharacters = 64 * 1024;

        var entries = service.Entries;
        Assert.Equal(3, entries.Count);
        Assert.Equal($"2:{message}", entries[0].Message);
    }

    [Fact]
    public void MaxQueueSize_Lowering_TrimsExistingEntries()
    {
        var service = new LogService { MaxQueueSize = 1000, MaxQueueCharacters = long.MaxValue };
        for (var i = 0; i < 150; i++)
        {
            service.Write(LogLevel.Info, "Retention", $"m{i:D3}");
        }

        service.MaxQueueSize = 100;

        var entries = service.Entries;
        Assert.Equal(100, entries.Count);
        Assert.Equal("m050", entries[0].Message);
    }

    [Fact]
    public void Clear_ResetsCharacterAccounting_SoLaterWritesAreRetained()
    {
        var service = new LogService { MaxQueueSize = int.MaxValue, MaxQueueCharacters = 64 * 1024 };

        service.Write(LogLevel.Info, "S", new string('z', 60 * 1024));
        service.Clear();
        service.Write(LogLevel.Info, "S", new string('z', 60 * 1024));

        Assert.Single(service.Entries);
    }
}