using Microsoft.Extensions.Logging;
using UotanStudio.Services;
using UotanStudio.Services.Logging;
using Xunit;
using AppLogLevel = UotanStudio.Services.LogLevel;
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace UotanStudio.Tests.Services;

public sealed class UnifiedLogLoggerBridgeTests
{
    private static readonly Func<string, Exception?, string> PassThrough = static (state, _) => state;

    [Fact]
    public void Log_InformationWithStructuredArguments_EnqueuesFormattedInfoEntryNamedByCategory()
    {
        var source = "uotan-bridge-" + Guid.NewGuid().ToString("N");
        var entries = Record(source, service =>
        {
            var logger = new UnifiedLogLoggerFactory(service).CreateLogger(source);
            logger.LogInformation("session {Count} opened", 3);
        });

        var entry = Assert.Single(entries);
        Assert.Equal(AppLogLevel.Info, entry.Level);
        Assert.Equal(source, entry.Source);
        Assert.Equal("session 3 opened", entry.Message);
    }

    [Fact]
    public void Log_StructuredArguments_AreForwardedAsFieldsWithoutTheTemplateKey()
    {
        var source = "uotan-bridge-" + Guid.NewGuid().ToString("N");
        var entries = Record(source, service =>
        {
            var logger = new UnifiedLogLoggerFactory(service).CreateLogger(source);
            logger.LogInformation("session {Count} opened at {Path}", 3, "/tmp/a");
        });

        var entry = Assert.Single(entries);
        var fields = Assert.IsAssignableFrom<IReadOnlyList<LogField>>(entry.Fields);
        Assert.Collection(fields,
            field =>
            {
                Assert.Equal("Count", field.Key);
                Assert.Equal("3", field.Value);
            },
            field =>
            {
                Assert.Equal("Path", field.Key);
                Assert.Equal("/tmp/a", field.Value);
            });
    }

    [Theory]
    [InlineData(MelLogLevel.Trace, AppLogLevel.Trace)]
    [InlineData(MelLogLevel.Debug, AppLogLevel.Debug)]
    [InlineData(MelLogLevel.Information, AppLogLevel.Info)]
    [InlineData(MelLogLevel.Warning, AppLogLevel.Warn)]
    [InlineData(MelLogLevel.Error, AppLogLevel.Error)]
    [InlineData(MelLogLevel.Critical, AppLogLevel.Fatal)]
    public void Log_EachProducerLevel_MapsOntoTheUnifiedLevel(MelLogLevel producer, AppLogLevel expected)
    {
        var source = "uotan-bridge-" + Guid.NewGuid().ToString("N");
        var entries = Record(source, service =>
        {
            var logger = new UnifiedLogLoggerFactory(service).CreateLogger(source);
            logger.Log(producer, default, "payload", null, PassThrough);
        });

        var entry = Assert.Single(entries);
        Assert.Equal(expected, entry.Level);
    }

    [Fact]
    public void Log_NoneLevel_EnqueuesNothingAndIsDisabled()
    {
        var source = "uotan-bridge-" + Guid.NewGuid().ToString("N");
        ILogger? captured = null;
        var entries = Record(source, service =>
        {
            captured = new UnifiedLogLoggerFactory(service).CreateLogger(source);
            captured.Log(MelLogLevel.None, default, "invisible", null, PassThrough);
        });

        Assert.Empty(entries);
        Assert.NotNull(captured);
        Assert.False(captured!.IsEnabled(MelLogLevel.None));
    }

    [Fact]
    public void Log_DebugWhileDebugModeOff_IsSkippedByTheStoreMinimum()
    {
        var source = "uotan-bridge-" + Guid.NewGuid().ToString("N");
        var service = LogService.Instance;
        var entries = new List<LogEntry>();
        var unsubscribe = service.Subscribe((_, entry) =>
        {
            if (string.Equals(entry.Source, source, StringComparison.Ordinal))
            {
                entries.Add(entry);
            }
        });
        try
        {
            service.ApplyDebugMode(debug: false);
            var logger = new UnifiedLogLoggerFactory(service).CreateLogger(source);
            Assert.False(logger.IsEnabled(MelLogLevel.Debug));
            logger.LogDebug("below the minimum");
            Assert.Empty(entries);

            service.ApplyDebugMode(debug: true);
            Assert.True(logger.IsEnabled(MelLogLevel.Debug));
            logger.LogDebug("now recorded");
            var entry = Assert.Single(entries);
            Assert.Equal("now recorded", entry.Message);
        }
        finally
        {
            unsubscribe();
            service.ApplyDebugMode(debug: false);
        }
    }

    [Fact]
    public void CreateLogger_BlankCategory_FallsBackToTheDefaultSource()
    {
        var entries = Record(UnifiedLogLoggerFactory.DefaultSource, service =>
        {
            var logger = new UnifiedLogLoggerFactory(service).CreateLogger(string.Empty);
            logger.LogWarning("unnamed producer");
        });

        var entry = Assert.Single(entries);
        Assert.Equal(AppLogLevel.Warn, entry.Level);
        Assert.Equal("unnamed producer", entry.Message);
    }

    [Fact]
    public void Log_WithEventId_AppendsTheIdentifierToTheText()
    {
        var source = "uotan-bridge-" + Guid.NewGuid().ToString("N");
        var entries = Record(source, service =>
        {
            var logger = new UnifiedLogLoggerFactory(service).CreateLogger(source);
            logger.LogWarning(new EventId(42, "PairVerify"), "boom");
        });

        var entry = Assert.Single(entries);
        Assert.Equal(AppLogLevel.Warn, entry.Level);
        Assert.Equal("boom [event 42 PairVerify]", entry.Message);
    }

    [Fact]
    public void LogError_WithExceptionCarriedSeparately_KeepsTheStackTraceInTheEntry()
    {
        var source = "uotan-bridge-" + Guid.NewGuid().ToString("N");
        var entries = Record(source, service =>
        {
            var logger = new UnifiedLogLoggerFactory(service).CreateLogger(source);
            logger.LogError(new InvalidOperationException("socket died"), "handshake failed");
        });

        var entry = Assert.Single(entries);
        Assert.Equal(AppLogLevel.Error, entry.Level);
        Assert.Contains("handshake failed", entry.Message, StringComparison.Ordinal);
        Assert.Contains("socket died", entry.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateLogger_SameCategoryTwice_ReusesOneCachedLogger()
    {
        var factory = new UnifiedLogLoggerFactory(LogService.Instance);
        var source = "uotan-bridge-" + Guid.NewGuid().ToString("N");

        Assert.Same(factory.CreateLogger(source), factory.CreateLogger(source));
    }

    private static List<LogEntry> Record(string source, Action<LogService> write)
    {
        var service = LogService.Instance;
        var entries = new List<LogEntry>();
        var unsubscribe = service.Subscribe((_, entry) =>
        {
            if (string.Equals(entry.Source, source, StringComparison.Ordinal))
            {
                entries.Add(entry);
            }
        });
        try
        {
            service.ApplyDebugMode(debug: true);
            write(service);
        }
        finally
        {
            unsubscribe();
            service.ApplyDebugMode(debug: false);
        }

        return entries;
    }
}