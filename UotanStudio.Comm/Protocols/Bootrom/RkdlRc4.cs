namespace UotanStudio.Comm.Protocols.Bootrom;

/// <summary>
/// Vendor RC4 stream cipher the legacy RK28 ID-block path applies to loader data, loader code and ID-block sectors,
/// reproducing <c>P_RC4</c> byte for byte.
/// <para>旧版 RK28 ID block 路径对 loader 数据、loader 代码与 ID block 扇区施加的厂商 RC4 流密码，逐字节复现
/// <c>P_RC4</c>。</para>
/// </summary>
/// <remarks>
/// The key schedule is not standard RC4: <c>P_RC4</c> rebuilds <c>K[i] = key[i % 16]</c> by masking the index with
/// <c>0x0f</c>, so the 16-byte key repeats instead of being consumed once. The state is rebuilt for every call, so a
/// sector is always encrypted from a fresh keystream; <see cref="EncryptSectors"/> therefore resets per sector rather
/// than continuing the stream. This cipher is vendor-specific and stays local to the protocol layer instead of joining
/// the checksum and digest surface of <c>UotanStudio.Hash</c>.
/// <para>密钥编排并非标准 RC4：<c>P_RC4</c> 用 <c>0x0f</c> 掩码索引重建 <c>K[i] = key[i % 16]</c>，16 字节密钥因此
/// 循环使用而不是只消费一次。每次调用都重建状态，所以每个扇区总是从新的密钥流开始加密；
/// <see cref="EncryptSectors"/> 因此按扇区重置而不是延续流。该密码是厂商私有实现，留在协议层，不并入
/// <c>UotanStudio.Hash</c> 的校验和与摘要接口。</para>
/// </remarks>
internal static class RkdlRc4
{
    private static ReadOnlySpan<byte> Key =>
    [
        124, 78, 3, 4, 85, 5, 9, 7, 45, 44, 123, 56, 23, 13, 23, 17,
    ];

    /// <summary>
    /// Encrypts or decrypts one sector in place with a fresh <c>P_RC4</c> state.
    /// <para>用全新的 <c>P_RC4</c> 状态就地加密或解密一个扇区。</para>
    /// </summary>
    /// <param name="sector">The sector bytes to transform in place; the length is the keystream length.
    /// <para>就地变换的扇区字节；其长度即密钥流长度。</para></param>
    public static void Encrypt(Span<byte> sector) => EncryptCore(sector);

    /// <summary>
    /// Encrypts or decrypts every whole 512-byte sector of <paramref name="data"/> in place, each with its own keystream,
    /// and leaves a trailing partial sector untouched, exactly like the <c>size / SECTOR_SIZE</c> loops of
    /// <c>MakeIDBlockData</c> and <c>upgrade_loader</c>.
    /// <para>用各自的密钥流就地加密或解密 <paramref name="data"/> 中每个完整的 512 字节扇区，末尾不足一个扇区的部分
    /// 保持不变，与 <c>MakeIDBlockData</c> 和 <c>upgrade_loader</c> 的 <c>size / SECTOR_SIZE</c> 循环一致。</para>
    /// </summary>
    /// <param name="data">The buffer whose whole sectors are transformed in place. <para>就地变换整扇区的缓冲区。</para></param>
    public static void EncryptSectors(Span<byte> data)
    {
        for (var offset = 0; offset + RkdlConstants.SectorSize <= data.Length; offset += RkdlConstants.SectorSize)
        {
            EncryptCore(data.Slice(offset, RkdlConstants.SectorSize));
        }
    }

    /// <summary>
    /// Runs the <c>P_RC4</c> key schedule and keystream over one buffer. <para>在一个缓冲区上执行 <c>P_RC4</c>
    /// 的密钥编排与密钥流。</para>
    /// </summary>
    /// <param name="buffer">The buffer transformed in place. <para>就地变换的缓冲区。</para></param>
    private static void EncryptCore(Span<byte> buffer)
    {
        Span<byte> state = stackalloc byte[256];
        Span<byte> schedule = stackalloc byte[256];
        var key = Key;

        var j = 0;
        for (var i = 0; i < 256; i++)
        {
            state[i] = (byte)i;
            j &= 0x0f;
            schedule[i] = key[j];
            j++;
        }

        j = 0;
        for (var i = 0; i < 256; i++)
        {
            j = (j + state[i] + schedule[i]) & 0xff;
            (state[i], state[j]) = (state[j], state[i]);
        }

        int x = 0, y = 0;
        for (var index = 0; index < buffer.Length; index++)
        {
            x = (x + 1) & 0xff;
            y = (y + state[x]) & 0xff;
            (state[x], state[y]) = (state[y], state[x]);
            buffer[index] ^= state[(state[x] + state[y]) & 0xff];
        }
    }
}