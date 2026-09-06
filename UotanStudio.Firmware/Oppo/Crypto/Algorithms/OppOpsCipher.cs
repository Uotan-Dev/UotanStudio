using System.Buffers.Binary;

namespace UotanStudio.Firmware.Oppo.Crypto.Algorithms;

internal static class OppOpsCipher
{
    private const int BlockSize = 16;

    /// <summary>
    /// Reads a uint32 value (little-endian) from the expanded S-Box at the specified index.
    /// <para>从扩展 S-Box 中读取指定索引处的 uint32 值（小端序）。</para>
    /// </summary>
    internal static uint Gsbox(ReadOnlySpan<byte> expandedSBox, int index) => BinaryPrimitives.ReadUInt32LittleEndian(expandedSBox.Slice(index, 4));

    /// <summary>
    /// Performs a key update operation, generating a new round key.
    /// <para>执行密钥更新操作，生成新的轮密钥。</para>
    /// </summary>
    internal static uint[] KeyUpdate(uint[] rkey, ReadOnlySpan<byte> mbox, ReadOnlySpan<byte> expandedSBox)
    {
        var result = new uint[4];
        Array.Copy(rkey, result, 4);
        KeyUpdateInPlace(result, mbox, expandedSBox);
        return result;
    }

    /// <summary>
    /// Performs a key update operation in place, directly modifying the input round key array.
    /// <para>就地执行密钥更新操作，直接修改输入的轮密钥数组。</para>
    /// </summary>
    internal static void KeyUpdateInPlace(uint[] rkey, ReadOnlySpan<byte> mbox, ReadOnlySpan<byte> expandedSBox)
    {


        var rounds = mbox.Length >= 61 ? mbox[60] : 10;


        var d = rkey[0] ^ mbox[0];
        var a = rkey[1] ^ mbox[1];
        var b = rkey[2] ^ mbox[2];
        var c = rkey[3] ^ mbox[3];


        var e = Gsbox(expandedSBox, ((int)((b >> 0x10) & 0xFF) * 8) + 2) ^
                 Gsbox(expandedSBox, ((int)((a >> 8) & 0xFF) * 8) + 3) ^
                 Gsbox(expandedSBox, ((int)((c >> 0x18) & 0xFF) * 8) + 1) ^
                 Gsbox(expandedSBox, (int)(d & 0xFF) * 8) ^ mbox[4];

        var h = Gsbox(expandedSBox, ((int)((c >> 0x10) & 0xFF) * 8) + 2) ^
                 Gsbox(expandedSBox, ((int)((b >> 8) & 0xFF) * 8) + 3) ^
                 Gsbox(expandedSBox, ((int)((d >> 0x18) & 0xFF) * 8) + 1) ^
                 Gsbox(expandedSBox, (int)(a & 0xFF) * 8) ^ mbox[5];

        var i = Gsbox(expandedSBox, ((int)((d >> 0x10) & 0xFF) * 8) + 2) ^
                 Gsbox(expandedSBox, ((int)((c >> 8) & 0xFF) * 8) + 3) ^
                 Gsbox(expandedSBox, ((int)((a >> 0x18) & 0xFF) * 8) + 1) ^
                 Gsbox(expandedSBox, (int)(b & 0xFF) * 8) ^ mbox[6];

        var a2 = Gsbox(expandedSBox, ((int)((d >> 8) & 0xFF) * 8) + 3) ^
                  Gsbox(expandedSBox, ((int)((a >> 0x10) & 0xFF) * 8) + 2) ^
                  Gsbox(expandedSBox, ((int)((b >> 0x18) & 0xFF) * 8) + 1) ^
                  Gsbox(expandedSBox, (int)(c & 0xFF) * 8) ^ mbox[7];


        var g = 8;
        for (var round = 0; round < rounds - 2; round++)
        {
            var d2 = e >> 0x18;
            var m = h >> 0x10;
            var s = h >> 0x18;
            var z = e >> 0x10;
            var l = i >> 0x18;
            var t = e >> 8;

            e = Gsbox(expandedSBox, ((int)((i >> 0x10) & 0xFF) * 8) + 2) ^
                Gsbox(expandedSBox, ((int)((h >> 8) & 0xFF) * 8) + 3) ^
                Gsbox(expandedSBox, ((int)((a2 >> 0x18) & 0xFF) * 8) + 1) ^
                Gsbox(expandedSBox, (int)(e & 0xFF) * 8) ^ mbox[g];

            h = Gsbox(expandedSBox, ((int)((a2 >> 0x10) & 0xFF) * 8) + 2) ^
                Gsbox(expandedSBox, ((int)((i >> 8) & 0xFF) * 8) + 3) ^
                Gsbox(expandedSBox, (int)((d2 * 8) + 1)) ^
                Gsbox(expandedSBox, (int)(h & 0xFF) * 8) ^ mbox[g + 1];

            i = Gsbox(expandedSBox, ((int)(z & 0xFF) * 8) + 2) ^
                Gsbox(expandedSBox, ((int)((a2 >> 8) & 0xFF) * 8) + 3) ^
                Gsbox(expandedSBox, (int)((s * 8) + 1)) ^
                Gsbox(expandedSBox, (int)(i & 0xFF) * 8) ^ mbox[g + 2];

            a2 = Gsbox(expandedSBox, ((int)(t & 0xFF) * 8) + 3) ^
                 Gsbox(expandedSBox, ((int)(m & 0xFF) * 8) + 2) ^
                 Gsbox(expandedSBox, (int)((l * 8) + 1)) ^
                 Gsbox(expandedSBox, (int)(a2 & 0xFF) * 8) ^ mbox[g + 3];

            g += 4;
        }


        rkey[0] = (Gsbox(expandedSBox, (int)((i >> 0x10) & 0xFF) * 8) & 0xFF0000) ^
                  (Gsbox(expandedSBox, ((int)((h >> 8) & 0xFF) * 8) + 1) & 0xFF00) ^
                  (Gsbox(expandedSBox, ((int)((a2 >> 0x18) & 0xFF) * 8) + 3) & 0xFF000000) ^
                  (Gsbox(expandedSBox, ((int)(e & 0xFF) * 8) + 2) & 0xFF) ^ mbox[g];

        rkey[1] = (Gsbox(expandedSBox, (int)((a2 >> 0x10) & 0xFF) * 8) & 0xFF0000) ^
                  (Gsbox(expandedSBox, ((int)((i >> 8) & 0xFF) * 8) + 1) & 0xFF00) ^
                  (Gsbox(expandedSBox, ((int)((e >> 0x18) & 0xFF) * 8) + 3) & 0xFF000000) ^
                  (Gsbox(expandedSBox, ((int)(h & 0xFF) * 8) + 2) & 0xFF) ^ mbox[g + 3];

        rkey[2] = (Gsbox(expandedSBox, (int)((e >> 0x10) & 0xFF) * 8) & 0xFF0000) ^
                  (Gsbox(expandedSBox, ((int)((a2 >> 8) & 0xFF) * 8) + 1) & 0xFF00) ^
                  (Gsbox(expandedSBox, ((int)((h >> 0x18) & 0xFF) * 8) + 3) & 0xFF000000) ^
                  (Gsbox(expandedSBox, ((int)(i & 0xFF) * 8) + 2) & 0xFF) ^ mbox[g + 2];

        rkey[3] = (Gsbox(expandedSBox, (int)((h >> 0x10) & 0xFF) * 8) & 0xFF0000) ^
                  (Gsbox(expandedSBox, ((int)((e >> 8) & 0xFF) * 8) + 1) & 0xFF00) ^
                  (Gsbox(expandedSBox, ((int)((i >> 0x18) & 0xFF) * 8) + 3) & 0xFF000000) ^
                  (Gsbox(expandedSBox, ((int)(a2 & 0xFF) * 8) + 2) & 0xFF) ^ mbox[g + 1];
    }

    /// <summary>
    /// Reads a byte value from the round key at the specified byte position.
    /// <para>从轮密钥中读取指定字节位置的字节值。</para>
    /// </summary>
    internal static byte GetRkeyByte(uint[] rkey, int byteIndex)
    {
        var wordIndex = byteIndex / 4;
        var byteOffset = byteIndex % 4;
        return (byte)(rkey[wordIndex] >> (byteOffset * 8));
    }

    /// <summary>
    /// Encrypts data using the OPS custom cipher (in-place operation).
    /// <para>使用 OPS 自定义密码加密数据（就地操作）。</para>
    /// </summary>
    internal static void Encrypt(Span<byte> data, uint[] rkey, ReadOnlySpan<byte> expandedSBox, ReadOnlySpan<byte> mbox, int outlength = 0)
    {
        if (data.Length == 0)
        {
            return;
        }

        ProcessBlocks(data, rkey, expandedSBox, mbox, outlength, encrypting: true);
    }

    /// <summary>
    /// Decrypts data using the OPS custom cipher (in-place operation).
    /// <para>使用 OPS 自定义密码解密数据（就地操作）。</para>
    /// </summary>
    internal static void Decrypt(Span<byte> data, uint[] rkey, ReadOnlySpan<byte> expandedSBox, ReadOnlySpan<byte> mbox, int outlength = 0)
    {
        if (data.Length == 0)
        {
            return;
        }

        ProcessBlocks(data, rkey, expandedSBox, mbox, outlength, encrypting: false);
    }

    private static void ProcessBlocks(Span<byte> data, uint[] rkey, ReadOnlySpan<byte> expandedSBox, ReadOnlySpan<byte> mbox, int outlength, bool encrypting)
    {
        var offset = 0;


        if (outlength is > 0 and < BlockSize)
        {


            for (var i = outlength; i < BlockSize && offset + i < data.Length; i++)
            {
                var rkeyByte = GetRkeyByte(rkey, i);
                data[offset + i] ^= rkeyByte;
            }

            offset = Math.Min(BlockSize, data.Length);


            KeyUpdateInPlace(rkey, mbox, expandedSBox);
        }


        while (offset + BlockSize <= data.Length)
        {
            ProcessBlock(data.Slice(offset, BlockSize), rkey, expandedSBox, mbox, encrypting);
            offset += BlockSize;
        }


        if (offset < data.Length)
        {
            ProcessSubBlock(data[offset..], rkey, expandedSBox, encrypting);
        }
    }

    private static void ProcessBlock(Span<byte> block, uint[] rkey, ReadOnlySpan<byte> expandedSBox, ReadOnlySpan<byte> mbox, bool encrypting)
    {

        KeyUpdateInPlace(rkey, mbox, expandedSBox);


        for (var i = 0; i < 4; i++)
        {
            var inputWord = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(i * 4, 4));
            var outputWord = inputWord ^ rkey[i];
            BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(i * 4, 4), outputWord);
        }


        if (encrypting)
        {

            for (var i = 0; i < 4; i++)
            {
                rkey[i] = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(i * 4, 4));
            }
        }
        else
        {


            for (var i = 0; i < 4; i++)
            {


                rkey[i] = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(i * 4, 4)) ^ rkey[i];
            }
        }
    }

    private static void ProcessSubBlock(Span<byte> block, uint[] rkey, ReadOnlySpan<byte> expandedSBox, bool encrypting)
    {

        KeyUpdateInPlace(rkey, expandedSBox, expandedSBox);


        var pos = 0;
        var m = 0;
        while (pos < block.Length)
        {

            uint tmp = 0;
            var bytesToRead = Math.Min(4, block.Length - pos);
            for (var j = 0; j < bytesToRead; j++)
            {
                tmp |= (uint)(block[pos + j] << (j * 8));
            }


            var output = tmp ^ rkey[m];


            var bytesToWrite = Math.Min(4, block.Length - pos);
            for (var j = 0; j < bytesToWrite; j++)
            {
                block[pos + j] = (byte)((output >> (j * 8)) & 0xFF);
            }


            if (encrypting)
            {

                rkey[m] = output;
            }
            else
            {

                rkey[m] = tmp;
            }

            pos += 4;
            m++;
        }
    }
}
