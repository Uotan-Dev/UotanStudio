using System.Security.Cryptography;

namespace UotanStudio.Mirroring.AirPlay.FairPlay;

/// <summary>
/// Recovers the mirroring AES key and IV from the stream <c>ekey</c> and <c>eiv</c> values with an
/// AES-128-CBC unwrap, and applies AES-128-CTR to the mirrored payload.
/// <para>用 AES-128-CBC 解包从流 <c>ekey</c> 与 <c>eiv</c> 取回镜像的 AES 密钥与 IV，并对镜像载荷应用
/// AES-128-CTR。</para>
/// </summary>
/// <remarks>
/// <para>
/// This is a clean-room implementation driven by the published AirPlay handshake behaviour; no GPL playfair,
/// UxPlay, RPiPlay or shairplay source was translated. It ships no key table: the unwrap key and IV are operator
/// supplied through <see cref="AirPlayOptions.FairPlayKeyPath"/> (32 raw bytes: 16-byte AES key followed by
/// 16-byte IV), because publishing the reverse-engineered table would need a redistribution review first. Until
/// material is configured, an encrypted stream is rejected instead of being decoded into garbage.
/// </para>
/// <para>
/// 本实现依据公开的 AirPlay 握手行为做清理室实现，未逐行翻译任何 GPL 的 playfair、UxPlay、RPiPlay 或
/// shairplay 源码。它不随包提供密钥表：解包密钥与 IV 由操作者经
/// <see cref="AirPlayOptions.FairPlayKeyPath"/> 提供（32 原始字节：16 字节 AES 密钥后接 16 字节 IV），
/// 因为发布逆向得到的密钥表需要先完成再分发评审。在配置密钥材料之前，加密流会被拒绝，而不是被解成乱码。
/// </para>
/// </remarks>
internal sealed class FairPlayPlayCipher
{
    private const int EkeyBlockBytes = 16;
    private const int KeyMaterialBytes = 32;
    private readonly byte[] _unwrapKey;
    private readonly byte[] _unwrapIv;

    private FairPlayPlayCipher(byte[] unwrapKey, byte[] unwrapIv)
    {
        _unwrapKey = unwrapKey;
        _unwrapIv = unwrapIv;
    }

    /// <summary>Loads the operator-supplied unwrap key material. <para>加载操作者提供的解包密钥材料。</para></summary>
    /// <param name="path">File holding 32 raw bytes; an empty path means no material is configured.
    /// <para>存放 32 原始字节的文件；空路径表示未配置密钥材料。</para></param>
    /// <param name="logSink">Optional diagnostic sink. <para>可选的诊断接收端。</para></param>
    /// <returns>The cipher, or <see langword="null"/> when no usable material is available.
    /// <para>密码器；没有可用材料时为 <see langword="null"/>。</para></returns>
    public static FairPlayPlayCipher? TryCreate(string path, Action<string>? logSink)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            logSink?.Invoke(
                "AirPlay FairPlay key material is not configured; encrypted mirroring streams cannot be decoded.");
            return null;
        }

        byte[] material;
        try
        {
            material = File.ReadAllBytes(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            logSink?.Invoke("AirPlay FairPlay key material could not be read: " + error.Message);
            return null;
        }

        if (material.Length < KeyMaterialBytes)
        {
            logSink?.Invoke(
                "AirPlay FairPlay key material must hold 32 raw bytes: a 16-byte key followed by a 16-byte IV.");
            return null;
        }

        var key = material.AsSpan(0, 16).ToArray();
        var iv = material.AsSpan(16, 16).ToArray();
        CryptographicOperations.ZeroMemory(material.AsSpan(KeyMaterialBytes).ToArray());
        return new FairPlayPlayCipher(key, iv);
    }

    /// <summary>Unwraps the stream key into the AES-128 key and IV used by the mirroring channel.
    /// <para>把流密钥解包为镜像通道使用的 AES-128 密钥与 IV。</para>
    /// </summary>
    /// <param name="ekey">Encrypted key bytes from the ANNOUNCE payload. <para>ANNOUNCE 载荷中的加密密钥字节。</para></param>
    /// <param name="eiv">Encrypted IV bytes, or an empty span when the client sent none.
    /// <para>加密的 IV 字节；客户端未发送时为空跨度。</para></param>
    /// <param name="aesKey">Receives the 16-byte AES key. <para>接收 16 字节 AES 密钥。</para></param>
    /// <param name="aesIv">Receives the 16-byte AES IV; all zero when the client sent no encrypted IV.
    /// <para>接收 16 字节 AES IV；客户端未发送加密 IV 时全为零。</para></param>
    /// <returns><see langword="true"/> when both values were produced.
    /// <para>两个值都成功产生时为 <see langword="true"/>。</para></returns>
    public bool TryDecryptEkey(ReadOnlySpan<byte> ekey, ReadOnlySpan<byte> eiv, out byte[] aesKey,
        out byte[] aesIv)
    {
        aesKey = new byte[16];
        aesIv = new byte[16];
        if (ekey.Length < EkeyBlockBytes)
        {
            return false;
        }

        using var aes = Aes.Create();
        aes.Key = _unwrapKey;
        aes.IV = _unwrapIv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using var decryptor = aes.CreateDecryptor();
        var padded = Pad(ekey);
        var plain = decryptor.TransformFinalBlock(padded, 0, padded.Length);
        if (plain.Length < 16)
        {
            return false;
        }

        plain.AsSpan(0, 16).CopyTo(aesKey);
        if (eiv.Length >= EkeyBlockBytes)
        {
            using var ivDecryptor = aes.CreateDecryptor();
            var ivPadded = Pad(eiv);
            var ivPlain = ivDecryptor.TransformFinalBlock(ivPadded, 0, ivPadded.Length);
            if (ivPlain.Length >= 16)
            {
                ivPlain.AsSpan(0, 16).CopyTo(aesIv);
            }
        }

        return true;
    }

    private static byte[] Pad(ReadOnlySpan<byte> value)
    {
        var length = (value.Length + 15) / 16 * 16;
        var buffer = new byte[length];
        value.CopyTo(buffer);
        return buffer;
    }
}

/// <summary>
/// Stateful AES-128-CTR transform for one mirrored channel: the counter advances across packets, so every
/// payload of a stream is decrypted with the continuation of the previous keystream block.
/// <para>单个镜像通道的有状态 AES-128-CTR 变换：计数器跨包推进，因此一条流的每个载荷都延续上一个密钥流块。</para>
/// </summary>
internal sealed class AesCtrCipher : IDisposable
{
    private readonly Aes _aes;
    private readonly ICryptoTransform _encryptor;
    private readonly byte[] _counter;
    private readonly byte[] _keystream = new byte[16];
    private int _keystreamOffset = 16;
    private int _disposed;

    /// <summary>Creates a cipher over one 16-byte key and one 16-byte initial counter.
    /// <para>基于一个 16 字节密钥与一个 16 字节初始计数器创建密码器。</para>
    /// </summary>
    /// <param name="key">AES-128 key. <para>AES-128 密钥。</para></param>
    /// <param name="initialCounter">Initial counter block. <para>初始计数器块。</para></param>
    /// <exception cref="ArgumentException">The key or counter is not 16 bytes.
    /// <para>密钥或计数器不是 16 字节。</para></exception>
    public AesCtrCipher(ReadOnlySpan<byte> key, ReadOnlySpan<byte> initialCounter)
    {
        if (key.Length != 16)
        {
            throw new ArgumentException("The AES-128 key must be 16 bytes.", nameof(key));
        }

        if (initialCounter.Length != 16)
        {
            throw new ArgumentException("The AES-CTR counter must be 16 bytes.", nameof(initialCounter));
        }

        _aes = Aes.Create();
        _aes.Key = key.ToArray();
        _aes.Mode = CipherMode.ECB;
        _aes.Padding = PaddingMode.None;
        _encryptor = _aes.CreateEncryptor();
        _counter = initialCounter.ToArray();
    }

    /// <summary>Decrypts one payload in place and advances the counter. <para>就地解密一个载荷并推进计数器。</para></summary>
    /// <param name="payload">Bytes to transform; may be empty. <para>要变换的字节；可为空。</para></param>
    public void DecryptInPlace(Span<byte> payload)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var offset = 0;
        while (offset < payload.Length)
        {
            if (_keystreamOffset == 16)
            {
                _encryptor.TransformBlock(_counter, 0, 16, _keystream, 0);
                _keystreamOffset = 0;
                IncrementCounter();
            }

            var count = Math.Min(16 - _keystreamOffset, payload.Length - offset);
            for (var index = 0; index < count; index++)
            {
                payload[offset + index] ^= _keystream[_keystreamOffset + index];
            }

            offset += count;
            _keystreamOffset += count;
        }
    }

    private void IncrementCounter()
    {
        for (var index = _counter.Length - 1; index >= 0; index--)
        {
            if (++_counter[index] != 0)
            {
                return;
            }
        }
    }

    /// <summary>Releases the AES instance. <para>释放 AES 实例。</para></summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _encryptor.Dispose();
        _aes.Dispose();
    }
}