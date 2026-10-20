using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using UotanStudio.Media.Decoding;
using UotanStudio.Media.Decoding.MediaFoundation;
using Xunit;

namespace UotanStudio.Tests.Media;

public sealed class DxgiInteropLayoutTests
{
    [Fact]
    [SupportedOSPlatform("windows")]
    public void DxgiResourceAndKeyedMutex_Iids_MatchTheSdk()
    {
        Assert.Equal(Guid.Parse("035F3AB4-482E-4E50-B41F-8A7F8BD8960B"), DxgiInterop.IidDxgiResource);
        Assert.Equal(Guid.Parse("9D8E1289-D7B3-465F-8126-250E349AF85D"), DxgiInterop.IidDxgiKeyedMutex);
        Assert.Equal(Guid.Parse("770AAE78-F26F-4DBA-A829-253C83D1B387"), DxgiInterop.IidDxgiFactory1);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void DxgiAdapterDesc_HasNativeSizeAndLuidAtTheTail()
    {
        Assert.Equal(304, Marshal.SizeOf<DxgiAdapterDesc>());
        Assert.Equal(272, Marshal.OffsetOf<DxgiAdapterDesc>(nameof(DxgiAdapterDesc.DedicatedVideoMemory)).ToInt32());
        Assert.Equal(296, Marshal.OffsetOf<DxgiAdapterDesc>(nameof(DxgiAdapterDesc.AdapterLuidLowPart)).ToInt32());
        Assert.Equal(300, Marshal.OffsetOf<DxgiAdapterDesc>(nameof(DxgiAdapterDesc.AdapterLuidHighPart)).ToInt32());
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void SharedFrame_DuplicateMarkPresented_DoesNotReopenANewerHandoff()
    {
        using var ring = new ExportSlotRing<MediaFoundationExportSlot>(1,
            _ => new MediaFoundationExportSlot { Texture = 1, OutputView = 2, KeyedMutex = 3, SharedHandle = 4 },
            _ => { },
            _ => true);
        var first = ring.Rent();
        Assert.NotNull(first);
        ring.CloseGate(first);
        var frame = new D3D11SharedVideoFrame(ring, first, first.SharedHandle, 8, 4, 0,
            VideoColorMatrix.Bt709, VideoColorRange.Limited);
        frame.MarkPresented();
        var second = ring.Rent();
        Assert.NotNull(second);
        ring.CloseGate(second);
        frame.MarkPresented();
        Assert.Null(ring.Rent());
        ring.MarkPresented(second);
        Assert.NotNull(ring.Rent());
    }

    [Fact]
    public void ExportSlotRing_MarkPresentedWithClosedFlag_IsNoop()
    {
        using var ring = new ExportSlotRing<MediaFoundationExportSlot>(1,
            _ => new MediaFoundationExportSlot { Texture = 1, OutputView = 2, KeyedMutex = 3, SharedHandle = 4 },
            _ => { },
            _ => true);
        var slot = ring.Rent();
        Assert.NotNull(slot);
        ring.CloseGate(slot);
        ring.MarkPresented(slot, open: false);
        Assert.Null(ring.Rent());
        ring.MarkPresented(slot);
        Assert.NotNull(ring.Rent());
    }
}