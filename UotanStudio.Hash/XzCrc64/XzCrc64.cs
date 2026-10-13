namespace UotanStudio.Hash.XzCrc64;

/// <summary>
/// XZ uses reflected CRC-64 with all-one initialization and final XOR.
/// System.IO.Hashing.Crc64 implements the non-reflected ECMA-182 variant instead.
/// </summary>
internal sealed class XzCrc64
{
    private static readonly ulong[] Table = CreateTable();
    private ulong _state = ulong.MaxValue;

    internal void Append(ReadOnlySpan<byte> data)
    {
        var state = _state;
        foreach (var value in data)
        {
            state = Table[(byte)(state ^ value)] ^ (state >> 8);
        }
        _state = state;
    }

    internal ulong GetCurrentHashAsUInt64() => ~_state;

    internal static ulong HashToUInt64(ReadOnlySpan<byte> data)
    {
        var crc = new XzCrc64();
        crc.Append(data);
        return crc.GetCurrentHashAsUInt64();
    }

    private static ulong[] CreateTable()
    {
        const ulong reflectedPolynomial = 0xc96c5795d7870f42;
        var table = new ulong[256];
        for (var i = 0; i < table.Length; i++)
        {
            var value = (ulong)i;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value >> 1) ^ ((value & 1) != 0 ? reflectedPolynomial : 0);
            }
            table[i] = value;
        }
        return table;
    }
}