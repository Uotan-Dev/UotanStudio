using System.Runtime.CompilerServices;

namespace UotanStudio.Media.Decoding;

/// <summary>
/// Converts NV12 frames to 32-bit BGRA pixel data for rendering.
/// <para>将 NV12 帧转换为 32 位 BGRA 像素数据以供渲染。</para>
/// </summary>
/// <remarks>
/// <para>
/// The conversion uses the BT.601 limited-range coefficients that match what
/// Android screen encoders emit for scrcpy-style streams. Chroma is upsampled
/// with bilinear-free nearest sampling at half resolution (each UV byte pair
/// covers a 2×2 pixel block), which is the standard fast path for video
/// content and keeps the loop allocation-free.
/// </para>
/// <para>
/// 转换使用 BT.601 有限范围系数，与 scrcpy 类码流中 Android 屏幕编码器的输出
/// 一致。色度按半分辨率最近邻上采样（每对 UV 字节覆盖 2×2 像素块），这是
/// 视频内容的标准快速路径，且循环全程无分配。
/// </para>
/// </remarks>
public static unsafe class Nv12ToBgraConverter
{
    /// <summary>Computes the required BGRA buffer size for a frame.</summary>
    /// <summary>计算一帧所需的 BGRA 缓冲区大小。</summary>
    /// <param name="width">Frame width in pixels. 帧宽度（像素）。</param>
    /// <param name="height">Frame height in pixels. 帧高度（像素）。</param>
    /// <returns>The required buffer size in bytes. 所需缓冲区大小（字节）。</returns>
    public static int GetBgraBufferSize(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Frame dimensions must be positive. 帧尺寸必须为正。");
        }

        return checked(width * height * 4);
    }

    /// <summary>
    /// Converts an NV12 frame to BGRA. Destination must be at least
    /// <see cref="GetBgraBufferSize"/> bytes.
    /// </summary>
    /// <summary>
    /// 将 NV12 帧转换为 BGRA。目标缓冲区不得小于 <see cref="GetBgraBufferSize"/> 字节。
    /// </summary>
    /// <param name="nv12">The NV12 source frame. NV12 源帧。</param>
    /// <param name="width">Frame width in pixels. 帧宽度（像素）。</param>
    /// <param name="height">Frame height in pixels. 帧高度（像素）。</param>
    /// <param name="nv12Stride">Row pitch in bytes of the NV12 source. NV12 源数据的行距（字节）。</param>
    /// <param name="destination">The BGRA destination buffer. BGRA 目标缓冲区。</param>
    /// <param name="destinationStride">
    /// Row pitch in bytes of the destination (defaults to width × 4).
    /// 目标缓冲区的行距（字节），默认为宽度 × 4。
    /// </param>
    public static void Convert(ReadOnlySpan<byte> nv12, int width, int height, int nv12Stride, Span<byte> destination, int destinationStride = 0)
    {
        if (destinationStride == 0)
        {
            destinationStride = checked(width * 4);
        }

        Validate(width, height, nv12Stride, destination.Length, destinationStride);

        fixed (byte* src = nv12)
        fixed (byte* dst = destination)
        {
            ConvertCore(src, dst, width, height, nv12Stride, destinationStride);
        }
    }

    /// <summary>Span overload taking a <see cref="DecodedVideoFrame"/> directly.</summary>
    /// <summary>直接接收 <see cref="DecodedVideoFrame"/> 的 Span 重载。</summary>
    public static void Convert(in DecodedVideoFrame frame, Span<byte> destination, int destinationStride = 0)
        => Convert(frame.Span, frame.Width, frame.Height, frame.Stride, destination, destinationStride);

    private static void Validate(int width, int height, int nv12Stride, int destinationLength, int destinationStride)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Frame dimensions must be positive. 帧尺寸必须为正。");
        }

        if (nv12Stride < width)
        {
            throw new ArgumentOutOfRangeException(nameof(nv12Stride), "NV12 stride must be at least the width. NV12 行距不得小于宽度。");
        }

        if (destinationStride < checked(width * 4))
        {
            throw new ArgumentOutOfRangeException(nameof(destinationStride), "Destination stride must be at least width × 4. 目标行距不得小于宽度 × 4。");
        }

        var required = checked((height - 1) * destinationStride) + checked(width * 4);
        if (destinationLength < required)
        {
            throw new ArgumentException(
                $"Destination buffer too small: requires {required} bytes. 目标缓冲区过小：需要 {required} 字节。",
                nameof(destinationLength));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ConvertCore(byte* src, byte* dst, int width, int height, int nv12Stride, int dstStride)
    {
        var yPlane = src;
        var uvPlane = src + (long)nv12Stride * height;
        var uvStride = nv12Stride;

        for (var row = 0; row < height; row++)
        {
            var yRow = yPlane + (long)row * nv12Stride;
            var uvRow = uvPlane + (long)(row >> 1) * uvStride;
            var dstRow = dst + (long)row * dstStride;

            for (var col = 0; col < width; col += 2)
            {
                // Each 2×2 pixel block shares one UV pair; every pixel uses its
                // own luma value.
                // 每个 2×2 像素块共享一对 UV；每个像素使用自己的亮度值。
                var uvIndex = col & ~1;
                var u = uvRow[uvIndex];
                var v = uvRow[uvIndex + 1];
                var d = u - 128;
                var e = v - 128;

                for (var pixel = col; pixel < Math.Min(col + 2, width); pixel++)
                {
                    var c = yRow[pixel] - 16;

                    // BT.601 limited range: Y in [16,235], UV in [16,240] scaled by 1/256.
                    // BT.601 有限范围：Y ∈ [16,235]，UV ∈ [16,240]，按 1/256 缩放。
                    var r = (298 * c + 409 * e + 128) >> 8;
                    var g = (298 * c - 100 * d - 208 * e + 128) >> 8;
                    var b = (298 * c + 516 * d + 128) >> 8;

                    dstRow[pixel * 4 + 0] = Clamp(b);
                    dstRow[pixel * 4 + 1] = Clamp(g);
                    dstRow[pixel * 4 + 2] = Clamp(r);
                    dstRow[pixel * 4 + 3] = 255;
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Clamp(int value)
        => (byte)(value < 0 ? 0 : (value > 255 ? 255 : value));
}
