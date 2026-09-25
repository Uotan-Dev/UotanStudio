using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Crypto.KeyDatabase;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Exceptions;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Models;
using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Crypto;

/// <summary>
/// OZip format provider that decrypts through <see cref="OzipDecryptor"/>. Key identifiers are
/// <c>OZ-&lt;index&gt;</c> for the built-in key database, <c>OZ-custom-&lt;index&gt;</c> for keys added via
/// <see cref="AddCustomKey"/>, or a direct 32-hex-character key; encryption always fails because OZip is
/// decryption-only.
/// <para>通过 <see cref="OzipDecryptor"/> 解密的 OZip 格式提供程序。密钥标识符为：<c>OZ-&lt;索引&gt;</c> 指向内置密钥库，
/// <c>OZ-custom-&lt;索引&gt;</c> 指向通过 <see cref="AddCustomKey"/> 添加的密钥，也可以直接使用 32 位十六进制字符的密钥；
/// 由于 OZip 仅支持解密，加密操作始终失败。</para>
/// </summary>
public sealed class OzipFormatCryptoProvider : IOppFormatCryptoProvider
{
    private const string KeyIdPrefix = "OZ";

    private readonly List<OzipKeyEntry> _customKeys = [];

    /// <summary>The format served by this provider, always <see cref="OppFormat.Ozip"/>.
    /// <para>本提供程序服务的格式，始终为 <see cref="OppFormat.Ozip"/>。</para></summary>
    /// <value><see cref="OppFormat.Ozip"/> for every instance. <para>每个实例都返回 <see cref="OppFormat.Ozip"/>。</para></value>
    public OppFormat SupportedFormat => OppFormat.Ozip;

    /// <summary>
    /// Decrypts data using the specified key.
    /// <para>使用指定密钥解密数据。</para>
    /// </summary>
    /// <param name="data">The complete OZip file bytes; the whole input is buffered in memory.
    /// <para>完整的 OZip 文件字节；整个输入会被缓冲到内存中。</para></param>
    /// <param name="keyId">Key identifier: <c>OZ-&lt;index&gt;</c>, <c>OZ-custom-&lt;index&gt;</c>, or a
    /// 32-hex-character key. <para>密钥标识符：<c>OZ-&lt;索引&gt;</c>、<c>OZ-custom-&lt;索引&gt;</c>，或 32 位十六进制字符的密钥。</para></param>
    /// <returns>The decrypted payload bytes. <para>解密后的负载字节。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="keyId"/> is null, empty, or white space, or the
    /// stream checks in <see cref="OzipDecryptor.DecryptFile"/> reject the input.
    /// <para><paramref name="keyId"/> 为 null、空或仅空白，或 <see cref="OzipDecryptor.DecryptFile"/> 的流检查拒绝了输入。</para></exception>
    /// <exception cref="OppDecryptionException"><paramref name="keyId"/> resolves to no known key.
    /// <para><paramref name="keyId"/> 无法解析为已知密钥。</para></exception>
    /// <exception cref="InvalidOppFormatException">The header is invalid or the declared size exceeds the
    /// bytes actually present. <para>头部无效，或声明的大小超出实际存在的字节数。</para></exception>
    /// <exception cref="System.Security.Cryptography.CryptographicException">The ECB decryption of a prefix block did not produce a full
    /// 16-byte block. <para>某个前缀块的 ECB 解密未产生完整的 16 字节块。</para></exception>
    public byte[] Decrypt(byte[] data, string keyId)
    {
        Guard.ThrowIfNull(data);
        Guard.ThrowIfNullOrWhiteSpace(keyId);

        var keyEntry = ResolveKey(keyId) ?? throw new OppDecryptionException($"Key '{keyId}' is not supported for OZip format.");
        using var input = new MemoryStream(data);
        using var output = new MemoryStream();
        OzipDecryptor.DecryptFile(input, output, keyEntry.KeyBytes);
        return output.ToArray();
    }

    /// <summary>
    /// Encrypts data using the specified key.
    /// <para>使用指定密钥加密数据。</para>
    /// </summary>
    /// <param name="data">Input bytes; only checked for null before the call fails.
    /// <para>输入字节；调用失败前仅检查其是否为 null。</para></param>
    /// <param name="keyId">Not consulted; the call fails before key resolution.
    /// <para>不会被使用；调用在解析密钥前即失败。</para></param>
    /// <returns>Never returns; the method always throws.
    /// <para>永不返回；该方法始终抛出异常。</para></returns>
    /// <exception cref="OppDecryptionException">Always, because OZip is a decryption-only format.
    /// <para>始终抛出，因为 OZip 是仅支持解密的格式。</para></exception>
    public byte[] Encrypt(byte[] data, string keyId)
    {
        Guard.ThrowIfNull(data);

        throw new OppDecryptionException(
            "OZip encryption is not supported. OZip is a decryption-only format.");
    }

    /// <summary>
    /// Decrypts stream data using the specified key.
    /// <para>使用指定密钥解密流数据。</para>
    /// </summary>
    /// <param name="input">Readable, seekable OZip file stream; the header is validated and reading
    /// continues from offset <c>0x1050</c>. It is not closed.
    /// <para>可读取、可寻址的 OZip 文件流；校验头部后从偏移 <c>0x1050</c> 继续读取，不会被关闭。</para></param>
    /// <param name="output">Stream that receives the decrypted payload; it is not closed.
    /// <para>接收解密负载的流；不会被关闭。</para></param>
    /// <param name="keyId">Key identifier: <c>OZ-&lt;index&gt;</c>, <c>OZ-custom-&lt;index&gt;</c>, or a
    /// 32-hex-character key. <para>密钥标识符：<c>OZ-&lt;索引&gt;</c>、<c>OZ-custom-&lt;索引&gt;</c>，或 32 位十六进制字符的密钥。</para></param>
    /// <param name="cancellationToken">Checked once at entry; the subsequent bulk decryption is not
    /// cancellable. <para>仅在入口处检查一次；随后的整体解密过程不可被取消。</para></param>
    /// <exception cref="OperationCanceledException">Cancellation was already requested at entry.
    /// <para>入口处已请求取消。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="keyId"/> is null, empty, or white space, or
    /// <paramref name="input"/> is not readable or not seekable.
    /// <para><paramref name="keyId"/> 为 null、空或仅空白，或 <paramref name="input"/> 不可读取或不可寻址。</para></exception>
    /// <exception cref="OppDecryptionException"><paramref name="keyId"/> resolves to no known key.
    /// <para><paramref name="keyId"/> 无法解析为已知密钥。</para></exception>
    /// <exception cref="InvalidOppFormatException">The header is invalid or the payload ends before the
    /// declared size is consumed. <para>头部无效，或负载在消费完声明大小前提前结束。</para></exception>
    /// <exception cref="System.Security.Cryptography.CryptographicException">The ECB decryption of a prefix
    /// block did not produce a full 16-byte block.
    /// <para>某个前缀块的 ECB 解密未产生完整的 16 字节块。</para></exception>
    public void Decrypt(Stream input, Stream output, string keyId, CancellationToken cancellationToken)
    {
        Guard.ThrowIfNull(input);
        Guard.ThrowIfNull(output);
        Guard.ThrowIfNullOrWhiteSpace(keyId);

        cancellationToken.ThrowIfCancellationRequested();

        var keyEntry = ResolveKey(keyId) ?? throw new OppDecryptionException($"Key '{keyId}' is not supported for OZip format.");
        OzipDecryptor.DecryptFile(input, output, keyEntry.KeyBytes);
    }

    /// <summary>
    /// Encrypts stream data using the specified key.
    /// <para>使用指定密钥加密流数据。</para>
    /// </summary>
    /// <param name="input">Not consulted; the call fails after the null checks.
    /// <para>不会被使用；调用在空值检查后即失败。</para></param>
    /// <param name="output">Not consulted; the call fails after the null checks.
    /// <para>不会被使用；调用在空值检查后即失败。</para></param>
    /// <param name="keyId">Not consulted; the call fails before key resolution.
    /// <para>不会被使用；调用在解析密钥前即失败。</para></param>
    /// <param name="cancellationToken">Not consulted; the call fails before any streaming work.
    /// <para>不会被使用；调用在任何流式处理前即失败。</para></param>
    /// <exception cref="OppDecryptionException">Always, once both streams are verified non-null, because
    /// OZip is a decryption-only format.
    /// <para>在确认两个流均非 null 后始终抛出，因为 OZip 是仅支持解密的格式。</para></exception>
    public void Encrypt(Stream input, Stream output, string keyId, CancellationToken cancellationToken)
    {
        Guard.ThrowIfNull(input);
        Guard.ThrowIfNull(output);

        throw new OppDecryptionException(
            "OZip encryption is not supported. OZip is a decryption-only format.");
    }

    /// <summary>
    /// Automatically infers the key and decrypts data.
    /// <para>自动推断密钥并解密数据。</para>
    /// </summary>
    /// <param name="data">Candidate file bytes; the whole input is buffered in memory.
    /// <para>候选文件字节；整个输入会被缓冲到内存中。</para></param>
    /// <returns>The decrypted payload, or <see langword="null"/> when the bytes lack the OZip magic or no
    /// built-in key produces valid plaintext.
    /// <para>解密后的负载；当字节缺少 OZip 魔数或没有内置密钥能产生有效明文时返回 <see langword="null"/>。</para></returns>
    /// <exception cref="InvalidOppFormatException">The magic matches but the header is malformed or the
    /// declared size exceeds the buffered data; this failure is not reported as <see langword="null"/>.
    /// <para>魔数匹配但头部格式错误，或声明的大小超出缓冲数据；这类失败不会以 <see langword="null"/> 形式返回。</para></exception>
    /// <exception cref="System.Security.Cryptography.CryptographicException">The ECB decryption of a prefix
    /// block did not produce a full 16-byte block.
    /// <para>某个前缀块的 ECB 解密未产生完整的 16 字节块。</para></exception>
    public byte[]? AutoDecrypt(byte[] data)
    {
        Guard.ThrowIfNull(data);

        using var input = new MemoryStream(data);

        if (!OzipHeader.IsOzipFile(input))
        {
            return null;
        }

        try
        {
            using var output = new MemoryStream();
            OzipDecryptor.DecryptFileWithAutoDetection(input, output);
            return output.ToArray();
        }
        catch (OppDecryptionException)
        {
            return null;
        }
    }

    /// <summary>
    /// Queries whether the specified key identifier is supported.
    /// <para>查询是否支持指定的密钥标识符。</para>
    /// </summary>
    /// <param name="keyId">Key identifier to probe: <c>OZ-&lt;index&gt;</c>,
    /// <c>OZ-custom-&lt;index&gt;</c>, or a 32-hex-character key.
    /// <para>要探测的密钥标识符：<c>OZ-&lt;索引&gt;</c>、<c>OZ-custom-&lt;索引&gt;</c>，或 32 位十六进制字符的密钥。</para></param>
    /// <returns><see langword="true"/> when the identifier resolves to a built-in, custom, or inline hex
    /// key; <see langword="false"/> for out-of-range indexes or unrecognized text.
    /// <para>标识符能解析为内置、自定义或内联十六进制密钥时返回 <see langword="true"/>；索引越界或文本无法识别时返回
    /// <see langword="false"/>。</para></returns>
    public bool IsSupported(string keyId) => ResolveKey(keyId) != null;

    /// <summary>
    /// Gets the list of all known key identifiers for this format.
    /// <para>获取此格式的所有已知密钥标识符列表。</para>
    /// </summary>
    /// <returns><c>OZ-0</c> up to the built-in database size followed by <c>OZ-custom-0</c> up to the
    /// number of added custom keys; inline hex keys are not listed.
    /// <para>先列出 <c>OZ-0</c> 至内置库容量前一项，再列出 <c>OZ-custom-0</c> 至已添加自定义密钥数量前一项；不包含内联十六进制密钥。</para></returns>
    public IReadOnlyList<string> GetKnownKeys()
    {
        var result = new List<string>();
        for (var i = 0; i < OzipKeyDatabase.Count; i++)
        {
            result.Add($"{KeyIdPrefix}-{i}");
        }

        for (var i = 0; i < _customKeys.Count; i++)
        {
            result.Add($"{KeyIdPrefix}-custom-{i}");
        }

        return result;
    }

    /// <summary>
    /// Adds a custom key.
    /// <para>添加自定义密钥。</para>
    /// </summary>
    /// <param name="keyEntry">An <see cref="OzipKeyEntry"/> instance; it is appended and addressed later as
    /// <c>OZ-custom-&lt;index&gt;</c> by its position in the added order.
    /// <para><see cref="OzipKeyEntry"/> 实例；按添加顺序追加，之后可用其位置对应的 <c>OZ-custom-&lt;索引&gt;</c> 引用。</para></param>
    /// <exception cref="ArgumentException"><paramref name="keyEntry"/> is not an
    /// <see cref="OzipKeyEntry"/>. <para><paramref name="keyEntry"/> 不是 <see cref="OzipKeyEntry"/>。</para></exception>
    public void AddCustomKey(object keyEntry)
    {
        Guard.ThrowIfNull(keyEntry);

        if (keyEntry is OzipKeyEntry ozipEntry)
        {
            _customKeys.Add(ozipEntry);
            return;
        }

        throw new ArgumentException($"Unsupported key entry type: {keyEntry.GetType().Name}. Expected OzipKeyEntry.", nameof(keyEntry));
    }

    /// <summary>
    /// Exports discovered keys.
    /// <para>导出发现的密钥。</para>
    /// </summary>
    /// <param name="filePath">Destination text file, overwritten with one key hex string per line — built-in
    /// database keys first, then added custom keys.
    /// <para>目标文本文件；写入时覆盖原内容，每行一个密钥十六进制字符串——先列出内置库密钥，再列出已添加的自定义密钥。</para></param>
    /// <exception cref="IOException">The file cannot be created or written.
    /// <para>无法创建或写入该文件。</para></exception>
    public void ExportDiscoveredKeys(string filePath)
    {
        Guard.ThrowIfNullOrWhiteSpace(filePath);

        var lines = new List<string>();
        foreach (var entry in OzipKeyDatabase.GetAllKeys())
        {
            lines.Add(entry.KeyHex);
        }

        foreach (var entry in _customKeys)
        {
            lines.Add(entry.KeyHex);
        }

        File.WriteAllLines(filePath, lines);
    }

    /// <summary>
    /// Clears all key data.
    /// <para>清除所有密钥数据。</para>
    /// </summary>
    public void Clear() => _customKeys.Clear();

    /// <summary>
    /// Searches the known keys for a match (built-in key database only; no brute-force).
    /// <para>在已知密钥中搜索匹配的密钥（仅内置密钥库；不执行暴力破解）。</para>
    /// </summary>
    /// <param name="firstEncrypted16Bytes">The first 16-byte encrypted prefix block of the payload.
    /// <para>负载的第一个 16 字节加密前缀块。</para></param>
    /// <returns>The matching built-in key entry, or <see langword="null"/> when no built-in key produces
    /// valid plaintext. <para>匹配的内置密钥项；没有内置密钥产生有效明文时返回 <see langword="null"/>。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="firstEncrypted16Bytes"/> is not exactly 16 bytes
    /// long. <para><paramref name="firstEncrypted16Bytes"/> 的长度不是 16 字节。</para></exception>
    public OzipKeyEntry? SearchKey(byte[] firstEncrypted16Bytes)
    {
        Guard.ThrowIfNull(firstEncrypted16Bytes);
        return OzipDecryptor.DetectKey(firstEncrypted16Bytes);
    }

    private OzipKeyEntry? ResolveKey(string keyId)
    {

        if (keyId.StartsWith($"{KeyIdPrefix}-custom-", StringComparison.OrdinalIgnoreCase))
        {
            var indexPart = keyId[$"{KeyIdPrefix}-custom-".Length..];
            return int.TryParse(indexPart, out var customIndex) &&
                customIndex >= 0 && customIndex < _customKeys.Count
                ? _customKeys[customIndex]
                : null;
        }

        if (keyId.StartsWith($"{KeyIdPrefix}-", StringComparison.OrdinalIgnoreCase))
        {
            var indexPart = keyId[$"{KeyIdPrefix}-".Length..];
            return int.TryParse(indexPart, out var index) &&
                index >= 0 && index < OzipKeyDatabase.Count
                ? OzipKeyDatabase.FindByIndex(index)
                : null;
        }


        if (keyId.Length == 32 && IsHexString(keyId))
        {
            try
            {
                return new OzipKeyEntry(keyId.ToUpperInvariant());
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static bool IsHexString(string value)
    {
        foreach (var c in value)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}