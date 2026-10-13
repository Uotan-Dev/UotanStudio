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
        var state = NativeMemory.AllocZeroed((nuint)stateSize);
        try
        {
            int sourceLength = source.Length;
            int result;
            unsafe
            {
                fixed (byte* input = source)
                fixed (byte* output = target)
                {
                    if (algorithm == 1)
                    {
                        result = LZ4_compress_generic((LZ4_stream_t*)state, input, output, source.Length,
                            &sourceLength, target.Length, limitedOutput_directive.fillOutput,
                            source.Length < LZ4_64Klimit ? tableType_t.byU16 : tableType_t.byU32,
                            dict_directive.noDict, dictIssue_directive.noDictIssue, Math.Max(1, level));
                    }
                    else
                    {
                        var context = LZ4_initStreamHC(state, stateSize);
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
        finally { NativeMemory.Free(state); }
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