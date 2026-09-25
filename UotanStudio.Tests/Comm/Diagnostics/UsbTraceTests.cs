using UotanStudio.Comm.Diagnostics;
using Xunit;

namespace UotanStudio.Tests.Comm.Diagnostics;

/// <summary>
/// USB diagnostics tests: <see cref="UsbTrace"/>'s structured transfer event
/// (<see cref="UsbTransferEvent"/>), the <see cref="IUsbLogger"/> bridging behaviour
/// (Log / LogError / LogFormatted) and the event emission error handling.
/// <para>USB 诊断测试：<see cref="UsbTrace"/> 的结构化传输事件（<see cref="UsbTransferEvent"/>）、
/// <see cref="IUsbLogger"/> 桥接行为（Log / LogError / LogFormatted）以及事件发送的错误处理。</para>
/// </summary>
/// <remarks>
/// All tests that touch the static <see cref="UsbTrace"/> state live in this single class
/// (xunit v3 runs classes in parallel), and each one restores the global logger/flag it
/// changes so the class remains order-independent.
/// <para>所有触及 <see cref="UsbTrace"/> 静态状态的测试都放在这一个类中
/// （xunit v3 并行运行不同类），并且每个测试都会还原其改动的全局 logger/开关，
/// 使类内顺序无关。</para>
/// </remarks>
public class UsbTraceTests
{
    private sealed class RecordingLogger : IUsbLogger
    {
        public List<string> Messages { get; } = [];

        public void Log(string message) => Messages.Add(message);
    }

    [Fact]
    public void UsbTransferEvent_ExposesAllFields()
    {
        var evt = new UsbTransferEvent
        {
            Backend = "winusb",
            DevicePath = @"\\.\usb#vid_1234&pid_5678",
            Operation = UsbTransferOperation.Write,
            RequestedBytes = 4096,
            TransferredBytes = 2048,
            TimeoutMs = 5000,
            RetryCount = 2,
            NativeErrorCode = 31,
            ElapsedMs = 12,
            Outcome = UsbTransferOutcome.ShortTransfer,
            Message = "short write",
            SetupPacket = new byte[] { 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 },
            Payload = new byte[] { 0x01, 0x02, 0x03 }
        };

        Assert.Equal("winusb", evt.Backend);
        Assert.Equal(UsbTransferOperation.Write, evt.Operation);
        Assert.Equal(4096, evt.RequestedBytes);
        Assert.Equal(2048, evt.TransferredBytes);
        Assert.Equal(5000, evt.TimeoutMs);
        Assert.Equal(2, evt.RetryCount);
        Assert.Equal(31, evt.NativeErrorCode);
        Assert.Equal(12L, evt.ElapsedMs);
        Assert.Equal(UsbTransferOutcome.ShortTransfer, evt.Outcome);
        Assert.Equal("short write", evt.Message);
        Assert.Equal(new byte[] { 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }, evt.SetupPacket);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, evt.Payload);
    }

    [Fact]
    public void EmitTransfer_RaisesEventWithExactFields()
    {
        var received = new List<UsbTransferEvent>();
        void Handler(UsbTransferEvent evt) => received.Add(evt);
        UsbTrace.TransferObserved += Handler;
        try
        {
            var evt = new UsbTransferEvent
            {
                Backend = "libusb",
                DevicePath = "usb:1-2.1",
                Operation = UsbTransferOperation.Read,
                RequestedBytes = 8192,
                TransferredBytes = 8192,
                TimeoutMs = 1000,
                RetryCount = 0,
                NativeErrorCode = null,
                ElapsedMs = 3,
                Outcome = UsbTransferOutcome.Success,
                Payload = new byte[] { 0xAA }
            };

            UsbTrace.EmitTransfer(evt);

            var raised = Assert.Single(received);
            Assert.Same(evt, raised);
            Assert.Equal("libusb", raised.Backend);
            Assert.Equal("usb:1-2.1", raised.DevicePath);
            Assert.Equal(UsbTransferOperation.Read, raised.Operation);
            Assert.Equal(8192, raised.TransferredBytes);
            Assert.Equal(UsbTransferOutcome.Success, raised.Outcome);
            Assert.Equal(new byte[] { 0xAA }, raised.Payload);
        }
        finally
        {
            UsbTrace.TransferObserved -= Handler;
        }
    }

    [Fact]
    public void HasTransferSubscribers_ReflectsCurrentSubscribers()
    {
        void Handler(UsbTransferEvent evt) { }
        Assert.False(UsbTrace.HasTransferSubscribers);

        UsbTrace.TransferObserved += Handler;
        try
        {
            Assert.True(UsbTrace.HasTransferSubscribers);
        }
        finally
        {
            UsbTrace.TransferObserved -= Handler;
        }

        Assert.False(UsbTrace.HasTransferSubscribers);
    }

    [Fact]
    public void EmitTransfer_NullEvent_IsNoOp()
    {
        UsbTrace.EmitTransfer(null!);
    }

    [Fact]
    public void EmitTransfer_SubscriberException_IsSwallowed()
    {
        void ThrowingHandler(UsbTransferEvent evt) => throw new InvalidOperationException("subscriber bug");
        UsbTrace.TransferObserved += ThrowingHandler;
        try
        {
            UsbTrace.EmitTransfer(new UsbTransferEvent { Operation = UsbTransferOperation.Enumerate });
        }
        finally
        {
            UsbTrace.TransferObserved -= ThrowingHandler;
        }
    }

    [Fact]
    public void Log_ForwardsToLoggerWhenEnabled()
    {
        var logger = new RecordingLogger();
        var previous = UsbTrace.Logger;
        var previousEnabled = UsbTrace.IsEnabled;
        try
        {
            UsbTrace.Logger = logger;
            UsbTrace.IsEnabled = true;

            UsbTrace.Log("hello usb");

            Assert.Equal(["hello usb"], logger.Messages);
        }
        finally
        {
            UsbTrace.Logger = previous;
            UsbTrace.IsEnabled = previousEnabled;
        }
    }

    [Fact]
    public void Log_WhenDisabled_DoesNotForward()
    {
        var logger = new RecordingLogger();
        var previous = UsbTrace.Logger;
        var previousEnabled = UsbTrace.IsEnabled;
        try
        {
            UsbTrace.Logger = logger;
            UsbTrace.IsEnabled = false;

            UsbTrace.Log("hello usb");

            Assert.Empty(logger.Messages);
        }
        finally
        {
            UsbTrace.Logger = previous;
            UsbTrace.IsEnabled = previousEnabled;
        }
    }

    [Fact]
    public void Log_EmptyMessage_IsIgnored()
    {
        var logger = new RecordingLogger();
        var previous = UsbTrace.Logger;
        var previousEnabled = UsbTrace.IsEnabled;
        try
        {
            UsbTrace.Logger = logger;
            UsbTrace.IsEnabled = true;

            UsbTrace.Log(string.Empty);
            UsbTrace.Log(null!);

            Assert.Empty(logger.Messages);
        }
        finally
        {
            UsbTrace.Logger = previous;
            UsbTrace.IsEnabled = previousEnabled;
        }
    }

    [Fact]
    public void LogError_ForwardsEvenWhenDisabled()
    {
        var logger = new RecordingLogger();
        var previous = UsbTrace.Logger;
        var previousEnabled = UsbTrace.IsEnabled;
        try
        {
            UsbTrace.Logger = logger;
            UsbTrace.IsEnabled = false;

            UsbTrace.LogError("native failure 31");

            Assert.Equal(["[ERROR] native failure 31"], logger.Messages);
        }
        finally
        {
            UsbTrace.Logger = previous;
            UsbTrace.IsEnabled = previousEnabled;
        }
    }

    [Fact]
    public void LogError_EmptyMessage_IsIgnored()
    {
        var logger = new RecordingLogger();
        var previous = UsbTrace.Logger;
        try
        {
            UsbTrace.Logger = logger;

            UsbTrace.LogError(string.Empty);
            UsbTrace.LogError(null!);

            Assert.Empty(logger.Messages);
        }
        finally
        {
            UsbTrace.Logger = previous;
        }
    }

    [Fact]
    public void LogFormatted_WhenEnabled_FormatsAndForwards()
    {
        var logger = new RecordingLogger();
        var previous = UsbTrace.Logger;
        var previousEnabled = UsbTrace.IsEnabled;
        try
        {
            UsbTrace.Logger = logger;
            UsbTrace.IsEnabled = true;

            var value = 42;
            UsbTrace.LogFormatted($"bulk write of {value} bytes");

            Assert.Equal(["bulk write of 42 bytes"], logger.Messages);
        }
        finally
        {
            UsbTrace.Logger = previous;
            UsbTrace.IsEnabled = previousEnabled;
        }
    }

    [Fact]
    public void LogFormatted_WhenDisabled_DoesNotForward()
    {
        var logger = new RecordingLogger();
        var previous = UsbTrace.Logger;
        var previousEnabled = UsbTrace.IsEnabled;
        try
        {
            UsbTrace.Logger = logger;
            UsbTrace.IsEnabled = false;

            UsbTrace.LogFormatted($"bulk write of {42} bytes");

            Assert.Empty(logger.Messages);
        }
        finally
        {
            UsbTrace.Logger = previous;
            UsbTrace.IsEnabled = previousEnabled;
        }
    }

    [Fact]
    public void CaptureFrames_IsConfigurableAndCapped()
    {
        var previous = UsbTrace.CaptureFrames;
        try
        {
            UsbTrace.CaptureFrames = true;
            Assert.True(UsbTrace.CaptureFrames);

            UsbTrace.CaptureFrames = false;
            Assert.False(UsbTrace.CaptureFrames);
        }
        finally
        {
            UsbTrace.CaptureFrames = previous;
        }

        Assert.Equal(256, UsbTrace.MaxCaptureBytes);
    }
}