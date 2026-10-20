using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace UotanStudio.Media.Decoding;

/// <summary>
/// Converts NV12 frames to 32-bit BGRA pixel data for rendering.
/// <para>将 NV12 帧转换为 32 位 BGRA 像素数据以供渲染。</para>
/// </summary>
/// <remarks>
/// <para>
/// Coefficient selection follows the explicit matrix and range arguments; the defaults are
/// BT.601 limited-range. The converter never infers color metadata from a codec, and BT.2020
/// is not among the coefficient sets. Chroma is sampled bilinearly in both axes at half
/// resolution, matching the GPU shader path.
/// <para>
/// 系数选择遵循显式的矩阵与范围参数；默认 BT.601 有限范围。转换器不会从编码推断色彩元数据，
/// BT.2020 不在系数集之内。色度在半分辨率上按双轴双线性采样，与 GPU 着色器路径一致。
/// </para>
/// </para>
/// </remarks>
public static unsafe class Nv12ToBgraConverter
{
    /// <summary>Computes the required BGRA buffer size for a frame.
    /// <para>计算一帧所需的 BGRA 缓冲区大小。</para></summary>
    /// <param name="width">Frame width in pixels.<para>帧宽度（像素）。</para></param>
    /// <param name="height">Frame height in pixels.<para>帧高度（像素）。</para></param>
    /// <returns>The required buffer size in bytes, that is width × height × 4.
    /// <para>所需缓冲区大小（字节），即宽度 × 高度 × 4。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> is not positive.
    /// <para><paramref name="width"/> 或 <paramref name="height"/> 不是正数。</para></exception>
    /// <exception cref="OverflowException">The product of width, height and 4 exceeds <see cref="int"/>.
    /// <para>宽度、高度与 4 的乘积超出 <see cref="int"/> 范围。</para></exception>
    public static int GetBgraBufferSize(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Frame dimensions must be positive. 帧尺寸必须为正。");
        }

        return checked(width * height * 4);
    }

    /// <summary>Converts an NV12 frame to BGRA. Destination must be at least <see cref="GetBgraBufferSize"/> bytes.
    /// <para>将 NV12 帧转换为 BGRA。目标缓冲区不得小于 <see cref="GetBgraBufferSize"/> 字节。</para></summary>
    /// <param name="nv12">The NV12 source frame, covering both planes at the given stride.
    /// <para>NV12 源帧，须按给定行距完整覆盖两个平面。</para></param>
    /// <param name="width">Frame width in pixels.<para>帧宽度（像素）。</para></param>
    /// <param name="height">Frame height in pixels.<para>帧高度（像素）。</para></param>
    /// <param name="nv12Stride">Row pitch in bytes of the NV12 source; must be at least the width.
    /// <para>NV12 源数据的行距（字节），不得小于宽度。</para></param>
    /// <param name="destination">The BGRA destination buffer, written row by row at <paramref name="destinationStride"/>.
    /// <para>BGRA 目标缓冲区，按 <paramref name="destinationStride"/> 逐行写入。</para></param>
    /// <param name="destinationStride">Row pitch in bytes of the destination; pass <c>0</c> to default to width × 4 and must never be less than width × 4.
    /// <para>目标缓冲区的行距（字节）；传 <c>0</c> 表示默认宽度 × 4，且不得小于宽度 × 4。</para></param>
    /// <param name="matrix">YUV matrix selecting the BT.601 or BT.709 coefficient set.
    /// <para>YUV 色彩矩阵，用于选择 BT.601 或 BT.709 系数组。</para></param>
    /// <param name="range">YUV range selecting limited (offset 16, scale 298) or full (offset 0, scale 256) luma handling.
    /// <para>YUV 数值范围，选择有限（偏移 16、缩放 298）或全量（偏移 0、缩放 256）亮度处理。</para></param>
    /// <exception cref="ArgumentOutOfRangeException">Dimensions are not positive, <paramref name="nv12Stride"/> is below the width, or <paramref name="destinationStride"/> is below width × 4.
    /// <para>尺寸非正数、<paramref name="nv12Stride"/> 小于宽度，或 <paramref name="destinationStride"/> 小于宽度 × 4。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="nv12"/> is shorter than the two stride-sized planes, or <paramref name="destination"/> cannot hold every row.
    /// <para><paramref name="nv12"/> 短于按行距计算的两个平面，或 <paramref name="destination"/> 无法容纳全部行。</para></exception>
    /// <exception cref="OverflowException">A stride or size computation exceeds <see cref="int"/>.
    /// <para>行距或尺寸计算超出 <see cref="int"/>。</para></exception>
    public static void Convert(ReadOnlySpan<byte> nv12, int width, int height, int nv12Stride, Span<byte> destination, int destinationStride = 0, VideoColorMatrix matrix = VideoColorMatrix.Bt601, VideoColorRange range = VideoColorRange.Limited)
    {
        if (destinationStride == 0)
        {
            destinationStride = checked(width * 4);
        }

        Validate(width, height, nv12Stride, destination.Length, destinationStride);
        var sourceLength = VideoFrameLimits.Nv12Length(width, height, nv12Stride);
        if (nv12.Length < sourceLength)
        {
            throw new ArgumentException("The source does not contain complete NV12 planes.", nameof(nv12));
        }

        fixed (byte* src = nv12)
        fixed (byte* dst = destination)
        {
            ConvertCore(src, dst, width, height, nv12Stride, destinationStride, matrix, range);
        }
    }

    /// <summary>Span overload taking a <see cref="DecodedVideoFrame"/> directly, converted with the default BT.601 limited-range coefficients.
    /// <para>直接接收 <see cref="DecodedVideoFrame"/> 的 Span 重载，按默认的 BT.601 有限范围系数转换。</para></summary>
    /// <param name="frame">Decoded NV12 frame supplying pixels, dimensions and stride.
    /// <para>提供像素、尺寸与行距的已解码 NV12 帧。</para></param>
    /// <param name="destination">The BGRA destination buffer.<para>BGRA 目标缓冲区。</para></param>
    /// <param name="destinationStride">Row pitch in bytes of the destination; pass <c>0</c> to default to width × 4.
    /// <para>目标缓冲区的行距（字节）；传 <c>0</c> 表示默认宽度 × 4。</para></param>
    /// <exception cref="ArgumentException">The frame span or the destination is too small for the computed layout, or the frame does not carry <see cref="VideoPixelFormat.Nv12"/> planes.
    /// <para>帧跨度或目标缓冲区小于按布局计算所需的大小，或该帧并非 <see cref="VideoPixelFormat.Nv12"/> 平面布局。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The frame dimensions or strides violate the converter bounds.
    /// <para>帧尺寸或行距超出转换器的边界约束。</para></exception>
    public static void Convert(in DecodedVideoFrame frame, Span<byte> destination, int destinationStride = 0)
    {
        if (frame.PixelFormat != VideoPixelFormat.Nv12)
        {
            throw new ArgumentException($"The converter reads NV12 planes; the frame carries {frame.PixelFormat}.", nameof(frame));
        }

        Convert(frame.Span, frame.Width, frame.Height, frame.Stride, destination, destinationStride);
    }

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
    private static void ConvertCore(byte* src, byte* dst, int width, int height, int nv12Stride, int dstStride, VideoColorMatrix matrix, VideoColorRange range)
    {
        var coefficients = Nv12ConversionTable.For(matrix, range);
        int yOffset = coefficients.LumaOffset, yScale = coefficients.LumaScale;
        var red = coefficients.RedFromChromaRed;
        var greenU = -coefficients.GreenFromChromaBlue;
        var greenV = -coefficients.GreenFromChromaRed;
        var blue = coefficients.BlueFromChromaBlue;
        var yPlane = src;
        var uvPlane = src + (long)nv12Stride * height;
        var uvStride = nv12Stride;
        var yOffset128 = Vector128.Create(yOffset);
        var yScale128 = Vector128.Create(yScale);
        var red128 = Vector128.Create(red);
        var greenU128 = Vector128.Create(greenU);
        var greenV128 = Vector128.Create(greenV);
        var blue128 = Vector128.Create(blue);
        var yOffset256 = Vector256.Create(yOffset);
        var yScale256 = Vector256.Create(yScale);
        var red256 = Vector256.Create(red);
        var greenU256 = Vector256.Create(greenU);
        var greenV256 = Vector256.Create(greenV);
        var blue256 = Vector256.Create(blue);
        var bias128 = Vector128.Create(128);
        var bias256 = Vector256.Create(128);
        var alpha256 = Vector256.Create(unchecked((int)0xff000000));

        for (var row = 0; row < height; row++)
        {
            var yRow = yPlane + (long)row * nv12Stride;
            var chromaRow = row >> 1;
            var uvRow = uvPlane + (long)chromaRow * uvStride;
            var uvRowNext = uvRow + ((chromaRow + 1 < (height + 1) >> 1) ? uvStride : 0);
            var verticalWeight = (row & 1) != 0 ? 1 : 0;
            var dstRow = dst + (long)row * dstStride;

            var col = 0;
            if (Avx2.IsSupported)
            {
                for (; col <= width - 8; col += 8)
                {
                    var y = Avx2.ConvertToVector256Int32(Unsafe.ReadUnaligned<Vector128<byte>>(yRow + col));
                    var cell = col >> 1;
                    var d0 = SampleChroma(uvRow, uvRowNext, cell, width, verticalWeight) - 128;
                    var e0 = SampleChroma(uvRow + 1, uvRowNext + 1, cell, width, verticalWeight) - 128;
                    var d1 = SampleChroma(uvRow, uvRowNext, cell + 1, width, verticalWeight) - 128;
                    var e1 = SampleChroma(uvRow + 1, uvRowNext + 1, cell + 1, width, verticalWeight) - 128;
                    var d2 = SampleChroma(uvRow, uvRowNext, cell + 2, width, verticalWeight) - 128;
                    var e2 = SampleChroma(uvRow + 1, uvRowNext + 1, cell + 2, width, verticalWeight) - 128;
                    var d3 = SampleChroma(uvRow, uvRowNext, cell + 3, width, verticalWeight) - 128;
                    var e3 = SampleChroma(uvRow + 1, uvRowNext + 1, cell + 3, width, verticalWeight) - 128;
                    var d = Vector256.Create(d0, d0, d1, d1, d2, d2, d3, d3);
                    var e = Vector256.Create(e0, e0, e1, e1, e2, e2, e3, e3);
                    var c = (y - yOffset256) * yScale256 + bias256;
                    var r = Vector256.ShiftRightArithmetic(c + e * red256, 8);
                    var g = Vector256.ShiftRightArithmetic(c - d * greenU256 - e * greenV256, 8);
                    var b = Vector256.ShiftRightArithmetic(c + d * blue256, 8);
                    r = Vector256.Min(Vector256.Max(r, Vector256<int>.Zero), Vector256.Create(255));
                    g = Vector256.Min(Vector256.Max(g, Vector256<int>.Zero), Vector256.Create(255));
                    b = Vector256.Min(Vector256.Max(b, Vector256<int>.Zero), Vector256.Create(255));
                    var pixels = b | Vector256.ShiftLeft(g, 8) | Vector256.ShiftLeft(r, 16) | alpha256;
                    pixels.Store((int*)(dstRow + col * 4));
                }
            }

            if (Vector128.IsHardwareAccelerated)
            {
                for (; col <= width - 4; col += 4)
                {
                    var y = Vector128.Create((int)yRow[col], yRow[col + 1], yRow[col + 2], yRow[col + 3]);
                    var cell = col >> 1;
                    var d0 = SampleChroma(uvRow, uvRowNext, cell, width, verticalWeight) - 128;
                    var e0 = SampleChroma(uvRow + 1, uvRowNext + 1, cell, width, verticalWeight) - 128;
                    var d1 = SampleChroma(uvRow, uvRowNext, cell + 1, width, verticalWeight) - 128;
                    var e1 = SampleChroma(uvRow + 1, uvRowNext + 1, cell + 1, width, verticalWeight) - 128;
                    var d = Vector128.Create(d0, d0, d1, d1);
                    var e = Vector128.Create(e0, e0, e1, e1);
                    var c = (y - yOffset128) * yScale128 + bias128;
                    var r = Vector128.ShiftRightArithmetic(c + e * red128, 8);
                    var g = Vector128.ShiftRightArithmetic(c - d * greenU128 - e * greenV128, 8);
                    var b = Vector128.ShiftRightArithmetic(c + d * blue128, 8);
                    r = Vector128.Min(Vector128.Max(r, Vector128<int>.Zero), Vector128.Create(255));
                    g = Vector128.Min(Vector128.Max(g, Vector128<int>.Zero), Vector128.Create(255));
                    b = Vector128.Min(Vector128.Max(b, Vector128<int>.Zero), Vector128.Create(255));
                    var pixels = b | Vector128.ShiftLeft(g, 8) | Vector128.ShiftLeft(r, 16) | Vector128.Create(unchecked((int)0xff000000));
                    pixels.Store((int*)(dstRow + col * 4));
                }
            }

            for (; col < width; col += 2)
            {
                var cell = col >> 1;
                var d = SampleChroma(uvRow, uvRowNext, cell, width, verticalWeight) - 128;
                var e = SampleChroma(uvRow + 1, uvRowNext + 1, cell, width, verticalWeight) - 128;

                for (var pixel = col; pixel < Math.Min(col + 2, width); pixel++)
                {
                    var c = yRow[pixel] - yOffset;

                    var r = (yScale * c + red * e + 128) >> 8;
                    var g = (yScale * c - greenU * d - greenV * e + 128) >> 8;
                    var b = (yScale * c + blue * d + 128) >> 8;

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

    /// <summary>
    /// Samples one chroma channel bilinearly for the pixel pair at <paramref name="cell"/>:
    /// horizontal 3:1 weighting toward the earlier column and, on odd luma rows, a 1:1 vertical
    /// blend of the two chroma rows; the last chroma column clamps to itself.
    /// <para>按双线性采样 <paramref name="cell"/> 处像素对的一个色度通道：水平按 3:1 加权靠前
    /// 一列，奇数亮度行再把两个色度行按 1:1 混合；最后一个色度列钳制到自身。</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SampleChroma(byte* uvRow, byte* uvRowNext, int cell, int width, int verticalWeight)
    {
        var nextCell = cell + 1 < (width + 1) >> 1 ? cell + 1 : cell;
        var upper = (uvRow[cell * 2] * 3 + uvRow[nextCell * 2] + 2) >> 2;
        var lower = (uvRowNext[cell * 2] * 3 + uvRowNext[nextCell * 2] + 2) >> 2;
        return verticalWeight != 0 ? (upper + lower + 1) >> 1 : upper;
    }
}