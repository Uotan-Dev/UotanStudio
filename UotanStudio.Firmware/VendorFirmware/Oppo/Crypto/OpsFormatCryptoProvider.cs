using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.VendorFirmware.Oppo.Exceptions;
using UotanStudio.Firmware.VendorFirmware.Oppo.Models;

namespace UotanStudio.Firmware.VendorFirmware.Oppo.Crypto;

/// <summary>
/// Crypto provider for OPS firmware payloads, implementing the symmetric OPS stream cipher driven by
/// the shared core key plus per-key <c>mbox</c> material. Custom keys added at runtime are kept in
/// memory only for the lifetime of this instance and are cleared by <see cref="Clear"/>.
/// <para>
/// OPS 固件负载的加密提供者，实现由共享核心密钥与每条密钥各自的 <c>mbox</c> 材料驱动的对称 OPS 流密码。
/// 运行时添加的自定义密钥仅在本实例生命周期内保存在内存中，并由 <see cref="Clear"/> 清除。
/// </para>
/// </summary>
public sealed class OpsFormatCryptoProvider : IOppFormatCryptoProvider
{
    private readonly ConcurrentDictionary<string, uint[]> _opsRkeyCache = new();
    private readonly ConcurrentDictionary<string, OpsKeyEntry> _customKeys = new();

    /// <summary>
    /// The OPS format this provider handles.
    /// <para>本提供者所处理的 OPS 格式。</para>
    /// </summary>
    /// <value>
    /// Always <see cref="OppFormat.Ops"/>; it is the registration key used by <see cref="OppCryptoProvider"/>.
    /// <para>恒为 <see cref="OppFormat.Ops"/>；即 <see cref="OppCryptoProvider"/> 注册时使用的键。</para>
    /// </value>
    public OppFormat SupportedFormat => OppFormat.Ops;

    /// <summary>
    /// Decrypts an OPS payload block with the named key.
    /// <para>使用指定密钥解密 OPS 负载块。</para>
    /// </summary>
    /// <param name="data">
    /// Ciphertext bytes; left untouched, the result is a separate array.
    /// <para>密文字节；保持原样不变，结果为独立数组。</para>
    /// </param>
    /// <param name="keyId">
    /// OPS key identifier, for example <c>mbox5</c>; resolved against the built-in table first and the
    /// custom keys second.
    /// <para>
    /// OPS 密钥标识符（例如 <c>mbox5</c>）；先在内置表中解析，再在自定义密钥中解析。
    /// </para>
    /// </param>
    /// <returns>
    /// The decrypted bytes, exactly as long as <paramref name="data"/>.
    /// <para>解密后的字节，长度与 <paramref name="data"/> 完全一致。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="OppDecryptionException">
    /// <paramref name="keyId"/> matches neither a built-in nor a custom key.
    /// <para><paramref name="keyId"/> 在内置密钥与自定义密钥中均无匹配。</para>
    /// </exception>
    public byte[] Decrypt(byte[] data, string keyId)
    {
        Guard.ThrowIfNull(data);

        var entry = FindKey(keyId)
            ?? throw new OppDecryptionException($"OPS key '{keyId}' not found in the key database.");

        var buffer = new byte[data.Length];
        data.AsSpan().CopyTo(buffer);
        var rkey = GetOrInitOpsRkey(entry);
        OppOpsCipher.Decrypt(buffer, rkey, OpsKeyDatabase.ExpandedSBox, entry.MBox);
        return buffer;
    }

    /// <summary>
    /// Encrypts a block with the named OPS key, the exact inverse of <see cref="Decrypt(byte[],string)"/>.
    /// <para>使用指定的 OPS 密钥加密数据块，是 <see cref="Decrypt(byte[],string)"/> 的完全逆运算。</para>
    /// </summary>
    /// <param name="data">
    /// Plaintext bytes; left untouched, the result is a separate array.
    /// <para>明文字节；保持原样不变，结果为独立数组。</para>
    /// </param>
    /// <param name="keyId">
    /// OPS key identifier, for example <c>mbox5</c>; resolved against the built-in table first and the
    /// custom keys second.
    /// <para>
    /// OPS 密钥标识符（例如 <c>mbox5</c>）；先在内置表中解析，再在自定义密钥中解析。
    /// </para>
    /// </param>
    /// <returns>
    /// The encrypted bytes, exactly as long as <paramref name="data"/>.
    /// <para>加密后的字节，长度与 <paramref name="data"/> 完全一致。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="OppDecryptionException">
    /// <paramref name="keyId"/> matches neither a built-in nor a custom key.
    /// <para><paramref name="keyId"/> 在内置密钥与自定义密钥中均无匹配。</para>
    /// </exception>
    public byte[] Encrypt(byte[] data, string keyId)
    {
        Guard.ThrowIfNull(data);

        var entry = FindKey(keyId)
            ?? throw new OppDecryptionException($"OPS key '{keyId}' not found in the key database.");

        var buffer = new byte[data.Length];
        data.AsSpan().CopyTo(buffer);
        var rkey = GetOrInitOpsRkey(entry);
        OppOpsCipher.Encrypt(buffer, rkey, OpsKeyDatabase.ExpandedSBox, entry.MBox);
        return buffer;
    }

    /// <summary>
    /// Reads the whole input stream into memory, decrypts it with the named OPS key, and appends the
    /// result to <paramref name="output"/>. Neither stream is repositioned or closed.
    /// <para>将整个输入流读入内存，使用指定的 OPS 密钥解密，并把结果追加写入 <paramref name="output"/>。两个流都不会被重定位或关闭。</para>
    /// </summary>
    /// <param name="input">
    /// Readable stream positioned at the start of the ciphertext; consumed to its end.
    /// <para>定位在密文起始处的可读流，会被读取到末尾。</para>
    /// </param>
    /// <param name="output">
    /// Writable stream that receives the plaintext at its current position.
    /// <para>在当前写入位置接收明文的 writable 流。</para>
    /// </param>
    /// <param name="keyId">
    /// OPS key identifier, for example <c>mbox5</c>.
    /// <para>OPS 密钥标识符（例如 <c>mbox5</c>）。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Reserved for the streaming signature; this implementation reads the input to the end and never
    /// polls the token.
    /// <para>为流式签名保留；本实现会把输入读到末尾，不检查该取消令牌。</para>
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="OppDecryptionException">
    /// <paramref name="keyId"/> matches neither a built-in nor a custom key.
    /// <para><paramref name="keyId"/> 在内置密钥与自定义密钥中均无匹配。</para>
    /// </exception>
    public void Decrypt(Stream input, Stream output, string keyId, CancellationToken cancellationToken)
    {
        Guard.ThrowIfNull(input);
        Guard.ThrowIfNull(output);

        var entry = FindKey(keyId)
            ?? throw new OppDecryptionException($"OPS key '{keyId}' not found in the key database.");


        using var tempStream = new MemoryStream();
        input.CopyTo(tempStream);
        var data = tempStream.ToArray();

        var rkey = GetOrInitOpsRkey(entry);
        OppOpsCipher.Decrypt(data, rkey, OpsKeyDatabase.ExpandedSBox, entry.MBox);
        output.Write(data, 0, data.Length);
    }

    /// <summary>
    /// Reads the whole input stream into memory, encrypts it with the named OPS key, and appends the
    /// result to <paramref name="output"/>. Neither stream is repositioned or closed.
    /// <para>将整个输入流读入内存，使用指定的 OPS 密钥加密，并把结果追加写入 <paramref name="output"/>。两个流都不会被重定位或关闭。</para>
    /// </summary>
    /// <param name="input">
    /// Readable stream positioned at the start of the plaintext; consumed to its end.
    /// <para>定位在明文起始处的可读流，会被读取到末尾。</para>
    /// </param>
    /// <param name="output">
    /// Writable stream that receives the ciphertext at its current position.
    /// <para>在当前写入位置接收密文的可写流。</para>
    /// </param>
    /// <param name="keyId">
    /// OPS key identifier, for example <c>mbox5</c>.
    /// <para>OPS 密钥标识符（例如 <c>mbox5</c>）。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Reserved for the streaming signature; this implementation reads the input to the end and never
    /// polls the token.
    /// <para>为流式签名保留；本实现会把输入读到末尾，不检查该取消令牌。</para>
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="OppDecryptionException">
    /// <paramref name="keyId"/> matches neither a built-in nor a custom key.
    /// <para><paramref name="keyId"/> 在内置密钥与自定义密钥中均无匹配。</para>
    /// </exception>
    public void Encrypt(Stream input, Stream output, string keyId, CancellationToken cancellationToken)
    {
        Guard.ThrowIfNull(input);
        Guard.ThrowIfNull(output);

        var entry = FindKey(keyId)
            ?? throw new OppDecryptionException($"OPS key '{keyId}' not found in the key database.");


        using var tempStream = new MemoryStream();
        input.CopyTo(tempStream);
        var data = tempStream.ToArray();

        var rkey = GetOrInitOpsRkey(entry);
        OppOpsCipher.Encrypt(data, rkey, OpsKeyDatabase.ExpandedSBox, entry.MBox);
        output.Write(data, 0, data.Length);
    }

    /// <summary>
    /// Automatically infers the key and decrypts data, trying all OPS keys.
    /// <para>自动推断密钥并解密数据，尝试所有 OPS 密钥。</para>
    /// </summary>
    /// <param name="data">
    /// Ciphertext of an OPS XML block; the buffer is never modified.
    /// <para>OPS XML 块的密文；传入数组不会被修改。</para>
    /// </param>
    /// <returns>
    /// UTF-8 plaintext cut after the last <c>&gt;</c> when a <c>&lt;?xml</c> declaration is found under a
    /// built-in key, the untruncated bytes when the declaration sits at offset zero and no trailing junk
    /// follows, or <see langword="null"/> when no built-in key yields an XML declaration. Custom keys are
    /// not part of the search.
    /// <para>
    /// 当某个内置密钥可解出 <c>&lt;?xml</c> 声明时，返回在最后一个 <c>&gt;</c> 之后截断的 UTF-8 明文；
    /// 声明位于偏移 0 且其后无多余字节时返回未截断的字节；无任何内置密钥解出 XML 声明时返回
    /// <see langword="null"/>。自定义密钥不参与搜索。
    /// </para>
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    public byte[]? AutoDecrypt(byte[] data)
    {
        Guard.ThrowIfNull(data);


        var buffer = new byte[data.Length];

        foreach (var entry in OpsKeyDatabase.Entries)
        {
            try
            {
                data.AsSpan().CopyTo(buffer);


                var rkey = GetOrInitOpsRkey(entry);

                OppOpsCipher.Decrypt(buffer, rkey, OpsKeyDatabase.ExpandedSBox, entry.MBox);

                var text = Encoding.UTF8.GetString(buffer);


                if (text.IndexOf("<?xml", StringComparison.Ordinal) >= 0)
                {
                    return TruncateAndCopy(buffer, text);
                }
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {

            }
        }

        return null;
    }

    /// <summary>
    /// Automatically detects OPS key and decrypts XML data.
    /// <para>自动探测 OPS 密钥并解密 XML 数据。</para>
    /// </summary>
    /// <param name="encryptedXml">
    /// Ciphertext of an OPS XML block; the array is never modified.
    /// <para>OPS XML 块的密文；传入数组不会被修改。</para>
    /// </param>
    /// <returns>
    /// The decrypted XML, starting at its <c>&lt;?xml</c> declaration and cut after the last
    /// <c>&gt;</c>, paired with the <c>mbox</c> identifier that produced it; <see langword="null"/> when
    /// no built-in key yields an XML declaration. Keys that throw a malformed-text error are skipped.
    /// <para>
    /// 解密后的 XML，从其 <c>&lt;?xml</c> 声明起、在最后一个 <c>&gt;</c> 之后截断，并附上产生该结果的
    /// <c>mbox</c> 标识符；无任何内置密钥解出 XML 声明时返回 <see langword="null"/>。抛出文本格式错误的密钥会被跳过。
    /// </para>
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="encryptedXml"/> is <see langword="null"/>.
    /// <para><paramref name="encryptedXml"/> 为 <see langword="null"/>。</para></exception>
    public (string Xml, string KeyId)? AutoDecryptOpsXml(byte[] encryptedXml)
    {
        Guard.ThrowIfNull(encryptedXml);


        var buffer = new byte[encryptedXml.Length];

        foreach (var entry in OpsKeyDatabase.Entries)
        {
            try
            {
                encryptedXml.AsSpan().CopyTo(buffer);


                var rkey = GetOrInitOpsRkey(entry);

                OppOpsCipher.Decrypt(buffer, rkey, OpsKeyDatabase.ExpandedSBox, entry.MBox);

                var text = Encoding.UTF8.GetString(buffer);


                var xmlDeclIndex = text.IndexOf("<?xml", StringComparison.Ordinal);
                if (xmlDeclIndex >= 0)
                {

                    var cleaned = xmlDeclIndex > 0 ? text[xmlDeclIndex..] : text;
                    return (TruncateXmlAtLastCloseTag(cleaned), entry.KeyId);
                }
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {

            }
        }

        return null;
    }

    /// <summary>
    /// Queries whether the specified key identifier is supported.
    /// <para>查询是否支持指定的密钥标识符。</para>
    /// </summary>
    /// <param name="keyId">
    /// Key identifier to test; built-in identifiers match case-insensitively, custom ones only under the
    /// exact string they were added with.
    /// <para>待测试的密钥标识符；内置标识符按不区分大小写匹配，自定义标识符必须与添加时的字符串完全一致。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when a built-in or custom OPS key carries that identifier.
    /// <para>内置或自定义 OPS 密钥中存在该标识符时为 <see langword="true"/>。</para>
    /// </returns>
    public bool IsSupported(string keyId) => OpsKeyDatabase.FindByKeyId(keyId) != null || _customKeys.ContainsKey(keyId);

    /// <summary>
    /// Gets the list of all known key identifiers for this format.
    /// <para>获取此格式的所有已知密钥标识符列表。</para>
    /// </summary>
    /// <returns>
    /// Built-in identifiers in table order, followed by the custom ones in unspecified order; the list is
    /// a fresh copy, so an identifier added twice through <see cref="AddCustomKey"/> appears once.
    /// <para>按表顺序列出内置标识符，其后以不特定顺序列出自定义标识符；返回列表为新副本，通过 <see cref="AddCustomKey"/> 重复添加的标识符只出现一次。</para>
    /// </returns>
    public IReadOnlyList<string> GetKnownKeys()
    {
        var result = new List<string>();
        foreach (var entry in OpsKeyDatabase.Entries)
        {
            result.Add(entry.KeyId);
        }

        foreach (var keyId in _customKeys.Keys)
        {
            result.Add(keyId);
        }

        return result;
    }

    /// <summary>
    /// Adds a custom key.
    /// <para>添加自定义密钥。</para>
    /// </summary>
    /// <param name="keyEntry">
    /// An <see cref="OpsKeyEntry"/> instance. Reusing an existing identifier replaces that entry and
    /// drops its cached round key, so the next operation re-derives it.
    /// <para><see cref="OpsKeyEntry"/> 实例。复用已有标识符会替换该条目并丢弃其缓存轮密钥，下次操作时重新派生。</para>
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="keyEntry"/> is <see langword="null"/>.
    /// <para><paramref name="keyEntry"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="keyEntry"/> is not an <see cref="OpsKeyEntry"/>.
    /// <para><paramref name="keyEntry"/> 不是 <see cref="OpsKeyEntry"/>。</para>
    /// </exception>
    public void AddCustomKey(object keyEntry)
    {
        Guard.ThrowIfNull(keyEntry);

        if (keyEntry is OpsKeyEntry opsEntry)
        {
            _opsRkeyCache.TryRemove(opsEntry.KeyId, out _);
            _customKeys[opsEntry.KeyId] = opsEntry;
            return;
        }

        throw new ArgumentException($"Unsupported key entry type: {keyEntry.GetType().Name}. Expected OpsKeyEntry.", nameof(keyEntry));
    }

    /// <summary>
    /// Exports discovered keys.
    /// <para>导出发现的密钥。</para>
    /// </summary>
    /// <param name="filePath">
    /// Destination path accepted for interface uniformity; this provider keeps no auto-discovered key
    /// material of its own, so the call creates no file and writes nothing.
    /// <para>为保持接口一致而接收的目标路径；本提供者不保存自行发现的密钥材料，因此该调用不创建文件也不写入内容。</para>
    /// </param>
    public void ExportDiscoveredKeys(string filePath)
    {

    }

    /// <summary>
    /// Clears all key data.
    /// <para>清除所有密钥数据。</para>
    /// </summary>
    public void Clear()
    {
        foreach (var entry in _opsRkeyCache.Values)
        {
            Array.Clear(entry, 0, entry.Length);
        }

        _opsRkeyCache.Clear();
        _customKeys.Clear();
    }

    private OpsKeyEntry? FindKey(string keyId) => OpsKeyDatabase.FindByKeyId(keyId) ?? (_customKeys.TryGetValue(keyId, out var custom) ? custom : null);

    private uint[] GetOrInitOpsRkey(OpsKeyEntry entry)
    {
        var cached = _opsRkeyCache.GetOrAdd(entry.KeyId, _ => InitOpsRkey());


        var copy = new uint[cached.Length];
        Array.Copy(cached, copy, cached.Length);
        return copy;
    }

    private static uint[] InitOpsRkey()
    {
        var coreKey = OpsKeyDatabase.OpsCoreKey;
        var rkey = new uint[4];
        rkey[0] = BinaryPrimitives.ReadUInt32LittleEndian(coreKey.AsSpan(0, 4));
        rkey[1] = BinaryPrimitives.ReadUInt32LittleEndian(coreKey.AsSpan(4, 4));
        rkey[2] = BinaryPrimitives.ReadUInt32LittleEndian(coreKey.AsSpan(8, 4));
        rkey[3] = BinaryPrimitives.ReadUInt32LittleEndian(coreKey.AsSpan(12, 4));
        return rkey;
    }

    private static string TruncateXmlAtLastCloseTag(string text)
    {
        var lastCloseTag = text.LastIndexOf('>');
        return lastCloseTag >= 0 && lastCloseTag < text.Length - 1 ? text[..(lastCloseTag + 1)] : text;
    }

    private static byte[] TruncateAndCopy(byte[] buffer, string text)
    {
        var lastCloseTag = text.LastIndexOf('>');
        if (lastCloseTag >= 0 && lastCloseTag < text.Length - 1)
        {
            var truncatedText = text[..(lastCloseTag + 1)];
            return Encoding.UTF8.GetBytes(truncatedText);
        }

        var result = new byte[buffer.Length];
        Array.Copy(buffer, result, buffer.Length);
        return result;
    }
}