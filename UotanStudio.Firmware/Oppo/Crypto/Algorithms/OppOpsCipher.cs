using System.Buffers.Binary;

namespace UotanStudio.Firmware.Oppo.Crypto.Algorithms;

/// <summary>
/// <para>OPS 自定义分组密码实现，基于 AES 风格的密钥调度和 S-Box 替换。</para>
/// OPS custom block cipher implementation based on AES-style key schedule and S-Box substitution.
/// </summary>
internal static class OppOpsCipher
{
    private const int BlockSize = 16;

    /// <summary>
    /// <para>从扩展 S-Box 中读取指定索引处的 uint32 值（小端序）。</para>
    /// Reads a uint32 value (little-endian) from the expanded S-Box at the specified index.
    /// </summary>
    /// <param name="expandedSBox"><para>扩展 S-Box 字节数组（2048 字节）。</para>The expanded S-Box byte array (2048 bytes).</param>
    /// <param name="index"><para>字节索引。</para>The byte index.</param>
    /// <returns><para>读取的 uint32 值。</para>The read uint32 value.</returns>
    internal static uint Gsbox(ReadOnlySpan<byte> expandedSBox, int index) => BinaryPrimitives.ReadUInt32LittleEndian(expandedSBox.Slice(index, 4));

    /// <summary>
    /// <para>执行密钥更新操作，生成新的轮密钥。</para>
    /// Performs a key update operation, generating a new round key.
    /// </summary>
    /// <param name="rkey"><para>输入轮密钥（4 个 uint32 值）。</para>The input round key (4 uint32 values).</param>
    /// <param name="mbox"><para>MBox 常量字节数组。</para>The MBox constants byte array.</param>
    /// <param name="expandedSBox"><para>扩展 S-Box 字节数组。</para>The expanded S-Box byte array.</param>
    /// <returns><para>更新后的轮密钥（4 个 uint32 值）。</para>The updated round key (4 uint32 values).</returns>
    internal static uint[] KeyUpdate(uint[] rkey, ReadOnlySpan<byte> mbox, ReadOnlySpan<byte> expandedSBox)
    {
        var result = new uint[4];
        Array.Copy(rkey, result, 4);
        KeyUpdateInPlace(result, mbox, expandedSBox);
        return result;
    }

    /// <summary>
    /// <para>就地执行密钥更新操作，直接修改输入的轮密钥数组。</para>
    /// Performs a key update operation in place, directly modifying the input round key array.
    /// </summary>
    /// <param name="rkey"><para>轮密钥数组（4 个 uint32 值），将被就地修改。</para>The round key array (4 uint32 values), will be modified in place.</param>
    /// <param name="mbox"><para>MBox 常量字节数组。</para>The MBox constants byte array.</param>
    /// <param name="expandedSBox"><para>扩展 S-Box 字节数组。</para>The expanded S-Box byte array.</param>
    internal static void KeyUpdateInPlace(uint[] rkey, ReadOnlySpan<byte> mbox, ReadOnlySpan<byte> expandedSBox)
    {
        // mbox structure: bytes used individually for XOR operations
        // The byte at offset 60 determines the number of rounds
        var rounds = mbox.Length >= 61 ? mbox[60] : 10;

        // Step 1: XOR each rkey word with the corresponding single byte from mbox
        var d = rkey[0] ^ mbox[0];
        var a = rkey[1] ^ mbox[1];
        var b = rkey[2] ^ mbox[2];
        var c = rkey[3] ^ mbox[3];

        // Step 2: Apply S-Box substitution (first round)
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

        // Step 3: Main round loop
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

        // Step 4: Final combination to produce new rkey
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
    /// <para>从轮密钥中读取指定字节位置的字节值。</para>
    /// Reads a byte value from the round key at the specified byte position.
    /// </summary>
    /// <param name="rkey"><para>轮密钥数组。</para>The round key array.</param>
    /// <param name="byteIndex"><para>字节索引。</para>The byte index.</param>
    /// <returns><para>指定位置的字节值。</para>The byte value at the specified position.</returns>
    internal static byte GetRkeyByte(uint[] rkey, int byteIndex)
    {
        var wordIndex = byteIndex / 4;
        var byteOffset = byteIndex % 4;
        return (byte)(rkey[wordIndex] >> (byteOffset * 8));
    }

    /// <summary>
    /// <para>使用 OPS 自定义密码加密数据（就地操作）。</para>
    /// Encrypts data using the OPS custom cipher (in-place operation).
    /// </summary>
    /// <param name="data"><para>要加密的数据，加密后直接在此缓冲区中更新。</para>The data to encrypt; updated directly in this buffer after encryption.</param>
    /// <param name="rkey"><para>轮密钥（4 个 uint32 值），操作过程中会被修改。</para>The round key (4 uint32 values); modified during the operation.</param>
    /// <param name="expandedSBox"><para>扩展 S-Box 字节数组。</para>The expanded S-Box byte array.</param>
    /// <param name="mbox"><para>MBox 常量字节数组。</para>The MBox constants byte array.</param>
    /// <param name="outlength"><para>输出长度偏移量，0 表示处理全部数据；非零值表示跳过前 outlength 字节。</para>Output length offset; 0 means process all data; non-zero means skip the first outlength bytes.</param>
    internal static void Encrypt(Span<byte> data, uint[] rkey, ReadOnlySpan<byte> expandedSBox, ReadOnlySpan<byte> mbox, int outlength = 0)
    {
        if (data.Length == 0)
        {
            return;
        }

        ProcessBlocks(data, rkey, expandedSBox, mbox, outlength, encrypting: true);
    }

    /// <summary>
    /// <para>使用 OPS 自定义密码解密数据（就地操作）。</para>
    /// Decrypts data using the OPS custom cipher (in-place operation).
    /// </summary>
    /// <param name="data"><para>要解密的数据，解密后直接在此缓冲区中更新。</para>The data to decrypt; updated directly in this buffer after decryption.</param>
    /// <param name="rkey"><para>轮密钥（4 个 uint32 值），操作过程中会被修改。</para>The round key (4 uint32 values); modified during the operation.</param>
    /// <param name="expandedSBox"><para>扩展 S-Box 字节数组。</para>The expanded S-Box byte array.</param>
    /// <param name="mbox"><para>MBox 常量字节数组。</para>The MBox constants byte array.</param>
    /// <param name="outlength"><para>输出长度偏移量，0 表示处理全部数据；非零值表示跳过前 outlength 字节。</para>Output length offset; 0 means process all data; non-zero means skip the first outlength bytes.</param>
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

        // Handle partial first block (when outlength > 0, skip the first outlength bytes)
        if (outlength is > 0 and < BlockSize)
        {
            // Process bytes from outlength to the end of the first 16-byte block
            // by XORing each byte with the corresponding rkey byte
            for (var i = outlength; i < BlockSize && offset + i < data.Length; i++)
            {
                var rkeyByte = GetRkeyByte(rkey, i);
                data[offset + i] ^= rkeyByte;
            }

            offset = Math.Min(BlockSize, data.Length);

            // KeyUpdate after partial first block (both encrypt and decrypt must advance rkey state)
            KeyUpdateInPlace(rkey, mbox, expandedSBox);
        }

        // Process full 16-byte blocks
        while (offset + BlockSize <= data.Length)
        {
            ProcessBlock(data.Slice(offset, BlockSize), rkey, expandedSBox, mbox, encrypting);
            offset += BlockSize;
        }

        // Process remaining tail bytes (sub-block)
        if (offset < data.Length)
        {
            ProcessSubBlock(data[offset..], rkey, expandedSBox, encrypting);
        }
    }

    private static void ProcessBlock(Span<byte> block, uint[] rkey, ReadOnlySpan<byte> expandedSBox, ReadOnlySpan<byte> mbox, bool encrypting)
    {
        // Step 1: KeyUpdate first (both encrypt and decrypt)
        KeyUpdateInPlace(rkey, mbox, expandedSBox);

        // Step 2: XOR block with rkey bytes (process as 4 uint32 words)
        for (var i = 0; i < 4; i++)
        {
            var inputWord = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(i * 4, 4));
            var outputWord = inputWord ^ rkey[i];
            BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(i * 4, 4), outputWord);
        }

        // Step 3: Update rkey for next block chaining
        if (encrypting)
        {
            // rkey = tmp = output (ciphertext, which is now in block)
            for (var i = 0; i < 4; i++)
            {
                rkey[i] = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(i * 4, 4));
            }
        }
        else
        {
            // For decrypt: rkey = original input (ciphertext)
            // Since output = input ^ rkey, then input = output ^ rkey
            for (var i = 0; i < 4; i++)
            {
                // block[i] is now plaintext, rkey[i] is still the key after KeyUpdate
                // original ciphertext = plaintext ^ key = block[i] ^ rkey[i]
                rkey[i] = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(i * 4, 4)) ^ rkey[i];
            }
        }
    }

    private static void ProcessSubBlock(Span<byte> block, uint[] rkey, ReadOnlySpan<byte> expandedSBox, bool encrypting)
    {
        // KeyUpdate using expandedSBox (sbox) for tail bytes, not mbox
        KeyUpdateInPlace(rkey, expandedSBox, expandedSBox);

        // Process tail bytes in 4-byte groups
        var pos = 0;
        var m = 0;
        while (pos < block.Length)
        {
            // Read up to 4 bytes, pad with zeros if needed
            uint tmp = 0;
            var bytesToRead = Math.Min(4, block.Length - pos);
            for (var j = 0; j < bytesToRead; j++)
            {
                tmp |= (uint)(block[pos + j] << (j * 8));
            }

            // XOR with rkey
            var output = tmp ^ rkey[m];

            // Write output bytes back to block
            var bytesToWrite = Math.Min(4, block.Length - pos);
            for (var j = 0; j < bytesToWrite; j++)
            {
                block[pos + j] = (byte)((output >> (j * 8)) & 0xFF);
            }

            // Update rkey for chaining
            if (encrypting)
            {
                // rkey[m] = tmp ^ rkey[m] = output (ciphertext)
                rkey[m] = output;
            }
            else
            {
                // rkey[m] = tmp (original ciphertext)
                rkey[m] = tmp;
            }

            pos += 4;
            m++;
        }
    }
}