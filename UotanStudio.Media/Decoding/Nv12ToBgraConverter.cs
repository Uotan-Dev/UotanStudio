using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace UotanStudio.Media.Decoding;

/// <summary>
/// Converts NV12 frames to 32-bit BGRA pixel data for rendering.
/// <para>将 NV12 帧转换为 32 位 BGRA 像素数据以供渲染。</para>
/// </summary>
/// <remarks>
/// <para>
/// The conversion uses BT.601 limited-range coefficients. It does not infer color
/// metadata from a codec and must not be used as a BT.709/BT.2020 or full-range converter.
/// Chroma uses nearest-neighbour sampling at half resolution.
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
        int sourceLength = VideoFrameLimits.Nv12Length(width, height, nv12Stride);
        if (nv12.Length < sourceLength)
        {
            throw new ArgumentException("The source does not contain complete NV12 planes.", nameof(nv12));
        }


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

            int col = 0;
            if (Vector128.IsHardwareAccelerated)
            {
                // Four pixels share two chroma pairs. Keep exact integer rounding
                // and saturate before packing BGRA; no temporary buffers or allocations.
                for (; col <= width - 4; col += 4)
                {
                    var y = Vector128.Create((int)yRow[col], yRow[col + 1], yRow[col + 2], yRow[col + 3]);
                    var d = Vector128.Create((int)uvRow[col], uvRow[col], uvRow[col + 2], uvRow[col + 2]) - Vector128.Create(128);
                    var e = Vector128.Create((int)uvRow[col + 1], uvRow[col + 1], uvRow[col + 3], uvRow[col + 3]) - Vector128.Create(128);
                    var c = (y - Vector128.Create(16)) * Vector128.Create(298) + Vector128.Create(128);
                    var r = Vector128.ShiftRightArithmetic(c + e * Vector128.Create(409), 8);
                    var g = Vector128.ShiftRightArithmetic(c - d * Vector128.Create(100) - e * Vector128.Create(208), 8);
                    var b = Vector128.ShiftRightArithmetic(c + d * Vector128.Create(516), 8);
                    r = Vector128.Min(Vector128.Max(r, Vector128<int>.Zero), Vector128.Create(255));
                    g = Vector128.Min(Vector128.Max(g, Vector128<int>.Zero), Vector128.Create(255));
                    b = Vector128.Min(Vector128.Max(b, Vector128<int>.Zero), Vector128.Create(255));
                    var pixels = b | Vector128.ShiftLeft(g, 8) | Vector128.ShiftLeft(r, 16) | Vector128.Create(unchecked((int)0xff000000));
                    pixels.Store((int*)(dstRow + col * 4));
                }
            }

            for (; col < width; col += 2)
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
