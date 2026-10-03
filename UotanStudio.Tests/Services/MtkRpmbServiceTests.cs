using System.Security.Cryptography;
using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Services.Flash;
using UotanStudio.Tests.Comm.Protocols.Mtk.Rpmb;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class MtkRpmbServiceTests
{
    private const int FrameLength = MtkRpmbWire.FrameLength;

    [Fact]
    public async Task CaptureAsync_XFlashFrameRange_PublishesDeliveredFramesAndReplacesStaleFile()
    {
        using var directory = new TempDirectory();
        var destination = Path.Combine(directory.Path, "rpmb-xflash.bin");
        File.WriteAllText(destination, "stale content");
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXFlashRpmbSessionAsync(transport);
        MtkRpmbTestHarness.EnqueueRpmbReadReply(transport, MtkRpmbTestHarness.RpmbFrame(0xA1));
        MtkRpmbTestHarness.EnqueueRpmbReadReply(transport, MtkRpmbTestHarness.RpmbFrame(0xA2));
        var expected = Frame(0xA1).Concat(Frame(0xA2)).ToArray();
        var before = transport.Writes.Count;
        var stages = new List<FlashProgress>();

        var result = await MtkRpmbService.CaptureAsync(session, new MtkRpmbCaptureRequest
        {
            Family = MtkDaProtocol.XFlash,
            Mode = MtkRpmbCaptureMode.Frames,
            FrameRange = new MtkRpmbFrameRange(0, 2),
            DestinationPath = destination,
        }, new ProgressCollector(stages), TestContext.Current.CancellationToken);

        Assert.Equal(expected, File.ReadAllBytes(destination));
        Assert.Equal(destination, result.DestinationPath);
        Assert.Equal(MtkDaProtocol.XFlash, result.Family);
        Assert.Equal(MtkRpmbCaptureMode.Frames, result.Mode);
        Assert.Equal(512, result.ByteLength);
        Assert.Equal(2, result.DeliveredFrames);
        Assert.Equal(2, result.RequestedFrames);
        Assert.Equal(MtkRpmbTarget.Emmc, result.Target);
        Assert.Equal(1536, result.ReportedCapacityBytes);
        Assert.True(result.IsComplete);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(expected)), result.Sha256);
        Assert.Contains("XFlash RPMB capture in Frames shape", result.Summary, StringComparison.Ordinal);
        Assert.True(transport.Writes.Count > before);
        Assert.Contains(stages, stage => stage.Partition == "rpmb" && stage.Percent == 100);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.uotan-*"));
    }

    [Fact]
    public async Task CaptureAsync_XFlashFullArea_PublishesEveryReportedFrame()
    {
        using var directory = new TempDirectory();
        var destination = Path.Combine(directory.Path, "rpmb-xflash-area.bin");
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXFlashRpmbSessionAsync(transport);
        for (var index = 0; index < 6; index++)
        {
            MtkRpmbTestHarness.EnqueueRpmbReadReply(transport,
                MtkRpmbTestHarness.RpmbFrame((byte)(0xA0 + index)));
        }

        var result = await MtkRpmbService.CaptureAsync(session, new MtkRpmbCaptureRequest
        {
            Family = MtkDaProtocol.XFlash,
            Mode = MtkRpmbCaptureMode.FullArea,
            DestinationPath = destination,
        }, null, TestContext.Current.CancellationToken);

        Assert.Equal(1536, result.ByteLength);
        Assert.Equal(6, result.DeliveredFrames);
        Assert.Equal(6, result.RequestedFrames);
        Assert.True(result.IsComplete);
        var published = File.ReadAllBytes(destination);
        Assert.Equal(1536, published.Length);
        Assert.Equal(0xA0, published[0]);
        Assert.Equal(0xA5, published[1535]);
        Assert.Equal(MtkRpmbTarget.Emmc, result.Target);
    }

    [Fact]
    public async Task CaptureAsync_XmlFrameRange_PublishesFrameBytesForXmlAgent()
    {
        using var directory = new TempDirectory();
        var destination = Path.Combine(directory.Path, "rpmb-xml.bin");
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXmlRpmbSessionAsync(transport);
        MtkRpmbTestHarness.EnqueueXmlRpmbReadReply(transport, MtkRpmbTestHarness.RpmbFrame(0xD1));
        var before = transport.Writes.Count;

        var result = await MtkRpmbService.CaptureAsync(session, new MtkRpmbCaptureRequest
        {
            Family = MtkDaProtocol.Xml,
            Mode = MtkRpmbCaptureMode.Frames,
            FrameRange = new MtkRpmbFrameRange(0, 1),
            DestinationPath = destination,
        }, null, TestContext.Current.CancellationToken);

        Assert.Equal(Frame(0xD1), File.ReadAllBytes(destination));
        Assert.Equal(MtkDaProtocol.Xml, result.Family);
        Assert.Equal(1, result.DeliveredFrames);
        Assert.Equal(MtkRpmbTarget.Emmc, result.Target);
        Assert.Equal(8192, result.ReportedCapacityBytes);
        Assert.True(result.IsComplete);
        Assert.True(transport.Writes.Count > before);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.uotan-*"));
    }

    [Fact]
    public async Task CaptureAsync_LegacyByteRange_PublishesRawPartitionBytes()
    {
        using var directory = new TempDirectory();
        var destination = Path.Combine(directory.Path, "rpmb-legacy.bin");
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateLegacyRpmbSessionAsync(transport);
        var data = Enumerable.Repeat((byte)0xCD, 512).ToArray();
        MtkRpmbTestHarness.EnqueueLegacyRpmbRead(transport, data);
        var before = transport.Writes.Count;

        var result = await MtkRpmbService.CaptureAsync(session, new MtkRpmbCaptureRequest
        {
            Family = MtkDaProtocol.Legacy,
            Mode = MtkRpmbCaptureMode.ByteRange,
            ByteOffset = 0,
            ByteLength = 512,
            DestinationPath = destination,
        }, null, TestContext.Current.CancellationToken);

        Assert.Equal(data, File.ReadAllBytes(destination));
        Assert.Equal(MtkDaProtocol.Legacy, result.Family);
        Assert.Equal(512, result.ByteLength);
        Assert.Equal(3072, result.ReportedCapacityBytes);
        Assert.Null(result.Target);
        Assert.Equal(0, result.DeliveredFrames);
        Assert.True(result.IsComplete);
        Assert.True(transport.Writes.Count > before);
    }

    [Fact]
    public async Task CaptureAsync_LegacyFrameRange_RefusesFamilyWithoutIoOrFile()
    {
        using var directory = new TempDirectory();
        var destination = Path.Combine(directory.Path, "rpmb-legacy-frames.bin");
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateLegacyRpmbSessionAsync(transport);
        var before = transport.Writes.Count;

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => MtkRpmbService.CaptureAsync(session,
            new MtkRpmbCaptureRequest
            {
                Family = MtkDaProtocol.Legacy,
                Mode = MtkRpmbCaptureMode.Frames,
                FrameRange = new MtkRpmbFrameRange(0, 1),
                DestinationPath = destination,
            }, null, TestContext.Current.CancellationToken));

        Assert.Contains("exposes no frame indexing", error.Message);
        Assert.Equal(before, transport.Writes.Count);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task CaptureAsync_XFlashByteRange_RefusesFamilyWithoutIoOrFile()
    {
        using var directory = new TempDirectory();
        var destination = Path.Combine(directory.Path, "rpmb-xflash-bytes.bin");
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXFlashRpmbSessionAsync(transport);
        var before = transport.Writes.Count;

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => MtkRpmbService.CaptureAsync(session,
            new MtkRpmbCaptureRequest
            {
                Family = MtkDaProtocol.XFlash,
                Mode = MtkRpmbCaptureMode.ByteRange,
                ByteLength = 512,
                DestinationPath = destination,
            }, null, TestContext.Current.CancellationToken));

        Assert.Contains("addresses RPMB by frame index only", error.Message);
        Assert.Equal(before, transport.Writes.Count);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task CaptureAsync_LegacyFullArea_RefusesUnboundedDumpWithoutIo()
    {
        using var directory = new TempDirectory();
        var destination = Path.Combine(directory.Path, "rpmb-legacy-area.bin");
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateLegacyRpmbSessionAsync(transport);
        var before = transport.Writes.Count;

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => MtkRpmbService.CaptureAsync(session,
            new MtkRpmbCaptureRequest
            {
                Family = MtkDaProtocol.Legacy,
                Mode = MtkRpmbCaptureMode.FullArea,
                DestinationPath = destination,
            }, null, TestContext.Current.CancellationToken));

        Assert.Contains("reports no RPMB area size before a read", error.Message);
        Assert.Equal(before, transport.Writes.Count);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task CaptureAsync_AuthenticatedWriteOptIn_RefusesCaptureWithoutIo()
    {
        using var directory = new TempDirectory();
        var destination = Path.Combine(directory.Path, "rpmb-write.bin");
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXFlashRpmbSessionAsync(transport);
        var before = transport.Writes.Count;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => MtkRpmbService.CaptureAsync(session,
            new MtkRpmbCaptureRequest
            {
                Family = MtkDaProtocol.XFlash,
                Mode = MtkRpmbCaptureMode.Frames,
                DestinationPath = destination,
                Options = new MtkRpmbOptions { AllowAuthenticatedWrites = true },
            }, null, TestContext.Current.CancellationToken));

        Assert.Contains("authentication-key provisioning are not reachable", error.Message);
        Assert.Equal(before, transport.Writes.Count);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task CaptureAsync_PreCancelledToken_ThrowsWithoutIoOrFile()
    {
        using var directory = new TempDirectory();
        var destination = Path.Combine(directory.Path, "rpmb-cancelled.bin");
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateXFlashRpmbSessionAsync(transport);
        var before = transport.Writes.Count;
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => MtkRpmbService.CaptureAsync(session,
            new MtkRpmbCaptureRequest
            {
                Family = MtkDaProtocol.XFlash,
                Mode = MtkRpmbCaptureMode.Frames,
                DestinationPath = destination,
            }, null, cancellation.Token));

        Assert.Equal(before, transport.Writes.Count);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task CaptureAsync_EmptyByteLength_RefusesBeforeIo()
    {
        using var directory = new TempDirectory();
        var destination = Path.Combine(directory.Path, "rpmb-empty.bin");
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateLegacyRpmbSessionAsync(transport);
        var before = transport.Writes.Count;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => MtkRpmbService.CaptureAsync(session,
            new MtkRpmbCaptureRequest
            {
                Family = MtkDaProtocol.Legacy,
                Mode = MtkRpmbCaptureMode.ByteRange,
                ByteLength = 0,
                DestinationPath = destination,
            }, null, TestContext.Current.CancellationToken));

        Assert.Equal(before, transport.Writes.Count);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task CaptureAsync_CorruptLegacyPacket_KeepsPreviousDestinationContentAndStaging()
    {
        using var directory = new TempDirectory();
        var destination = Path.Combine(directory.Path, "rpmb-corrupt.bin");
        File.WriteAllText(destination, "previous capture");
        using var transport = new ScriptedCommTransport();
        using var session = await MtkRpmbTestHarness.CreateLegacyRpmbSessionAsync(transport);
        MtkRpmbTestHarness.EnqueueLegacyRpmbRead(transport,
            Enumerable.Repeat((byte)0x3C, 512).ToArray(), corruptChecksum: true);

        await Assert.ThrowsAsync<MtkProtocolException>(() => MtkRpmbService.CaptureAsync(session,
            new MtkRpmbCaptureRequest
            {
                Family = MtkDaProtocol.Legacy,
                Mode = MtkRpmbCaptureMode.ByteRange,
                ByteLength = 512,
                DestinationPath = destination,
            }, null, TestContext.Current.CancellationToken));

        Assert.Equal("previous capture", File.ReadAllText(destination));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.uotan-*"));
        Assert.Equal(MtkSessionState.Faulted, session.State);
    }

    [Fact]
    public void DescribeShapeRefusal_TabledCombinations_ReportsDocumentedSupport()
    {
        var combinations = new (MtkDaProtocol Family, MtkRpmbCaptureMode Mode, bool Supported)[]
        {
            (MtkDaProtocol.XFlash, MtkRpmbCaptureMode.Frames, true),
            (MtkDaProtocol.XFlash, MtkRpmbCaptureMode.FullArea, true),
            (MtkDaProtocol.XFlash, MtkRpmbCaptureMode.ByteRange, false),
            (MtkDaProtocol.Xml, MtkRpmbCaptureMode.Frames, true),
            (MtkDaProtocol.Xml, MtkRpmbCaptureMode.FullArea, true),
            (MtkDaProtocol.Xml, MtkRpmbCaptureMode.ByteRange, false),
            (MtkDaProtocol.Legacy, MtkRpmbCaptureMode.ByteRange, true),
            (MtkDaProtocol.Legacy, MtkRpmbCaptureMode.Frames, false),
            (MtkDaProtocol.Legacy, MtkRpmbCaptureMode.FullArea, false),
        };
        foreach (var (family, mode, supported) in combinations)
        {
            Assert.Equal(supported, MtkRpmbService.DescribeShapeRefusal(family, mode) is null);
        }
    }

    [Fact]
    public async Task CaptureOnEndpointAsync_WriteOptIn_RefusesBeforeLease()
    {
        var endpoint = new UotanStudio.Services.Devices.DetectedDevice
        {
            Key = "usb:fake-mtk",
            Serial = "fake",
            Protocol = UotanStudio.Services.Devices.DeviceProtocolKind.Mtk,
            Transport = UotanStudio.Services.Devices.DeviceTransportKind.Usb,
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MtkRpmbService.CaptureOnEndpointAsync(endpoint, new MtkRpmbCaptureRequest
            {
                Family = MtkDaProtocol.Legacy,
                Mode = MtkRpmbCaptureMode.ByteRange,
                ByteLength = 512,
                DestinationPath = "rpmb.bin",
                Options = new MtkRpmbOptions { AllowAuthenticatedWrites = true },
            }, null, CancellationToken.None));

        Assert.Contains("read-only RPMB captures", error.Message);
    }

    [Fact(Skip = "Requires physical MTK storage with an RPMB area; set UOTAN_TEST_MTK_USB_ID to run a bounded read-only frame capture.")]
    public async Task CaptureAsync_PhysicalRpmbArea_ReportsFramesWithoutWriting() =>
        await Task.CompletedTask;

    private static byte[] Frame(byte fill) => Enumerable.Repeat(fill, FrameLength).ToArray();

    private sealed class ProgressCollector(List<FlashProgress> stages) : IProgress<FlashProgress>
    {
        public void Report(FlashProgress value) => stages.Add(value);
    }
}