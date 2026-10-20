using UotanStudio.Media.Decoding;
using UotanStudio.Media.Decoding.GStreamer;
using UotanStudio.Media.GStreamer;
using Xunit;

namespace UotanStudio.Tests.Media;

public sealed class GStreamerPipelineShapeTests
{
    private const string Parser = "h264parse";
    private const string Element = "avdec_h264";
    private const string InputCaps = "video/x-h264,stream-format=byte-stream,alignment=au,width=640,height=480";
    private const string OutputCaps = "video/x-raw,format=NV12";
    private const string SinkTail = "appsink name=output sync=false max-buffers=2 drop=true enable-last-sample=false";

    private static string Build(GStreamerPlanePath path)
        => GStreamerVideoDecoder.BuildPipeline(Parser, Element, InputCaps, OutputCaps, path);

    private static int Count(string haystack, string needle)
    {
        var total = 0;
        for (var index = haystack.IndexOf(needle, StringComparison.Ordinal); index >= 0; index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
            total++;
        return total;
    }

    [Fact]
    public void BuildPipeline_DirectNv12_NegotiatesOnDecoderPadWithoutConverter()
    {
        var description = Build(GStreamerPlanePath.DirectNv12);
        Assert.Contains($"! {Parser} ! {Element} ! {OutputCaps} !", description, StringComparison.Ordinal);
        Assert.DoesNotContain("videoconvert", description, StringComparison.Ordinal);
        Assert.DoesNotContain("video/x-raw !", description, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPipeline_CpuConverter_RetainsBareRawCapsAndExactlyOneConverterStage()
    {
        var description = Build(GStreamerPlanePath.CpuConverter);
        Assert.Contains($"! {Element} ! video/x-raw ! videoconvert ! {OutputCaps} !", description, StringComparison.Ordinal);
        Assert.Equal(1, Count(description, "videoconvert"));
    }

    [Fact]
    public void BuildPipeline_BothPaths_ShareBoundedAppsrcPrefixAndAppsinkSuffix()
    {
        var direct = Build(GStreamerPlanePath.DirectNv12);
        var converter = Build(GStreamerPlanePath.CpuConverter);
        var prefix = $"appsrc name=input is-live=true format=time block=false max-bytes={GstPipeline.MaximumQueuedBytes} caps={InputCaps}";
        Assert.StartsWith(prefix, direct, StringComparison.Ordinal);
        Assert.StartsWith(prefix, converter, StringComparison.Ordinal);
        Assert.EndsWith($"! {SinkTail}", direct, StringComparison.Ordinal);
        Assert.EndsWith($"! {SinkTail}", converter, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPipeline_EmptyColorimetryOverride_KeepsPlainNv12Caps()
    {
        Assert.DoesNotContain("colorimetry", Build(GStreamerPlanePath.DirectNv12), StringComparison.Ordinal);
    }

    [Fact]
    public void RequireNv12_PlanesTheCopierUnderstands_Accepts()
        => GStreamerVideoDecoder.RequireNv12("NV12");

    [Theory]
    [InlineData("I420")]
    [InlineData("BGRA")]
    [InlineData("NV21")]
    [InlineData(null)]
    public void RequireNv12_OtherLayout_ThrowsWithObservedFormat(string? format)
    {
        var error = Assert.Throws<InvalidDataException>(() => GStreamerVideoDecoder.RequireNv12(format));
        Assert.Contains(format ?? "(no format field)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaneDescription_HardwareBeforeProbe_ReportsCpuPlanes()
    {
        using var decoder = new GStreamerVideoDecoder(hardware: true);
        Assert.Equal("hardware, CPU planes", decoder.PlaneDescription);
    }

    [Fact]
    public void PlaneDescription_SoftwareBeforeProbe_ReportsSoftware()
    {
        using var decoder = new GStreamerVideoDecoder(hardware: false);
        Assert.Equal("software", decoder.PlaneDescription);
    }

    [Fact]
    public void GetBackendName_GStreamerDecoder_NeverClaimsGpuResidentPlanes()
    {
        using var hardware = new GStreamerVideoDecoder(hardware: true);
        using var software = new GStreamerVideoDecoder(hardware: false);
        Assert.DoesNotContain("GPU", VideoDecoderFactory.GetBackendName(hardware), StringComparison.Ordinal);
        Assert.DoesNotContain("GPU", VideoDecoderFactory.GetBackendName(software), StringComparison.Ordinal);
    }
}