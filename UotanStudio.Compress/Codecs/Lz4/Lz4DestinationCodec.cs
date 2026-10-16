using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace UotanStudio.Compress.Codecs.Lz4;

internal static partial class Lz4DestinationCodec
{
    private const int MaximumBlockBytes = 256 * 1024 * 1024;
    private const int ALGORITHM_ARCH = 8;

    internal static unsafe int Encode(ReadOnlySpan<byte> source, Span<byte> target, int algorithm, int level, out int consumed)
        => Encode(source, target, algorithm, level, out consumed, state: null);

    /// <summary>
    /// Encodes with a caller-owned LZ4 stream context, so a per-block loop can reuse one allocation instead of
    /// paying a native context allocation per block.
    /// <para>使用调用方持有的 LZ4 流上下文编码，使逐块循环复用同一分配而不必每块付一次原生上下文分配。</para>
    /// </summary>
    /// <param name="source">Block payload to encode. <para>待编码的块载荷。</para></param>
    /// <param name="target">Destination span, bounded exactly as the parameterless-context overload requires.
    /// <para>目标 span，其上界要求与不带上下文的重载一致。</para></param>
    /// <param name="algorithm">1 for the fast path, 2 for HC. <para>1 为快速路径，2 为 HC。</para></param>
    /// <param name="level">Compression level handed to the selected path. <para>传给所选路径的压缩级别。</para></param>
    /// <param name="consumed">Bytes the encoder consumed from <paramref name="source"/>.
    /// <para>编码器从 <paramref name="source"/> 消费的字节数。</para></param>
    /// <param name="state">
    /// Native context from <see cref="RentState"/>; pass <see langword="null"/> to allocate and release a
    /// private context for this call alone.
    /// <para>来自 <see cref="RentState"/> 的原生上下文；传 <see langword="null"/> 表示仅为本调用分配并在结束时
    /// 释放私有上下文。</para></param>
    /// <returns>Bytes written into <paramref name="target"/>.
    /// <para>写入 <paramref name="target"/> 的字节数。</para></returns>
    /// <remarks>
    /// The context is re-initialised before every use, so a caller may keep one for the whole run and reuse it
    /// across blocks: the emitted bytes depend only on the block, its level and its algorithm, never on what the
    /// context previously encoded. Reuse is single-threaded per context; two concurrent callers must rent their own.
    /// <para>每次使用前都会重新初始化上下文，因此调用方可以在整个运行期间持有同一个上下文并在各块之间复用：
    /// 输出字节只取决于该块、其级别与算法，与上下文此前编码过什么无关。复用按上下文单线程；并发的两个调用方必须
    /// 各自借用自己的上下文。</para>
    /// </remarks>
    internal static unsafe int Encode(ReadOnlySpan<byte> source, Span<byte> target, int algorithm, int level,
        out int consumed, void* state)
    {
        if (source.Length > MaximumBlockBytes || target.Length > MaximumBlockBytes || target.IsEmpty)
            throw new InvalidDataException("LZ4 block exceeds the bounded destination-size codec limits.");
        if (algorithm is not (1 or 2))
            throw new InvalidDataException("Unknown destination-size LZ4 algorithm.");
        if (source.Overlaps(target))
            throw new ArgumentException("LZ4 input and output buffers must not overlap.");
        if (source.IsEmpty)
        {
            target[0] = 0;
            consumed = 0;
            return 1;
        }

        int stateSize;
        unsafe
        {
            stateSize = algorithm == 1 ? sizeof(LZ4_stream_t) : sizeof(LZ4_streamHC_t);
        }

        // A caller-supplied context is owned by the caller and only borrowed here; a private one is allocated
        // up front and released on the way out, so the single-call path keeps the old behaviour exactly.
        var ownedState = state is not null ? state : NativeMemory.AllocZeroed((nuint)stateSize);
        var releaseOwned = state is null;
        try
        {
            // A reused context still carries the previous block's currentOffset and dictionary pointers, and the
            // fast path derives its match base from currentOffset, so those stale values would aim the encoder at
            // memory it no longer owns. Clearing the whole context before every use starts each call from exactly
            // the state a freshly allocated one has, which is what keeps reuse byte-identical and memory-safe.
            if (state is not null)
            {
                new Span<byte>(ownedState, stateSize).Clear();
            }

            var sourceLength = source.Length;
            int result;
            unsafe
            {
                fixed (byte* input = source)
                fixed (byte* output = target)
                {
                    if (algorithm == 1)
                    {
                        result = LZ4_compress_generic((LZ4_stream_t*)ownedState, input, output, source.Length,
                            &sourceLength, target.Length, limitedOutput_directive.fillOutput,
                            source.Length < LZ4_64Klimit ? tableType_t.byU16 : tableType_t.byU32,
                            dict_directive.noDict, dictIssue_directive.noDictIssue, Math.Max(1, level));
                    }
                    else
                    {
                        var context = LZ4_initStreamHC(ownedState, stateSize);
                        LZ4HC_init_internal(context, input);
                        result = LZ4HC_compress_generic_internal(context, input, output, &sourceLength,
                            target.Length, level, limitedOutput_directive.fillOutput, dictCtx_directive.noDictCtx);
                    }
                }
            }
            if (result <= 0 || result > target.Length || sourceLength < 0 || sourceLength > source.Length)
                throw new InvalidDataException("LZ4 destination-size compression failed.");
            consumed = sourceLength;
            return result;
        }
        finally
        {
            if (releaseOwned)
            {
                NativeMemory.Free(ownedState);
            }
        }
    }

    /// <summary>
    /// Allocates a native LZ4 context sized for <paramref name="highCompression"/>, for use with the
    /// <c>Encode</c> overload whose last parameter is a <c>state</c> pointer.
    /// <para>按 <paramref name="highCompression"/> 分配原生 LZ4 上下文，供末参数为 <c>state</c> 指针的
    /// <c>Encode</c> 重载使用。</para>
    /// </summary>
    /// <param name="highCompression">
    /// <see langword="true"/> for the HC context, which is the larger of the two.
    /// <para>为 <see langword="true"/> 时分配较大的 HC 上下文。</para></param>
    /// <returns>A pointer to release with <see cref="ReturnState"/>.
    /// <para>需以 <see cref="ReturnState"/> 释放的指针。</para></returns>
    internal static unsafe void* RentState(bool highCompression)
        => NativeMemory.AllocZeroed((nuint)(highCompression ? sizeof(LZ4_streamHC_t) : sizeof(LZ4_stream_t)));

    /// <summary>
    /// Releases a context obtained from <see cref="RentState"/>.
    /// <para>释放由 <see cref="RentState"/> 取得的上下文。</para>
    /// </summary>
    /// <param name="state">Context pointer, or <see langword="null"/> for no action.
    /// <para>上下文指针；为 <see langword="null"/> 时不做任何事。</para></param>
    internal static unsafe void ReturnState(void* state)
    {
        if (state is not null)
        {
            NativeMemory.Free(state);
        }
    }

    [Conditional("DEBUG")]
    private static void Assert(bool value, [CallerArgumentExpression(nameof(value))] string? expression = null)
        => Debug.Assert(value, expression);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint LZ4_NbCommonBytes(ulong value) => (uint)BitOperations.TrailingZeroCount(value) >> 3;

    private static unsafe class Mem
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ushort Peek2(void* pointer) => Unsafe.ReadUnaligned<ushort>(pointer);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static uint Peek4(void* pointer) => Unsafe.ReadUnaligned<uint>(pointer);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static ulong PeekW(void* pointer) => Unsafe.ReadUnaligned<ulong>(pointer);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void Poke2(void* pointer, ushort value) => Unsafe.WriteUnaligned(pointer, value);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void Poke4(void* pointer, uint value) => Unsafe.WriteUnaligned(pointer, value);
        internal static void Copy(byte* target, byte* source, int length)
            => new ReadOnlySpan<byte>(source, length).CopyTo(new Span<byte>(target, length));
        internal static void Move(byte* target, byte* source, int length) => Copy(target, source, length);
        internal static void Fill(byte* target, byte value, int length) => new Span<byte>(target, length).Fill(value);
        internal static void WildCopy8(byte* target, byte* source, byte* end)
        {
            do
            {
                Unsafe.WriteUnaligned(target, Unsafe.ReadUnaligned<ulong>(source));
                target += 8;
                source += 8;
            } while (target < end);
        }
    }
}