using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using UotanStudio.Media.Acceleration.Linux;
using UotanStudio.Media.Decoding;
using Xunit;

namespace UotanStudio.Tests.Media;

[SupportedOSPlatform("linux")]
public sealed class VaApiDmabufExportTests
{
    [Fact]
    public void ExportPath_DeliversHeldSurface_AndReleasesOnDispose()
    {
        var session = new FakeSession();
        session.ReceiveStatuses.Enqueue(1);
        using var decoder = CreateDecoder(session);
        var frames = new List<VaDmaBufVideoFrame>();
        decoder.DmabufFrameCallback = frames.Add;
        decoder.Decode(Picture, 111);
        VaDmaBufVideoFrame frame = Assert.Single(frames);
        Assert.Equal(4, frame.Width);
        Assert.Equal(VideoColorMatrix.Bt709, frame.ColorMatrix);
        Assert.Equal(VideoColorRange.Limited, frame.ColorRange);
        Assert.Equal(111, frame.TimestampTicks);
        Assert.Equal(1, session.ExportCalls);
        Assert.Equal(0, session.ReleaseFrames);
        Assert.Equal(0, session.CopyCalls);
        frame.Dispose();
        Assert.Equal(1, session.ReleaseFrames);
    }

    [Fact]
    public void ExportFailure_DisablesExportAndDownloadsTheSameFrame_ThenRecoversAfterReset()
    {
        var session = new FakeSession { ExportStatus = -5 };
        session.ReceiveStatuses.Enqueue(1);
        var dmabuf = new List<VaDmaBufVideoFrame>();
        var nv12 = new List<DecodedVideoFrame>();
        using var decoder = new VaApiVideoDecoder(new VideoDecoderOptions { VaApiPreferGpuExport = true }, (_, _) => session);
        decoder.Initialize(VideoCodecId.H264, 4, 4, nv12.Add);
        decoder.DmabufFrameCallback = dmabuf.Add;
        decoder.Decode(Picture, 222);
        Assert.Empty(dmabuf);
        Assert.Single(nv12);
        Assert.Equal(1, session.ExportCalls);
        Assert.Equal(0, session.ReleaseFrames);
        Assert.Equal(1, session.CopyCalls);
        nv12[0].Dispose();
        decoder.Reset();
        session.ExportStatus = 0;
        session.ReceiveStatuses.Enqueue(1);
        decoder.Decode(Picture, 333);
        Assert.Equal(2, session.ExportCalls);
        Assert.Equal(1, session.CopyCalls);
        VaDmaBufVideoFrame resumed = Assert.Single(dmabuf);
        resumed.Dispose();
        Assert.Equal(1, session.ReleaseFrames);
    }

    [Fact]
    public void UnsupportedChromaFourcc_DisablesExportAndDownloadsTheSameFrame()
    {
        var session = new FakeSession { ChromaFourcc = 0x30323149 };
        session.ReceiveStatuses.Enqueue(1);
        var dmabuf = new List<VaDmaBufVideoFrame>();
        var nv12 = new List<DecodedVideoFrame>();
        using var decoder = new VaApiVideoDecoder(new VideoDecoderOptions { VaApiPreferGpuExport = true }, (_, _) => session);
        decoder.Initialize(VideoCodecId.H264, 4, 4, nv12.Add);
        decoder.DmabufFrameCallback = dmabuf.Add;
        decoder.Decode(Picture, 444);
        Assert.Empty(dmabuf);
        Assert.Single(nv12);
        Assert.Equal(1, session.ExportCalls);
        Assert.Equal(1, session.CopyCalls);
        nv12[0].Dispose();
    }

    [Fact]
    public void ComposedSingleLayerDescriptor_DisablesExportAndDownloadsTheSameFrame()
    {
        var session = new FakeSession { LayerCount = 1 };
        session.ReceiveStatuses.Enqueue(1);
        var dmabuf = new List<VaDmaBufVideoFrame>();
        var nv12 = new List<DecodedVideoFrame>();
        using var decoder = new VaApiVideoDecoder(new VideoDecoderOptions { VaApiPreferGpuExport = true }, (_, _) => session);
        decoder.Initialize(VideoCodecId.H264, 4, 4, nv12.Add);
        decoder.DmabufFrameCallback = dmabuf.Add;
        decoder.Decode(Picture, 555);
        Assert.Empty(dmabuf);
        Assert.Single(nv12);
        Assert.Equal(1, session.CopyCalls);
        nv12[0].Dispose();
    }

    [Fact]
    public void Gr88ChromaLayer_IsAcceptedAndReportedAsCrCbOrder()
    {
        var session = new FakeSession();
        session.ReceiveStatuses.Enqueue(1);
        using var decoder = CreateDecoder(session);
        var frames = new List<VaDmaBufVideoFrame>();
        decoder.DmabufFrameCallback = frames.Add;
        decoder.Decode(Picture, 666);
        VaDmaBufVideoFrame frame = Assert.Single(frames);
        Assert.Equal(DmabufChromaOrder.CrCb, frame.Surface.ChromaOrder);
        frame.Dispose();
    }

    [Fact]
    public void Rg88ChromaLayer_IsAcceptedAndReportedAsCbCrOrder()
    {
        var session = new FakeSession { ChromaFourcc = 0x38384752 };
        session.ReceiveStatuses.Enqueue(1);
        using var decoder = CreateDecoder(session);
        var frames = new List<VaDmaBufVideoFrame>();
        decoder.DmabufFrameCallback = frames.Add;
        decoder.Decode(Picture, 777);
        VaDmaBufVideoFrame frame = Assert.Single(frames);
        Assert.Equal(DmabufChromaOrder.CbCr, frame.Surface.ChromaOrder);
        frame.Dispose();
    }

    [Fact]
    public void UnreleasedExport_ReclaimedAfterWaitAndSessionKeepsRunning()
    {
        var session = new FakeSession();
        session.ReceiveStatuses.Enqueue(1);
        using var decoder = CreateDecoder(session);
        decoder.ExportReleaseWaitMilliseconds = 15;
        var frames = new List<VaDmaBufVideoFrame>();
        decoder.DmabufFrameCallback = frames.Add;
        decoder.Decode(Picture, 1);
        Assert.Single(frames);
        Assert.True(session.HoldsFrame);
        Assert.Equal(0, decoder.ExportAbandonedFrames);
        decoder.Decode(Picture, 2);
        Assert.Equal(1, decoder.ExportAbandonedFrames);
        Assert.Equal(1, session.ReleaseFrames);
        Assert.False(session.HoldsFrame);
        frames[0].Dispose();
        Assert.Equal(1, session.ReleaseFrames);
    }

    [Fact]
    public void PresenterReleasesBeforeWaitExpires_NothingIsReclaimed()
    {
        var session = new FakeSession();
        session.ReceiveStatuses.Enqueue(1);
        using var decoder = CreateDecoder(session);
        decoder.ExportReleaseWaitMilliseconds = 60_000;
        var frames = new List<VaDmaBufVideoFrame>();
        decoder.DmabufFrameCallback = frames.Add;
        decoder.Decode(Picture, 1);
        frames[0].Dispose();
        decoder.Decode(Picture, 2);
        Assert.Equal(0, decoder.ExportAbandonedFrames);
        Assert.Equal(1, session.ReleaseFrames);
    }

    [Fact]
    public void SurfacePinnedByTheDriver_ReclaimsOnceThenFailsInsteadOfLoopingForever()
    {
        var session = new FakeSession { HoldForever = true };
        session.ReceiveStatuses.Enqueue(1);
        using var decoder = CreateDecoder(session);
        decoder.ExportReleaseWaitMilliseconds = 5;
        var frames = new List<VaDmaBufVideoFrame>();
        decoder.DmabufFrameCallback = frames.Add;
        decoder.Decode(Picture, 1);
        Assert.Throws<InvalidDataException>(() => decoder.Decode(Picture, 2));
        Assert.Equal(1, decoder.ExportAbandonedFrames);
    }

    [Fact]
    public void ResetThenDisposeOfOutstandingFrame_StaleReleaseIsNoOp()
    {
        var session = new FakeSession();
        session.ReceiveStatuses.Enqueue(1);
        using var decoder = CreateDecoder(session);
        var frames = new List<VaDmaBufVideoFrame>();
        decoder.DmabufFrameCallback = frames.Add;
        decoder.Decode(Picture, 1);
        decoder.Reset();
        frames[0].Dispose();
        Assert.Equal(0, session.ReleaseFrames);
    }

    [Fact]
    public void DmabufFrame_MatchesNativeBridgeAbiLayout()
    {
        Assert.Equal(328, Unsafe.SizeOf<VaDmaBufFrame>());
        Assert.Equal(16, (int)Marshal.OffsetOf<VaDmaBufFrame>(nameof(VaDmaBufFrame.Objects)));
        Assert.Equal(80, (int)Marshal.OffsetOf<VaDmaBufFrame>(nameof(VaDmaBufFrame.LayerCount)));
        Assert.Equal(84, (int)Marshal.OffsetOf<VaDmaBufFrame>(nameof(VaDmaBufFrame.Layers)));
        Assert.Equal(308, (int)Marshal.OffsetOf<VaDmaBufFrame>(nameof(VaDmaBufFrame.Matrix)));
        Assert.Equal(312, (int)Marshal.OffsetOf<VaDmaBufFrame>(nameof(VaDmaBufFrame.Range)));
        Assert.Equal(320, (int)Marshal.OffsetOf<VaDmaBufFrame>(nameof(VaDmaBufFrame.Pts)));
    }

    private static readonly byte[] Picture = [0, 0, 1, 0x65, 0x80];

    private static VaApiVideoDecoder CreateDecoder(FakeSession session)
    {
        var decoder = new VaApiVideoDecoder(new VideoDecoderOptions { VaApiPreferGpuExport = true }, (_, _) => session);
        decoder.Initialize(VideoCodecId.H264, 4, 4, static _ => { });
        return decoder;
    }

    private sealed class FakeSession : IVaCodecSession
    {
        public Queue<int> ReceiveStatuses { get; } = new();
        public int ExportStatus { get; set; }
        public uint ChromaFourcc { get; set; } = 0x38385247;
        public uint LayerCount { get; set; } = 2;
        public bool HoldForever { get; set; }
        public bool HoldsFrame { get; private set; }
        public int ExportCalls { get; private set; }
        public int ReleaseFrames { get; private set; }
        public int CopyCalls { get; private set; }
        public bool Disposed { get; private set; }

        public int Send(ReadOnlySpan<byte> packet, long timestamp) => HoldsFrame ? 2 : 0;

        public int Receive(out VaFrameInfo info)
        {
            if (ReceiveStatuses.TryDequeue(out var status))
            {
                info = Frame();
                return status;
            }
            info = default;
            return 0;
        }

        public void Copy(Span<byte> destination) => CopyCalls++;

        public int Export(out VaDmaBufFrame frame)
        {
            ExportCalls++;
            frame = new VaDmaBufFrame
            {
                Format = 0x3432584e,
                Width = 4,
                Height = 4,
                ObjectCount = 1,
                LayerCount = LayerCount,
                Matrix = 2,
                Range = 1,
                Pts = 111,
            };
            frame.Objects[0] = new DmaBufObject { Fd = -1, Size = 64, Modifier = 0 };
            frame.Layers[0] = new DmaBufLayer { Format = 0x20203852, Planes = 1 };
            frame.Layers[1] = new DmaBufLayer { Format = ChromaFourcc, Planes = 1 };
            if (ExportStatus == 0) HoldsFrame = true;
            return ExportStatus;
        }

        public void ReleaseFrame()
        {
            ReleaseFrames++;
            if (!HoldForever) HoldsFrame = false;
        }
        public void Reset() { }
        public void Dispose() => Disposed = true;

        private static VaFrameInfo Frame() => new()
        {
            Width = 4,
            Height = 4,
            Stride = 4,
            Length = 24,
            Matrix = 2,
            Range = 1,
            Timestamp = 111,
        };
    }
}