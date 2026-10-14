using System.Runtime.Versioning;
using UotanStudio.Media.Acceleration.Linux;
using Xunit;

namespace UotanStudio.Tests.Media;

[SupportedOSPlatform("linux")]
public sealed class DmaBufNv12LayoutTests
{
    private const uint LumaR8 = 0x20203852;
    private const uint ChromaRg88 = 0x38384752;
    private const uint ChromaGr88 = 0x38385247;

    [Theory]
    [InlineData(ChromaRg88, DmabufChromaOrder.CbCr)]
    [InlineData(ChromaGr88, DmabufChromaOrder.CrCb)]
    public void TryClassify_SupportedChroma_AcceptsAndReportsOrder(uint chroma, DmabufChromaOrder expected)
    {
        Assert.True(Nv12PlaneLayout.TryClassify(2, LumaR8, 1, chroma, 1, out DmabufChromaOrder order));
        Assert.Equal(expected, order);
    }

    [Theory]
    [InlineData(1u, LumaR8, 1u, ChromaGr88, 1u)]
    [InlineData(3u, LumaR8, 1u, ChromaGr88, 1u)]
    [InlineData(2u, 0x20203859u, 1u, ChromaGr88, 1u)]
    [InlineData(2u, LumaR8, 2u, ChromaGr88, 1u)]
    [InlineData(2u, LumaR8, 1u, 0x30323149u, 1u)]
    [InlineData(2u, LumaR8, 1u, ChromaGr88, 2u)]
    public void TryClassify_OtherLayouts_AreRejected(uint layers, uint luma, uint lumaPlanes, uint chroma, uint chromaPlanes)
        => Assert.False(Nv12PlaneLayout.TryClassify(layers, luma, lumaPlanes, chroma, chromaPlanes, out _));

    private static DmaBufDescriptor Descriptor(uint layerCount, uint lumaFourcc, uint chromaFourcc)
    {
        var descriptor = new DmaBufDescriptor
        {
            Format = 0x3231564e,
            Width = 4,
            Height = 4,
            ObjectCount = 1,
            LayerCount = layerCount,
        };
        descriptor.Objects[0] = new DmaBufObject { Fd = -1, Size = 64, Modifier = 0 };
        descriptor.Layers[0] = new DmaBufLayer { Format = lumaFourcc, Planes = 1 };
        descriptor.Layers[1] = new DmaBufLayer { Format = chromaFourcc, Planes = 1 };
        return descriptor;
    }

    [Fact]
    public void Surface_Gr88Chroma_ReportsCrCbOrder()
    {
        using var surface = new DmaBufSurface(Descriptor(2, LumaR8, ChromaGr88));
        Assert.Equal(DmabufChromaOrder.CrCb, surface.ChromaOrder);
    }

    [Fact]
    public void Surface_UnknownChromaFourcc_ThrowsAndNamesBothLayers()
    {
        var error = Assert.Throws<InvalidDataException>(() => new DmaBufSurface(Descriptor(2, LumaR8, 0x30323149)));
        Assert.Contains($"0x{LumaR8:X8}", error.Message, StringComparison.Ordinal);
        Assert.Contains("0x30323149", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Surface_ComposedSingleLayer_ThrowsInsteadOfPresentingHalfAFrame()
        => Assert.Throws<InvalidDataException>(() => new DmaBufSurface(Descriptor(1, LumaR8, ChromaGr88)));
}