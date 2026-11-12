using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using ArmSve = System.Runtime.Intrinsics.Arm.Sve;

#pragma warning disable SYSLIB5003
namespace UotanStudio.Hash.Simd;

/// <summary>
/// Byte-block XOR and additive-sum primitives dispatched across AVX-512, AVX10, AVX2, SSE2, ARM SVE, AdvSIMD and
/// portable vectors, with a scalar software fallback on every processor.
/// <para>在 AVX-512、AVX10、AVX2、SSE2、ARM SVE、AdvSIMD 与可移植向量之间分派的字节块异或及累加原语，
/// 并在所有处理器上提供标量软件回退。</para>
/// </summary>
/// <remarks>
/// The widest kernel the processor and runtime can execute is selected once. Every kernel consumes whole fixed-width
/// blocks and hands the trailing bytes to the same scalar loop, so results are bit-identical across all kernels, on
/// processors without any vector ISA, and when <see cref="ForceSoftwareFallback"/> is set. ARM SVE uses the
/// runtime-determined <see cref="Vector{T}"/> width instead of a fixed register width. ARM SVE intrinsics remain
/// experimental in the base class library, so SYSLIB5003 is suppressed locally in this file.
/// <para>处理器与运行时能执行的最宽内核只在类型初始化时选择一次。每个内核消费完整的定宽块，尾部字节交给同一个标量循环，
/// 因此在任意内核、无向量指令集的处理器以及设置 <see cref="ForceSoftwareFallback"/> 时结果都逐位一致。ARM SVE 使用由运行时
/// 决定的 <see cref="Vector{T}"/> 宽度而非固定寄存器宽度。基础类库仍将 ARM SVE intrinsic 标记为实验性，
/// 故本文件在本地抑制 SYSLIB5003。</para>
/// </remarks>
public static class SimdOps
{
    /// <summary>
    /// Names the concrete implementation behind a byte-block operation.
    /// <para>标识字节块运算背后的具体实现。</para>
    /// </summary>
    internal enum SimdKernel
    {
        /// <summary>Portable byte-at-a-time loop.<para>可移植的逐字节循环。</para></summary>
        Software,

        /// <summary>Runtime-sized <see cref="Vector{T}"/> loop.<para>运行时定宽的 <see cref="Vector{T}"/> 循环。</para></summary>
        PortableVectors,

        /// <summary>Fixed 128-bit register loop.<para>固定 128 位寄存器循环。</para></summary>
        Vector128,

        /// <summary>Fixed 256-bit register loop.<para>固定 256 位寄存器循环。</para></summary>
        Vector256,

        /// <summary>Fixed 512-bit register loop.<para>固定 512 位寄存器循环。</para></summary>
        Vector512,

        /// <summary>ARM scalable-vector loop.<para>ARM 可伸缩向量循环。</para></summary>
        Sve,
    }

    [ThreadStatic]
    private static bool s_forceSoftwareFallback;

    private static readonly SimdKernel SelectedKernel = SelectKernel();

    /// <summary>
    /// Gets or sets a per-thread switch that forces the scalar software fallback regardless of the detected hardware.
    /// <para>获取或设置一个线程级开关，无论检测到何种硬件都强制使用标量软件回退。</para>
    /// </summary>
    /// <remarks>
    /// The switch is thread-local so that parallel test collections and worker threads cannot change each other's
    /// dispatch; callers that enable it must restore it in a <c>finally</c> block.
    /// <para>该开关是线程本地的，因此并行测试集合与工作线程不会改变彼此的分派；启用方必须在 <c>finally</c> 中恢复。</para>
    /// </remarks>
    public static bool ForceSoftwareFallback
    {
        get => s_forceSoftwareFallback;
        set => s_forceSoftwareFallback = value;
    }

    /// <summary>
    /// Gets the kernel selected at type initialization for this processor.
    /// <para>获取类型初始化时为本处理器选定的内核。</para>
    /// </summary>
    internal static SimdKernel ActiveKernel => SelectedKernel;

    /// <summary>
    /// XORs <paramref name="source"/> into <paramref name="destination"/> in place using the widest available kernel.
    /// <para>使用最宽的可用内核把 <paramref name="source"/> 就地异或进 <paramref name="destination"/>。</para>
    /// </summary>
    /// <param name="destination">Buffer to update in place.<para>就地更新的缓冲区。</para></param>
    /// <param name="source">Bytes to XOR in; must have the same length as <paramref name="destination"/>.
    /// <para>参与异或的字节；长度必须与 <paramref name="destination"/> 相同。</para></param>
    /// <exception cref="ArgumentException">The two spans have different lengths.<para>两个区间长度不同。</para></exception>
    public static void XorInPlace(Span<byte> destination, ReadOnlySpan<byte> source)
    {
        if (destination.Length != source.Length)
        {
            throw new ArgumentException("The source and destination must have equal lengths. 源与目标长度必须一致。", nameof(source));
        }

        XorKernel(destination, source, CurrentKernel());
    }

    /// <summary>
    /// XORs a single repeated byte into <paramref name="destination"/> in place using the widest available kernel.
    /// <para>使用最宽的可用内核把一个重复字节就地异或进 <paramref name="destination"/>。</para>
    /// </summary>
    /// <param name="destination">Buffer to update in place.<para>就地更新的缓冲区。</para></param>
    /// <param name="value">Byte XORed into every position.<para>异或到每个位置的字节。</para></param>
    public static void XorConstantInPlace(Span<byte> destination, byte value)
        => XorConstantKernel(destination, value, CurrentKernel());

    /// <summary>
    /// Adds every byte modulo 2^32 using the widest available kernel.
    /// <para>使用最宽的可用内核按模 2^32 累加每个字节。</para>
    /// </summary>
    /// <param name="data">Bytes to sum; an empty span returns <c>0</c>.<para>待累加的字节；空区间返回 <c>0</c>。</para></param>
    /// <returns>The sum modulo 2^32.<para>按模 2^32 得到的和。</para></returns>
    public static uint SumBytes(ReadOnlySpan<byte> data)
        => SumBytesKernel(data, CurrentKernel());

    /// <summary>
    /// XOR-folds every native 16-bit word of <paramref name="words"/> into one accumulator using the widest available
    /// kernel.
    /// <para>使用最宽的可用内核把 <paramref name="words"/> 中的每个本机 16 位字异或折叠成一个累加值。</para>
    /// </summary>
    /// <remarks>
    /// The fold is endianness-neutral over native words: callers that need little-endian words must first cast the
    /// bytes on a little-endian processor.
    /// <para>折叠对本机字而言与字节序无关：需要小端字的调用方必须先在小端处理器上完成字节重解释。</para>
    /// </remarks>
    /// <param name="words">Native 16-bit words to fold; an empty span returns <c>0</c>.
    /// <para>待折叠的本机 16 位字；空区间返回 <c>0</c>。</para></param>
    /// <returns>The XOR reduction of all words.<para>全部字的异或归约值。</para></returns>
    public static ushort XorFoldWords(ReadOnlySpan<ushort> words)
        => XorFoldWordsKernel(words, CurrentKernel());

    /// <summary>
    /// XORs two equal-length spans through one specific kernel, falling back to the scalar loop when that kernel is
    /// unavailable on the running processor.
    /// <para>用指定内核异或两个等长区间；该内核在本机不可用时回退到标量循环。</para>
    /// </summary>
    /// <param name="destination">Buffer to update in place.<para>就地更新的缓冲区。</para></param>
    /// <param name="source">Bytes to XOR in; must have the same length as <paramref name="destination"/>.
    /// <para>参与异或的字节；长度必须与 <paramref name="destination"/> 相同。</para></param>
    /// <param name="kernel">Kernel to exercise.<para>要执行的内核。</para></param>
    internal static void XorKernel(Span<byte> destination, ReadOnlySpan<byte> source, SimdKernel kernel)
    {
        switch (kernel)
        {
            case SimdKernel.Sve:
                XorSve(destination, source);
                break;
            case SimdKernel.Vector512:
                XorVector512(destination, source);
                break;
            case SimdKernel.Vector256:
                XorVector256(destination, source);
                break;
            case SimdKernel.Vector128:
                XorVector128(destination, source);
                break;
            case SimdKernel.PortableVectors:
                XorPortableVectors(destination, source);
                break;
            default:
                XorSoftware(destination, source);
                break;
        }
    }

    /// <summary>
    /// XORs a repeated byte through one specific kernel, falling back to the scalar loop when that kernel is
    /// unavailable on the running processor.
    /// <para>用指定内核异或重复字节；该内核在本机不可用时回退到标量循环。</para>
    /// </summary>
    /// <param name="destination">Buffer to update in place.<para>就地更新的缓冲区。</para></param>
    /// <param name="value">Byte XORed into every position.<para>异或到每个位置的字节。</para></param>
    /// <param name="kernel">Kernel to exercise.<para>要执行的内核。</para></param>
    internal static void XorConstantKernel(Span<byte> destination, byte value, SimdKernel kernel)
    {
        switch (kernel)
        {
            case SimdKernel.Sve:
                XorConstantSve(destination, value);
                break;
            case SimdKernel.Vector512:
                XorConstantVector512(destination, value);
                break;
            case SimdKernel.Vector256:
                XorConstantVector256(destination, value);
                break;
            case SimdKernel.Vector128:
                XorConstantVector128(destination, value);
                break;
            case SimdKernel.PortableVectors:
                XorConstantPortableVectors(destination, value);
                break;
            default:
                XorConstantSoftware(destination, value);
                break;
        }
    }

    /// <summary>
    /// Sums bytes through one specific kernel, falling back to the scalar loop when that kernel is unavailable.
    /// <para>用指定内核累加字节；该内核不可用时回退到标量循环。</para>
    /// </summary>
    /// <param name="data">Bytes to sum.<para>待累加的字节。</para></param>
    /// <param name="kernel">Kernel to exercise.<para>要执行的内核。</para></param>
    /// <returns>The sum modulo 2^32.<para>按模 2^32 得到的和。</para></returns>
    internal static uint SumBytesKernel(ReadOnlySpan<byte> data, SimdKernel kernel) => kernel switch
    {
        SimdKernel.Vector512 => SumBytesVector512(data),
        SimdKernel.Vector256 => SumBytesVector256(data),
        SimdKernel.Vector128 => SumBytesVector128(data),
        SimdKernel.Sve or SimdKernel.PortableVectors => SumBytesPortableVectors(data),
        _ => SumBytesSoftware(data),
    };

    /// <summary>
    /// XOR-folds native 16-bit words through one specific kernel, falling back to the scalar loop when that kernel
    /// is unavailable on the running processor.
    /// <para>用指定内核异或折叠本机 16 位字；该内核不可用时回退到标量循环。</para>
    /// </summary>
    /// <param name="words">Native 16-bit words to fold.<para>待折叠的本机 16 位字。</para></param>
    /// <param name="kernel">Kernel to exercise.<para>要执行的内核。</para></param>
    /// <returns>The XOR reduction of all words.<para>全部字的异或归约值。</para></returns>
    internal static ushort XorFoldWordsKernel(ReadOnlySpan<ushort> words, SimdKernel kernel) => kernel switch
    {
        SimdKernel.Vector512 => XorFoldWordsVector512(words),
        SimdKernel.Vector256 => XorFoldWordsVector256(words),
        SimdKernel.Vector128 => XorFoldWordsVector128(words),
        SimdKernel.Sve or SimdKernel.PortableVectors => XorFoldWordsPortableVectors(words),
        _ => XorFoldWordsSoftware(words),
    };

    /// <summary>Returns the scalar loop when the per-thread fallback switch is set; otherwise the selected kernel.
    /// <para>线程级回退开关打开时返回标量循环，否则返回所选内核。</para></summary>
    /// <returns>The kernel used by the public entry points.<para>公共入口使用的内核。</para></returns>
    private static SimdKernel CurrentKernel()
        => ForceSoftwareFallback ? SimdKernel.Software : SelectedKernel;

    /// <summary>Selects the widest kernel the processor and runtime can execute.
    /// <para>选择处理器与运行时能执行的最宽内核。</para></summary>
    /// <returns>The selected kernel.<para>选定的内核。</para></returns>
    private static SimdKernel SelectKernel()
    {
        if (ArmSve.IsSupported && Vector.IsHardwareAccelerated)
        {
            return SimdKernel.Sve;
        }

        if (Vector512.IsHardwareAccelerated
            && (Avx512F.IsSupported || Avx512BW.IsSupported || Avx10v1.V512.IsSupported || Avx10v2.V512.IsSupported))
        {
            return SimdKernel.Vector512;
        }

        if (Vector256.IsHardwareAccelerated)
        {
            return SimdKernel.Vector256;
        }

        if (Vector128.IsHardwareAccelerated)
        {
            return SimdKernel.Vector128;
        }

        return Vector.IsHardwareAccelerated ? SimdKernel.PortableVectors : SimdKernel.Software;
    }

    /// <summary>XORs byte by byte; the software fallback used by every kernel for its trailing remainder.
    /// <para>逐字节异或；所有内核的尾部余数与软件回退都走这里。</para></summary>
    /// <param name="destination">Buffer to update in place.<para>就地更新的缓冲区。</para></param>
    /// <param name="source">Bytes to XOR in.<para>参与异或的字节。</para></param>
    private static void XorSoftware(Span<byte> destination, ReadOnlySpan<byte> source)
    {
        for (var index = 0; index < destination.Length; index++)
        {
            destination[index] ^= source[index];
        }
    }

    /// <summary>XORs one repeated byte into every position with a portable loop.
    /// <para>用可移植循环把重复字节异或到每个位置。</para></summary>
    /// <param name="destination">Buffer to update in place.<para>就地更新的缓冲区。</para></param>
    /// <param name="value">Byte XORed into every position.<para>异或到每个位置的字节。</para></param>
    private static void XorConstantSoftware(Span<byte> destination, byte value)
    {
        for (var index = 0; index < destination.Length; index++)
        {
            destination[index] ^= value;
        }
    }

    /// <summary>Adds every byte with a portable loop, wrapping modulo 2^32.
    /// <para>用可移植循环按模 2^32 累加每个字节。</para></summary>
    /// <param name="data">Bytes to sum.<para>待累加的字节。</para></param>
    /// <returns>The sum modulo 2^32.<para>按模 2^32 得到的和。</para></returns>
    private static uint SumBytesSoftware(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        foreach (var value in data)
        {
            sum += value;
        }

        return sum;
    }

    /// <summary>XORs whole 512-bit blocks, then the scalar tail.
    /// <para>先异或完整的 512 位块，再处理标量尾部。</para></summary>
    /// <param name="destination">Buffer to update in place.<para>就地更新的缓冲区。</para></param>
    /// <param name="source">Bytes to XOR in.<para>参与异或的字节。</para></param>
    private static void XorVector512(Span<byte> destination, ReadOnlySpan<byte> source)
    {
        if (!Vector512.IsHardwareAccelerated)
        {
            XorSoftware(destination, source);
            return;
        }

        var width = Vector512<byte>.Count;
        var offset = 0;
        for (; offset + width <= destination.Length; offset += width)
        {
            var left = Vector512.Create(destination.Slice(offset, width));
            var right = Vector512.Create(source.Slice(offset, width));
            (left ^ right).CopyTo(destination.Slice(offset, width));
        }

        XorSoftware(destination[offset..], source[offset..]);
    }

    /// <summary>XORs whole 256-bit blocks, then the scalar tail.
    /// <para>先异或完整的 256 位块，再处理标量尾部。</para></summary>
    /// <param name="destination">Buffer to update in place.<para>就地更新的缓冲区。</para></param>
    /// <param name="source">Bytes to XOR in.<para>参与异或的字节。</para></param>
    private static void XorVector256(Span<byte> destination, ReadOnlySpan<byte> source)
    {
        if (!Vector256.IsHardwareAccelerated)
        {
            XorSoftware(destination, source);
            return;
        }

        var width = Vector256<byte>.Count;
        var offset = 0;
        for (; offset + width <= destination.Length; offset += width)
        {
            var left = Vector256.Create(destination.Slice(offset, width));
            var right = Vector256.Create(source.Slice(offset, width));
            (left ^ right).CopyTo(destination.Slice(offset, width));
        }

        XorSoftware(destination[offset..], source[offset..]);
    }

    /// <summary>XORs whole 128-bit blocks, then the scalar tail.
    /// <para>先异或完整的 128 位块，再处理标量尾部。</para></summary>
    /// <param name="destination">Buffer to update in place.<para>就地更新的缓冲区。</para></param>
    /// <param name="source">Bytes to XOR in.<para>参与异或的字节。</para></param>
    private static void XorVector128(Span<byte> destination, ReadOnlySpan<byte> source)
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            XorSoftware(destination, source);
            return;
        }

        var width = Vector128<byte>.Count;
        var offset = 0;
        for (; offset + width <= destination.Length; offset += width)
        {
            var left = Vector128.Create(destination.Slice(offset, width));
            var right = Vector128.Create(source.Slice(offset, width));
            (left ^ right).CopyTo(destination.Slice(offset, width));
        }

        XorSoftware(destination[offset..], source[offset..]);
    }

    /// <summary>XORs whole runtime-sized blocks, which the JIT lowers to the best available register width.
    /// <para>按运行时决定的块宽异或，由 JIT 降级到最宽的可用寄存器。</para></summary>
    /// <param name="destination">Buffer to update in place.<para>就地更新的缓冲区。</para></param>
    /// <param name="source">Bytes to XOR in.<para>参与异或的字节。</para></param>
    private static void XorPortableVectors(Span<byte> destination, ReadOnlySpan<byte> source)
    {
        if (!Vector.IsHardwareAccelerated)
        {
            XorSoftware(destination, source);
            return;
        }

        var width = Vector<byte>.Count;
        var offset = 0;
        for (; offset + width <= destination.Length; offset += width)
        {
            var left = new Vector<byte>(destination.Slice(offset, width));
            var right = new Vector<byte>(source.Slice(offset, width));
            Vector.Xor(left, right).CopyTo(destination.Slice(offset, width));
        }

        XorSoftware(destination[offset..], source[offset..]);
    }

    /// <summary>XORs scalable SVE vectors whose width is determined by the processor at run time.
    /// <para>按处理器运行时决定的宽度异或可伸缩 SVE 向量。</para></summary>
    /// <param name="destination">Buffer to update in place.<para>就地更新的缓冲区。</para></param>
    /// <param name="source">Bytes to XOR in.<para>参与异或的字节。</para></param>
    private static void XorSve(Span<byte> destination, ReadOnlySpan<byte> source)
    {
        if (!ArmSve.IsSupported || !Vector.IsHardwareAccelerated)
        {
            XorSoftware(destination, source);
            return;
        }

        var width = Vector<byte>.Count;
        ref var target = ref MemoryMarshal.GetReference(destination);
        ref readonly var input = ref MemoryMarshal.GetReference(source);
        var offset = 0;
        for (; offset + width <= destination.Length; offset += width)
        {
            var left = Vector.LoadUnsafe(in target, (nuint)offset);
            var right = Vector.LoadUnsafe(in input, (nuint)offset);
            Vector.StoreUnsafe(ArmSve.Xor(left, right), ref target, (nuint)offset);
        }

        XorSoftware(destination[offset..], source[offset..]);
    }

    /// <summary>XORs a repeated 512-bit constant into whole blocks, then the scalar tail.
    /// <para>先把重复的 512 位常量异或进完整块，再处理标量尾部。</para></summary>
    /// <param name="destination">Buffer to update in place.<para>就地更新的缓冲区。</para></param>
    /// <param name="value">Byte XORed into every position.<para>异或到每个位置的字节。</para></param>
    private static void XorConstantVector512(Span<byte> destination, byte value)
    {
        if (!Vector512.IsHardwareAccelerated)
        {
            XorConstantSoftware(destination, value);
            return;
        }

        var constant = Vector512.Create(value);
        var width = Vector512<byte>.Count;
        var offset = 0;
        for (; offset + width <= destination.Length; offset += width)
        {
            var block = Vector512.Create(destination.Slice(offset, width));
            (block ^ constant).CopyTo(destination.Slice(offset, width));
        }

        XorConstantSoftware(destination[offset..], value);
    }

    /// <summary>XORs a repeated 256-bit constant into whole blocks, then the scalar tail.
    /// <para>先把重复的 256 位常量异或进完整块，再处理标量尾部。</para></summary>
    /// <param name="destination">Buffer to update in place.<para>就地更新的缓冲区。</para></param>
    /// <param name="value">Byte XORed into every position.<para>异或到每个位置的字节。</para></param>
    private static void XorConstantVector256(Span<byte> destination, byte value)
    {
        if (!Vector256.IsHardwareAccelerated)
        {
            XorConstantSoftware(destination, value);
            return;
        }

        var constant = Vector256.Create(value);
        var width = Vector256<byte>.Count;
        var offset = 0;
        for (; offset + width <= destination.Length; offset += width)
        {
            var block = Vector256.Create(destination.Slice(offset, width));
            (block ^ constant).CopyTo(destination.Slice(offset, width));
        }

        XorConstantSoftware(destination[offset..], value);
    }

    /// <summary>XORs a repeated 128-bit constant into whole blocks, then the scalar tail.
    /// <para>先把重复的 128 位常量异或进完整块，再处理标量尾部。</para></summary>
    /// <param name="destination">Buffer to update in place.<para>就地更新的缓冲区。</para></param>
    /// <param name="value">Byte XORed into every position.<para>异或到每个位置的字节。</para></param>
    private static void XorConstantVector128(Span<byte> destination, byte value)
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            XorConstantSoftware(destination, value);
            return;
        }

        var constant = Vector128.Create(value);
        var width = Vector128<byte>.Count;
        var offset = 0;
        for (; offset + width <= destination.Length; offset += width)
        {
            var block = Vector128.Create(destination.Slice(offset, width));
            (block ^ constant).CopyTo(destination.Slice(offset, width));
        }

        XorConstantSoftware(destination[offset..], value);
    }

    /// <summary>XORs a repeated runtime-sized constant into whole blocks, then the scalar tail.
    /// <para>先把重复的运行时宽度常量异或进完整块，再处理标量尾部。</para></summary>
    /// <param name="destination">Buffer to update in place.<para>就地更新的缓冲区。</para></param>
    /// <param name="value">Byte XORed into every position.<para>异或到每个位置的字节。</para></param>
    private static void XorConstantPortableVectors(Span<byte> destination, byte value)
    {
        if (!Vector.IsHardwareAccelerated)
        {
            XorConstantSoftware(destination, value);
            return;
        }

        var constant = new Vector<byte>(value);
        var width = Vector<byte>.Count;
        var offset = 0;
        for (; offset + width <= destination.Length; offset += width)
        {
            var block = new Vector<byte>(destination.Slice(offset, width));
            Vector.Xor(block, constant).CopyTo(destination.Slice(offset, width));
        }

        XorConstantSoftware(destination[offset..], value);
    }

    /// <summary>XORs a repeated scalable SVE constant into whole vectors, then the scalar tail.
    /// <para>先把重复的可伸缩 SVE 常量异或进完整向量，再处理标量尾部。</para></summary>
    /// <param name="destination">Buffer to update in place.<para>就地更新的缓冲区。</para></param>
    /// <param name="value">Byte XORed into every position.<para>异或到每个位置的字节。</para></param>
    private static void XorConstantSve(Span<byte> destination, byte value)
    {
        if (!ArmSve.IsSupported || !Vector.IsHardwareAccelerated)
        {
            XorConstantSoftware(destination, value);
            return;
        }

        var constant = new Vector<byte>(value);
        var width = Vector<byte>.Count;
        ref var target = ref MemoryMarshal.GetReference(destination);
        var offset = 0;
        for (; offset + width <= destination.Length; offset += width)
        {
            var block = Vector.LoadUnsafe(in target, (nuint)offset);
            Vector.StoreUnsafe(ArmSve.Xor(block, constant), ref target, (nuint)offset);
        }

        XorConstantSoftware(destination[offset..], value);
    }

    /// <summary>Sums bytes widened into 32-bit lanes of a 512-bit register.
    /// <para>把字节扩宽到 512 位寄存器的 32 位通道后累加。</para></summary>
    /// <param name="data">Bytes to sum.<para>待累加的字节。</para></param>
    /// <returns>The sum modulo 2^32.<para>按模 2^32 得到的和。</para></returns>
    private static uint SumBytesVector512(ReadOnlySpan<byte> data)
    {
        if (!Vector512.IsHardwareAccelerated)
        {
            return SumBytesSoftware(data);
        }

        var accumulator = Vector512<uint>.Zero;
        var width = Vector512<byte>.Count;
        var offset = 0;
        for (; offset + width <= data.Length; offset += width)
        {
            var bytes = Vector512.Create(data.Slice(offset, width));
            (var lower, var upper) = Vector512.Widen(bytes);
            (var first, var second) = Vector512.Widen(lower);
            (var third, var fourth) = Vector512.Widen(upper);
            accumulator += first + second + third + fourth;
        }

        return ReduceVector512(accumulator, data[offset..]);
    }

    /// <summary>Sums bytes widened into 32-bit lanes of a 256-bit register.
    /// <para>把字节扩宽到 256 位寄存器的 32 位通道后累加。</para></summary>
    /// <param name="data">Bytes to sum.<para>待累加的字节。</para></param>
    /// <returns>The sum modulo 2^32.<para>按模 2^32 得到的和。</para></returns>
    private static uint SumBytesVector256(ReadOnlySpan<byte> data)
    {
        if (!Vector256.IsHardwareAccelerated)
        {
            return SumBytesSoftware(data);
        }

        var accumulator = Vector256<uint>.Zero;
        var width = Vector256<byte>.Count;
        var offset = 0;
        for (; offset + width <= data.Length; offset += width)
        {
            var bytes = Vector256.Create(data.Slice(offset, width));
            (var lower, var upper) = Vector256.Widen(bytes);
            (var first, var second) = Vector256.Widen(lower);
            (var third, var fourth) = Vector256.Widen(upper);
            accumulator += first + second + third + fourth;
        }

        return ReduceVector256(accumulator, data[offset..]);
    }

    /// <summary>Sums bytes widened into 32-bit lanes of a 128-bit register.
    /// <para>把字节扩宽到 128 位寄存器的 32 位通道后累加。</para></summary>
    /// <param name="data">Bytes to sum.<para>待累加的字节。</para></param>
    /// <returns>The sum modulo 2^32.<para>按模 2^32 得到的和。</para></returns>
    private static uint SumBytesVector128(ReadOnlySpan<byte> data)
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            return SumBytesSoftware(data);
        }

        var accumulator = Vector128<uint>.Zero;
        var width = Vector128<byte>.Count;
        var offset = 0;
        for (; offset + width <= data.Length; offset += width)
        {
            var bytes = Vector128.Create(data.Slice(offset, width));
            (var lower, var upper) = Vector128.Widen(bytes);
            (var first, var second) = Vector128.Widen(lower);
            (var third, var fourth) = Vector128.Widen(upper);
            accumulator += first + second + third + fourth;
        }

        return ReduceVector128(accumulator, data[offset..]);
    }

    /// <summary>Sums bytes widened into 32-bit lanes whose width the runtime chooses, including SVE hosts.
    /// <para>按运行时决定的通道宽度把字节扩宽到 32 位通道后累加，SVE 主机也走此路径。</para></summary>
    /// <param name="data">Bytes to sum.<para>待累加的字节。</para></param>
    /// <returns>The sum modulo 2^32.<para>按模 2^32 得到的和。</para></returns>
    private static uint SumBytesPortableVectors(ReadOnlySpan<byte> data)
    {
        if (!Vector.IsHardwareAccelerated)
        {
            return SumBytesSoftware(data);
        }

        var accumulator = Vector<uint>.Zero;
        var width = Vector<byte>.Count;
        var offset = 0;
        for (; offset + width <= data.Length; offset += width)
        {
            var bytes = new Vector<byte>(data.Slice(offset, width));
            Vector.Widen(bytes, out Vector<ushort> lower, out Vector<ushort> upper);
            Vector.Widen(lower, out Vector<uint> first, out Vector<uint> second);
            Vector.Widen(upper, out Vector<uint> third, out Vector<uint> fourth);
            accumulator += first + second + third + fourth;
        }

        uint sum = 0;
        for (var lane = 0; lane < Vector<uint>.Count; lane++)
        {
            sum += accumulator[lane];
        }

        return sum + SumBytesSoftware(data[offset..]);
    }

    /// <summary>XOR-folds words with a portable loop, the software fallback used by every kernel for its remainder.
    /// <para>用可移植循环折叠字；所有内核的尾部余数与软件回退都走这里。</para></summary>
    /// <param name="words">Native 16-bit words to fold.<para>待折叠的本机 16 位字。</para></param>
    /// <returns>The XOR reduction of all words.<para>全部字的异或归约值。</para></returns>
    private static ushort XorFoldWordsSoftware(ReadOnlySpan<ushort> words)
    {
        ushort checksum = 0;
        foreach (var word in words)
        {
            checksum ^= word;
        }

        return checksum;
    }

    /// <summary>XOR-folds whole 512-bit word blocks, then the scalar tail.
    /// <para>先折叠完整的 512 位字块，再以标量收尾。</para></summary>
    /// <param name="words">Native 16-bit words to fold.<para>待折叠的本机 16 位字。</para></param>
    /// <returns>The XOR reduction of all words.<para>全部字的异或归约值。</para></returns>
    private static ushort XorFoldWordsVector512(ReadOnlySpan<ushort> words)
    {
        if (!Vector512.IsHardwareAccelerated)
        {
            return XorFoldWordsSoftware(words);
        }

        var width = Vector512<ushort>.Count;
        var accumulator = Vector512<ushort>.Zero;
        var index = 0;
        for (; index + width <= words.Length; index += width)
        {
            accumulator ^= Vector512.Create(words.Slice(index, width));
        }

        return (ushort)(ReduceWords512(accumulator) ^ XorFoldWordsSoftware(words[index..]));
    }

    /// <summary>XOR-folds whole 256-bit word blocks, then the scalar tail.
    /// <para>先折叠完整的 256 位字块，再以标量收尾。</para></summary>
    /// <param name="words">Native 16-bit words to fold.<para>待折叠的本机 16 位字。</para></param>
    /// <returns>The XOR reduction of all words.<para>全部字的异或归约值。</para></returns>
    private static ushort XorFoldWordsVector256(ReadOnlySpan<ushort> words)
    {
        if (!Vector256.IsHardwareAccelerated)
        {
            return XorFoldWordsSoftware(words);
        }

        var width = Vector256<ushort>.Count;
        var accumulator = Vector256<ushort>.Zero;
        var index = 0;
        for (; index + width <= words.Length; index += width)
        {
            accumulator ^= Vector256.Create(words.Slice(index, width));
        }

        return (ushort)(ReduceWords256(accumulator) ^ XorFoldWordsSoftware(words[index..]));
    }

    /// <summary>XOR-folds whole 128-bit word blocks, then the scalar tail.
    /// <para>先折叠完整的 128 位字块，再以标量收尾。</para></summary>
    /// <param name="words">Native 16-bit words to fold.<para>待折叠的本机 16 位字。</para></param>
    /// <returns>The XOR reduction of all words.<para>全部字的异或归约值。</para></returns>
    private static ushort XorFoldWordsVector128(ReadOnlySpan<ushort> words)
    {
        if (!Vector128.IsHardwareAccelerated)
        {
            return XorFoldWordsSoftware(words);
        }

        var width = Vector128<ushort>.Count;
        var accumulator = Vector128<ushort>.Zero;
        var index = 0;
        for (; index + width <= words.Length; index += width)
        {
            accumulator ^= Vector128.Create(words.Slice(index, width));
        }

        return (ushort)(ReduceWords128(accumulator) ^ XorFoldWordsSoftware(words[index..]));
    }

    /// <summary>XOR-folds runtime-sized word blocks, which the JIT lowers to the widest available register width.
    /// <para>按运行时决定的块宽折叠字，由 JIT 降级到最宽的可用寄存器。</para></summary>
    /// <param name="words">Native 16-bit words to fold.<para>待折叠的本机 16 位字。</para></param>
    /// <returns>The XOR reduction of all words.<para>全部字的异或归约值。</para></returns>
    private static ushort XorFoldWordsPortableVectors(ReadOnlySpan<ushort> words)
    {
        if (!Vector.IsHardwareAccelerated)
        {
            return XorFoldWordsSoftware(words);
        }

        var width = Vector<ushort>.Count;
        var accumulator = Vector<ushort>.Zero;
        var index = 0;
        for (; index + width <= words.Length; index += width)
        {
            accumulator ^= new Vector<ushort>(words.Slice(index, width));
        }

        ushort folded = 0;
        for (var lane = 0; lane < width; lane++)
        {
            folded ^= accumulator[lane];
        }

        return (ushort)(folded ^ XorFoldWordsSoftware(words[index..]));
    }

    /// <summary>Horizontally XORs the 512-bit lanes. <para>横向异或 512 位通道。</para></summary>
    /// <param name="accumulator">Lane accumulators.<para>通道累加值。</para></param>
    /// <returns>The folded 16-bit result.<para>折叠后的 16 位结果。</para></returns>
    private static ushort ReduceWords512(Vector512<ushort> accumulator)
    {
        ushort folded = 0;
        for (var lane = 0; lane < Vector512<ushort>.Count; lane++)
        {
            folded ^= accumulator.GetElement(lane);
        }

        return folded;
    }

    /// <summary>Horizontally XORs the 256-bit lanes. <para>横向异或 256 位通道。</para></summary>
    /// <param name="accumulator">Lane accumulators.<para>通道累加值。</para></param>
    /// <returns>The folded 16-bit result.<para>折叠后的 16 位结果。</para></returns>
    private static ushort ReduceWords256(Vector256<ushort> accumulator)
    {
        ushort folded = 0;
        for (var lane = 0; lane < Vector256<ushort>.Count; lane++)
        {
            folded ^= accumulator.GetElement(lane);
        }

        return folded;
    }

    /// <summary>Horizontally XORs the 128-bit lanes. <para>横向异或 128 位通道。</para></summary>
    /// <param name="accumulator">Lane accumulators.<para>通道累加值。</para></param>
    /// <returns>The folded 16-bit result.<para>折叠后的 16 位结果。</para></returns>
    private static ushort ReduceWords128(Vector128<ushort> accumulator)
    {
        ushort folded = 0;
        for (var lane = 0; lane < Vector128<ushort>.Count; lane++)
        {
            folded ^= accumulator.GetElement(lane);
        }

        return folded;
    }

    /// <summary>Horizontally adds the 512-bit lanes and the scalar tail.
    /// <para>横向累加 512 位通道与标量尾部。</para></summary>
    /// <param name="accumulator">Widened lane sums.<para>扩宽后的通道和。</para></param>
    /// <param name="tail">Bytes left after the vector loop.<para>向量循环后剩余的字节。</para></param>
    /// <returns>The sum modulo 2^32.<para>按模 2^32 得到的和。</para></returns>
    private static uint ReduceVector512(Vector512<uint> accumulator, ReadOnlySpan<byte> tail)
    {
        uint sum = 0;
        for (var lane = 0; lane < Vector512<uint>.Count; lane++)
        {
            sum += accumulator.GetElement(lane);
        }

        return sum + SumBytesSoftware(tail);
    }

    /// <summary>Horizontally adds the 256-bit lanes and the scalar tail.
    /// <para>横向累加 256 位通道与标量尾部。</para></summary>
    /// <param name="accumulator">Widened lane sums.<para>扩宽后的通道和。</para></param>
    /// <param name="tail">Bytes left after the vector loop.<para>向量循环后剩余的字节。</para></param>
    /// <returns>The sum modulo 2^32.<para>按模 2^32 得到的和。</para></returns>
    private static uint ReduceVector256(Vector256<uint> accumulator, ReadOnlySpan<byte> tail)
    {
        uint sum = 0;
        for (var lane = 0; lane < Vector256<uint>.Count; lane++)
        {
            sum += accumulator.GetElement(lane);
        }

        return sum + SumBytesSoftware(tail);
    }

    /// <summary>Horizontally adds the 128-bit lanes and the scalar tail.
    /// <para>横向累加 128 位通道与标量尾部。</para></summary>
    /// <param name="accumulator">Widened lane sums.<para>扩宽后的通道和。</para></param>
    /// <param name="tail">Bytes left after the vector loop.<para>向量循环后剩余的字节。</para></param>
    /// <returns>The sum modulo 2^32.<para>按模 2^32 得到的和。</para></returns>
    private static uint ReduceVector128(Vector128<uint> accumulator, ReadOnlySpan<byte> tail)
    {
        uint sum = 0;
        for (var lane = 0; lane < Vector128<uint>.Count; lane++)
        {
            sum += accumulator.GetElement(lane);
        }

        return sum + SumBytesSoftware(tail);
    }
}
#pragma warning restore SYSLIB5003