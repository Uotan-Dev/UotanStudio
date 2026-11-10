using System.Buffers.Binary;

namespace UotanStudio.Hash.Xxh32;

/// <summary>
/// XXH32 as the on-disk EROFS xattr name filter requires, in the variant the Linux kernel and the fsck
/// implementations compute.
/// <para>EROFS xattr 名称过滤器所固定的磁盘格式变体，即 Linux 内核与 fsck 实现所计算的 XXH32。</para>
/// </summary>
public static class Xxh32
{
    /// <summary>
    /// Computes the 32-bit xxHash of the input with the given seed.
    /// <para>以给定种子计算输入的 32 位 xxHash。</para>
    /// </summary>
    /// <param name="input">Bytes to hash.<para>待哈希的字节。</para></param>
    /// <param name="seed">Seed mixed into the accumulator.<para>混入累加器的种子。</para></param>
    /// <returns>The 32-bit digest.<para>32 位摘要。</para></returns>
    public static uint Compute(ReadOnlySpan<byte> input, uint seed)
    {
        const uint prime1 = 2654435761u, prime2 = 2246822519u, prime3 = 3266489917u;
        const uint prime4 = 668265263u, prime5 = 374761393u;
        var offset = 0;
        uint hash;
        if (input.Length >= 16)
        {
            var v1 = seed + prime1 + prime2;
            var v2 = seed + prime2;
            var v3 = seed;
            var v4 = seed - prime1;
            for (; offset <= input.Length - 16; offset += 16)
            {
                v1 = Round(v1, BinaryPrimitives.ReadUInt32LittleEndian(input[offset..]));
                v2 = Round(v2, BinaryPrimitives.ReadUInt32LittleEndian(input[(offset + 4)..]));
                v3 = Round(v3, BinaryPrimitives.ReadUInt32LittleEndian(input[(offset + 8)..]));
                v4 = Round(v4, BinaryPrimitives.ReadUInt32LittleEndian(input[(offset + 12)..]));
            }

            hash = RotateLeft(v1, 1) + RotateLeft(v2, 7) + RotateLeft(v3, 12) + RotateLeft(v4, 18);
        }
        else
        {
            hash = seed + prime5;
        }

        hash += (uint)input.Length;
        for (; offset <= input.Length - 4; offset += 4)
        {
            hash += BinaryPrimitives.ReadUInt32LittleEndian(input[offset..]) * prime3;
            hash = RotateLeft(hash, 17) * prime4;
        }

        for (; offset < input.Length; offset++)
        {
            hash += input[offset] * prime5;
            hash = RotateLeft(hash, 11) * prime1;
        }

        hash ^= hash >> 15;
        hash *= prime2;
        hash ^= hash >> 13;
        hash *= prime3;
        hash ^= hash >> 16;
        return hash;
    }

    private static uint Round(uint accumulator, uint input)
    {
        const uint prime1 = 2654435761u, prime2 = 2246822519u;
        accumulator += input * prime2;
        accumulator = RotateLeft(accumulator, 13);
        return accumulator * prime1;
    }

    private static uint RotateLeft(uint value, int count) => (value << count) | (value >> (32 - count));
}