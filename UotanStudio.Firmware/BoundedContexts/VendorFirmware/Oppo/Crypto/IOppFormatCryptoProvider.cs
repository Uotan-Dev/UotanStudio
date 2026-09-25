using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Models;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Crypto;

/// <summary>
/// Crypto contract for one Oppo container format: it resolves key identifiers of its own namespace and
/// performs the format's transformation on buffers or stream pairs.
/// <para>单一 Oppo 容器格式的加密契约：解析本格式命名空间内的密钥标识符，并在缓冲区或流对上执行该格式的变换。</para>
/// </summary>
/// <remarks>
/// Each implementation owns one <see cref="OppFormat"/> and its own identifier syntax, so a value accepted
/// by <see cref="IsSupported"/> on one provider is not valid on another. Implementations keep no ownership of
/// caller-supplied streams or buffers.
/// <para>每个实现只对应一个 <see cref="OppFormat"/> 并拥有自己的标识符语法，因此某个提供者的
/// <see cref="IsSupported"/> 接受的值在另一个提供者上并不有效。实现不持有调用方提供的流或缓冲区的所有权。</para>
/// </remarks>
public interface IOppFormatCryptoProvider
{
    /// <summary>
    /// The single container format this provider serves.
    /// <para>本提供者服务的唯一容器格式。</para></summary>
    /// <value>
    /// The format key under which the facade registers the provider; each implementation returns one fixed
    /// <see cref="OppFormat"/> value.
    /// <para>门面注册本提供者所使用的格式键；每个实现都返回一个固定的 <see cref="OppFormat"/> 值。</para></value>
    OppFormat SupportedFormat { get; }

    /// <summary>
    /// Decrypts data using the specified key.
    /// <para>使用指定密钥解密数据。</para>
    /// </summary>
    /// <param name="data">Ciphertext for this format; it is read only and never modified in place. A format
    /// that decrypts a whole file expects the complete file bytes.
    /// <para>本格式的密文；仅读取，绝不就地修改。需要整文件解密的格式要求传入完整的文件字节。</para></param>
    /// <param name="keyId">Key identifier in this provider's syntax; unknown identifiers are rejected before
    /// any transformation runs.
    /// <para>本提供者语法内的密钥标识符；未知标识符在任何变换执行前即被拒绝。</para></param>
    /// <returns>The decrypted bytes as a newly allocated array.
    /// <para>以新分配数组返回的解密字节。</para></returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Exceptions.OppDecryptionException">
    /// <paramref name="keyId"/> resolves to no key of this format.
    /// <para><paramref name="keyId"/> 无法解析为本格式的任何密钥。</para></exception>
    byte[] Decrypt(byte[] data, string keyId);

    /// <summary>
    /// Encrypts data using the specified key.
    /// <para>使用指定密钥加密数据。</para>
    /// </summary>
    /// <param name="data">Plaintext for this format; it is read only and never modified in place.
    /// <para>本格式的明文；仅读取，绝不就地修改。</para></param>
    /// <param name="keyId">Key identifier in this provider's syntax; a decryption-only format rejects the call
    /// before consulting it.
    /// <para>本提供者语法内的密钥标识符；仅支持解密的格式会在读取该值前就拒绝调用。</para></param>
    /// <returns>The encrypted bytes as a newly allocated array.
    /// <para>以新分配数组返回的加密字节。</para></returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Exceptions.OppDecryptionException">
    /// <paramref name="keyId"/> resolves to no key of this format, or the format supports decryption only.
    /// <para><paramref name="keyId"/> 无法解析为本格式的任何密钥，或该格式仅支持解密。</para></exception>
    byte[] Encrypt(byte[] data, string keyId);

    /// <summary>
    /// Decrypts stream data using the specified key.
    /// <para>使用指定密钥解密流数据。</para>
    /// </summary>
    /// <param name="input">Readable source stream positioned where this format's payload starts; some formats
    /// additionally require seeking. The provider reads it but never closes it.
    /// <para>定位到本格式负载起始处的可读取源流；部分格式还要求可寻址。提供者只读取且绝不关闭。</para></param>
    /// <param name="output">Stream receiving the plaintext; the provider writes to it, and neither flushes nor
    /// closes it.
    /// <para>接收明文的流；提供者向其写入，且既不调用刷新也不关闭。</para></param>
    /// <param name="keyId">Key identifier in this provider's syntax.
    /// <para>本提供者语法内的密钥标识符。</para></param>
    /// <param name="cancellationToken">Observed at coarsely bounded points by providers whose transformation
    /// supports cancellation; a provider that buffers the payload covers it with a single check before doing
    /// any work, and one that transforms in bulk may not observe it at all.
    /// <para>支持取消的转换实现只在粗粒度的边界点观察；缓冲负载的提供者至多在开始工作前检查一次，整体变换的提供者可能完全不观察。</para></param>
    /// <exception cref="System.ArgumentNullException"><paramref name="input"/> or
    /// <paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="System.OperationCanceledException">Cancellation was already requested at a point the
    /// provider checks. <para>提供者在检查点上已收到取消请求。</para></exception>
    /// <exception cref="UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Exceptions.OppDecryptionException">
    /// <paramref name="keyId"/> resolves to no key of this format.
    /// <para><paramref name="keyId"/> 无法解析为本格式的任何密钥。</para></exception>
    void Decrypt(Stream input, Stream output, string keyId, CancellationToken cancellationToken);

    /// <summary>
    /// Encrypts stream data using the specified key.
    /// <para>使用指定密钥加密流数据。</para>
    /// </summary>
    /// <param name="input">Readable source stream holding the plaintext; the provider never closes it.
    /// <para>持有明文的源流；提供者绝不关闭。</para></param>
    /// <param name="output">Stream receiving the ciphertext; the provider neither flushes nor closes it.
    /// <para>接收密文的流；提供者既不调用刷新也不关闭。</para></param>
    /// <param name="keyId">Key identifier in this provider's syntax; a decryption-only format rejects the call
    /// before consulting it.
    /// <para>本提供者语法内的密钥标识符；仅支持解密的格式会在读取该值前就拒绝调用。</para></param>
    /// <param name="cancellationToken">Cancellation behaviour follows the same coarse bounds as the decryption
    /// overload. <para>取消行为与解密重载相同的粗粒度边界。</para></param>
    /// <exception cref="System.ArgumentNullException"><paramref name="input"/> or
    /// <paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Exceptions.OppDecryptionException">
    /// <paramref name="keyId"/> resolves to no key of this format, or the format supports decryption only.
    /// <para><paramref name="keyId"/> 无法解析为本格式的任何密钥，或该格式仅支持解密。</para></exception>
    void Encrypt(Stream input, Stream output, string keyId, CancellationToken cancellationToken);

    /// <summary>
    /// Automatically infers the key and decrypts data.
    /// <para>自动推断密钥并解密数据。</para>
    /// </summary>
    /// <param name="data">Candidate ciphertext tried against this format's built-in and custom keys until one
    /// produces accepted plaintext; it is never modified in place.
    /// <para>候选密文；依次用本格式的内置与自定义密钥尝试，直到某个密钥产出被接受的明文；不会就地修改。</para></param>
    /// <returns>The accepted plaintext, or <see langword="null"/> when no key of this format matches the
    /// buffer. <para>被接受的明文；本格式没有任何密钥匹配该缓冲时返回 <see langword="null"/>。</para></returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    byte[]? AutoDecrypt(byte[] data);

    /// <summary>
    /// Queries whether the specified key identifier is supported.
    /// <para>查询是否支持指定的密钥标识符。</para>
    /// </summary>
    /// <param name="keyId">Identifier tested against this format's built-in table and its runtime-added keys.
    /// <para>在本格式的内置密钥表与运行期添加的密钥中测试的标识符。</para></param>
    /// <returns><see langword="true"/> when the identifier resolves to a key of this format.
    /// <para>标识符可解析为本格式的密钥时返回 <see langword="true"/>。</para></returns>
    bool IsSupported(string keyId);

    /// <summary>
    /// Gets the list of all known key identifiers for this format.
    /// <para>获取此格式的所有已知密钥标识符列表。</para>
    /// </summary>
    /// <returns>Built-in identifiers first and runtime-added identifiers afterwards, in provider order; keys
    /// that are addressed by inline key text rather than by an identifier are not listed. The list is a
    /// snapshot and does not reflect keys added later.
    /// <para>按提供者顺序先列出内置标识符，再列出运行期添加的标识符；以内联密钥文本而非标识符引用的密钥不在列表中。
    /// 该列表是快照，不会反映之后添加的密钥。</para></returns>
    IReadOnlyList<string> GetKnownKeys();

    /// <summary>
    /// Adds a custom key.
    /// <para>添加自定义密钥。</para>
    /// </summary>
    /// <param name="keyEntry">The key entry type owned by this format, e.g.
    /// <see cref="KeyDatabase.QcKeyEntry"/>, <see cref="KeyDatabase.MtkKeyEntry"/>,
    /// <see cref="KeyDatabase.OpsKeyEntry"/> or <see cref="KeyDatabase.OzipKeyEntry"/>.
    /// <para>本格式所属的密钥条目类型，例如 <see cref="KeyDatabase.QcKeyEntry"/>、<see cref="KeyDatabase.MtkKeyEntry"/>、
    /// <see cref="KeyDatabase.OpsKeyEntry"/> 或 <see cref="KeyDatabase.OzipKeyEntry"/>。</para></param>
    /// <exception cref="System.ArgumentNullException"><paramref name="keyEntry"/> is <see langword="null"/>.
    /// <para><paramref name="keyEntry"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="System.ArgumentException"><paramref name="keyEntry"/> is not the entry type this
    /// format owns. <para><paramref name="keyEntry"/> 不是本格式所属的条目类型。</para></exception>
    void AddCustomKey(object keyEntry);

    /// <summary>
    /// Exports discovered keys.
    /// <para>导出发现的密钥。</para>
    /// </summary>
    /// <param name="filePath">Destination path; a provider holding key material replaces the file at this
    /// path, while a provider with nothing to publish leaves the path untouched. Empty and white-space paths
    /// are rejected.
    /// <para>目标路径；持有密钥材料的提供者会替换该路径上的文件，无可发布内容的提供者则不改动该路径。空或仅空白的路径会被拒绝。</para></param>
    /// <exception cref="System.ArgumentException"><paramref name="filePath"/> is empty or consists only of
    /// white space. <para><paramref name="filePath"/> 为空或仅由空白字符组成。</para></exception>
    /// <exception cref="System.ArgumentNullException"><paramref name="filePath"/> is <see langword="null"/>.
    /// <para><paramref name="filePath"/> 为 <see langword="null"/>。</para></exception>
    void ExportDiscoveredKeys(string filePath);

    /// <summary>
    /// Clears all key data.
    /// <para>清除所有密钥数据。</para>
    /// </summary>
    void Clear();
}