using UotanStudio.Comm.Diagnostics;
using Xunit;

namespace UotanStudio.Tests.Diagnostics;

public sealed class CommTraceTests
{
    [Theory]
    [InlineData(null, CommLogLevel.Off)]
    [InlineData("", CommLogLevel.Off)]
    [InlineData("off", CommLogLevel.Off)]
    [InlineData("none", CommLogLevel.Off)]
    [InlineData("0", CommLogLevel.Off)]
    [InlineData("error", CommLogLevel.Error)]
    [InlineData("INFO", CommLogLevel.Info)]
    [InlineData("debug", CommLogLevel.Debug)]
    [InlineData("true", CommLogLevel.Debug)]
    [InlineData("1", CommLogLevel.Debug)]
    [InlineData("trace", CommLogLevel.Trace)]
    [InlineData("all", CommLogLevel.Trace)]
    [InlineData("verbose", CommLogLevel.Trace)]
    [InlineData("  trace  ", CommLogLevel.Trace)]
    [InlineData("diagnostic", CommLogLevel.Off)]
    public void ParseLevel_KnownAndUnknownTokens_MapToDocumentedLevels(string? text, CommLogLevel expected)
    {
        Assert.Equal(expected, CommTrace.ParseLevel(text));
    }

    [Fact]
    public void Write_TraceWhileDebugIsSelected_EmitsNothing()
    {
        var recorder = new RecordingCommLogger();
        CommTrace.Level = CommLogLevel.Debug;
        CommTrace.Logger = recorder;
        try
        {
            CommTrace.Trace("usb", $"{1} chunk");
            CommTrace.Debug("usb", $"{1} chunk at debug");
        }
        finally
        {
            CommTrace.Level = CommLogLevel.Off;
            CommTrace.Logger = new RecordingCommLogger();
        }

        var record = Assert.Single(recorder.Records);
        Assert.Equal(CommLogLevel.Debug, record.Level);
        Assert.Equal("usb", record.Category);
        Assert.Equal("1 chunk at debug", record.Message);
        Assert.False(CommTrace.IsEnabled(CommLogLevel.Off));
    }

    [Fact]
    public void Error_LevelOff_StillReachesTheSink()
    {
        var recorder = new RecordingCommLogger();
        CommTrace.Level = CommLogLevel.Off;
        CommTrace.Logger = recorder;
        try
        {
            CommTrace.Write(CommLogLevel.Info, "lease", $"{0} leases");
            CommTrace.Error("lease", $"lease {7} failed");
        }
        finally
        {
            CommTrace.Logger = new RecordingCommLogger();
        }

        var record = Assert.Single(recorder.Records);
        Assert.Equal(CommLogLevel.Error, record.Level);
        Assert.Equal("lease 7 failed", record.Message);
    }

    [Fact]
    public void Payload_CaptureDisabled_WritesNothingEvenAtTrace()
    {
        var recorder = new RecordingCommLogger();
        CommTrace.Level = CommLogLevel.Trace;
        CommTrace.CaptureRawPayloads = false;
        CommTrace.Logger = recorder;
        try
        {
            CommTrace.Payload("adb-shell", "stdout", "hello"u8);
        }
        finally
        {
            CommTrace.Level = CommLogLevel.Off;
            CommTrace.Logger = new RecordingCommLogger();
        }

        Assert.Empty(recorder.Records);
    }

    [Fact]
    public void Payload_CaptureEnabled_WritesBoundedPreviewAtTrace()
    {
        var recorder = new RecordingCommLogger();
        var payload = new byte[300];
        CommTrace.Level = CommLogLevel.Trace;
        CommTrace.CaptureRawPayloads = true;
        CommTrace.Logger = recorder;
        try
        {
            CommTrace.Payload("usb", "frame", payload);
        }
        finally
        {
            CommTrace.CaptureRawPayloads = false;
            CommTrace.Level = CommLogLevel.Off;
            CommTrace.Logger = new RecordingCommLogger();
        }

        var record = Assert.Single(recorder.Records);
        Assert.Equal(CommLogLevel.Trace, record.Level);
        Assert.StartsWith("frame: 300 B: 00 00 00 00", record.Message, StringComparison.Ordinal);
        Assert.EndsWith("(truncated)", record.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void State_InfoLevel_ReportsTheMessageAtInfo()
    {
        var recorder = new RecordingCommLogger();
        CommTrace.Level = CommLogLevel.Info;
        CommTrace.Logger = recorder;
        try
        {
            CommTrace.State("adb-sync", $"push done {12} B");
        }
        finally
        {
            CommTrace.Level = CommLogLevel.Off;
            CommTrace.Logger = new RecordingCommLogger();
        }

        var record = Assert.Single(recorder.Records);
        Assert.Equal(CommLogLevel.Info, record.Level);
        Assert.Equal("adb-sync", record.Category);
        Assert.Equal("push done 12 B", record.Message);
    }

    [Fact]
    public void Error_RenderedStringOverload_ReachesTheSinkWithoutLevelGating()
    {
        var recorder = new RecordingCommLogger();
        CommTrace.Level = CommLogLevel.Off;
        CommTrace.Logger = recorder;
        try
        {
            CommTrace.Error("adb-link", "rx #1 header rejected: " + PayloadPreview.Format([0x01]));
        }
        finally
        {
            CommTrace.Logger = new RecordingCommLogger();
        }

        var record = Assert.Single(recorder.Records);
        Assert.Equal(CommLogLevel.Error, record.Level);
        Assert.StartsWith("rx #1 header rejected: 1 B: 01 |.|", record.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Debug_RenderedStringOverload_IsGatedByLevel()
    {
        var recorder = new RecordingCommLogger();
        CommTrace.Level = CommLogLevel.Info;
        CommTrace.Logger = recorder;
        try
        {
            CommTrace.Debug("adb-link", "rx #1 header");
        }
        finally
        {
            CommTrace.Level = CommLogLevel.Off;
            CommTrace.Logger = new RecordingCommLogger();
        }

        Assert.Empty(recorder.Records);
    }

    private sealed class RecordingCommLogger : ICommLogger
    {
        public List<(CommLogLevel Level, string Category, string Message)> Records { get; } = [];

        public void Log(CommLogLevel level, string category, string message)
            => Records.Add((level, category, message));
    }
}