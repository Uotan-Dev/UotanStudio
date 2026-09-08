using UotanStudio.Media.Decoding;
using Xunit;

namespace UotanStudio.Tests.Media;

/// <summary>
/// 解码抽象与工厂的平台行为测试（非 Windows 平台断言跳过）。
/// </summary>
public class VideoDecoderFactoryTests
{
    [Fact]
    public void IsSupported_OnWindows_AcceptsH264()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // 非 Windows 平台不支持任何后端，断言相反结果。
        }

        Assert.True(VideoDecoderFactory.IsSupported(VideoCodecId.H264));
    }

    [Fact]
    public void IsSupported_H265_DependsOnPlatform()
    {
        // H.265 需要 HEVC 扩展/驱动，工厂不在此层静态承诺支持。
        // 非 Windows 平台必须返回 false。
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(VideoDecoderFactory.IsSupported(VideoCodecId.H265));
        }
    }

    [Fact]
    public void Create_OnNonWindows_ThrowsNotSupported()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Throws<NotSupportedException>(() => VideoDecoderFactory.Create());
    }

    [Fact]
    public void Create_OnWindows_ReturnsInitializedFalseDecoder()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var decoder = VideoDecoderFactory.Create();
        Assert.False(decoder.IsInitialized);
    }

    [Fact]
    public void DecodedVideoFrame_RejectsInvalidArguments()
    {
        var buffer = new byte[64];

        Assert.Throws<ArgumentNullException>(() => new DecodedVideoFrame(null!, 64, 4, 4, 4, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecodedVideoFrame(buffer, 128, 4, 4, 4, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecodedVideoFrame(buffer, 64, 0, 4, 4, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecodedVideoFrame(buffer, 64, 4, 4, 2, 0));
    }

    [Fact]
    public void DecodedVideoFrame_AfterDispose_ThrowsObjectDisposed()
    {
        var pooled = System.Buffers.ArrayPool<byte>.Shared.Rent(64);
        var frame = new DecodedVideoFrame(pooled, 64, 4, 4, 4, 0);
        frame.Dispose();
        frame.Dispose(); // 幂等。

        Assert.Throws<ObjectDisposedException>(() => frame.Buffer);
    }
}
