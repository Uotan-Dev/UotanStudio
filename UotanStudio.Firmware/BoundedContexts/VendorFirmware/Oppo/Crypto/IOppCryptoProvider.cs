using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Models;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Crypto;

/// <summary>
/// Format-agnostic Oppo crypto facade: one entry point that routes a buffer, a stream pair or a key
/// identifier to the per-format provider registered for <see cref="OppFormat"/>.
/// <para>与格式无关的 Oppo 加密门面：把缓冲区、流对或密钥标识符路由到按 <see cref="OppFormat"/> 注册的各格式提供者。</para>
/// </summary>
/// <remarks>
/// Routing of an explicit key identifier follows its prefix: <c>MTK</c> selects the MediaTek provider,
/// <c>mbox</c> the OPS provider, <c>OZ</c> the OZIP provider, and any other identifier the Qualcomm
/// provider. A missing provider for the selected format is reported as
/// <see cref="UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Exceptions.OppDecryptionException"/>.
/// <para>显式密钥标识符按前缀路由：<c>MTK</c> 选择联发科提供者，<c>mbox</c> 选择 OPS 提供者，<c>OZ</c> 选择 OZIP 提供者，
/// 其余标识符选择高通提供者。所选格式没有注册提供者时报告为 <see cref="UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Exceptions.OppDecryptionException"/>。</para>
/// </remarks>
public interface IOppCryptoProvider
{
    /// <summary>
    /// Decrypts data using the specified key.
    /// <para>使用指定密钥解密数据。</para>
    /// </summary>
    /// <param name="data">Ciphertext buffer; it is read only and never modified in place.
    /// <para>密文缓冲；仅读取，绝不就地修改。</para></param>
    /// <param name="keyId">Key identifier routed by prefix, or <see langword="null"/> to infer the key from
    /// the registered providers.
    /// <para>按前缀路由的密钥标识符；为 <see langword="null"/> 时由已注册提供者推断密钥。</para></param>
    /// <returns>The plaintext as a newly allocated array.
    /// <para>以新分配数组返回的明文。</para></returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Exceptions.OppDecryptionException">No provider is registered for the format the
    /// identifier routes to, no key matches, or automatic inference found no key.
    /// <para>标识符路由到的格式未注册提供者、没有匹配的密钥，或自动推断未找到密钥。</para></exception>
    byte[] Decrypt(byte[] data, string? keyId = null);

    /// <summary>
    /// Encrypts data using the specified key.
    /// <para>使用指定密钥加密数据。</para>
    /// </summary>
    /// <param name="data">Plaintext buffer; it is read only and never modified in place.
    /// <para>明文缓冲；仅读取，绝不就地修改。</para></param>
    /// <param name="keyId">Key identifier routed by prefix; <see langword="null"/> is rejected because no
    /// format can be inferred for encryption.
    /// <para>按前缀路由的密钥标识符；为 <see langword="null"/> 会被拒绝，因为加密无法推断格式。</para></param>
    /// <returns>The ciphertext as a newly allocated array.
    /// <para>以新分配数组返回的密文。</para></returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Exceptions.OppDecryptionException"><paramref name="keyId"/> is
    /// <see langword="null"/>, the target format is decryption-only, or no key matches.
    /// <para><paramref name="keyId"/> 为 <see langword="null"/>、目标格式仅支持解密，或没有匹配的密钥。</para></exception>
    byte[] Encrypt(byte[] data, string? keyId = null);

    /// <summary>
    /// Decrypts stream data using the specified key.
    /// <para>使用指定密钥解密流数据。</para>
    /// </summary>
    /// <param name="input">Readable source stream positioned where the provider expects the payload to
    /// start; the facade leaves it open and the caller keeps ownership.
    /// <para>可读取的源流，需定位到提供者期望的负载起始处；门面不会关闭它，所有权仍归调用方。</para></param>
    /// <param name="output">Stream that receives the plaintext; it is neither flushed nor closed here.
    /// <para>接收明文的流；此处既不调用刷新也不关闭。</para></param>
    /// <param name="keyId">Key identifier routed by prefix, or <see langword="null"/> to infer the key.
    /// <para>按前缀路由的密钥标识符；为 <see langword="null"/> 时自动推断密钥。</para></param>
    /// <param name="cancellationToken">Observed by providers whose transformation supports cancellation;
    /// a provider that buffers the whole payload checks it, if at all, before reading.
    /// <para>由支持取消的转换实现负责观察；整体缓冲负载的提供者至多在读取前检查一次。</para></param>
    /// <exception cref="System.ArgumentNullException"><paramref name="input"/> or
    /// <paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Exceptions.OppDecryptionException">No provider or no key matches the routed
    /// format, or inference found no key.
    /// <para>路由格式没有匹配的提供者或密钥，或推断未找到密钥。</para></exception>
    void Decrypt(Stream input, Stream output, string? keyId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Encrypts stream data using the specified key.
    /// <para>使用指定密钥加密流数据。</para>
    /// </summary>
    /// <param name="input">Readable source stream holding the plaintext; it stays open and caller-owned.
    /// <para>持有明文的源流；保持打开且由调用方持有。</para></param>
    /// <param name="output">Stream that receives the ciphertext; it is neither flushed nor closed here.
    /// <para>接收密文的流；此处既不调用刷新也不关闭。</para></param>
    /// <param name="keyId">Key identifier routed by prefix; <see langword="null"/> is rejected because no
    /// format can be inferred for encryption.
    /// <para>按前缀路由的密钥标识符；为 <see langword="null"/> 会被拒绝，因为加密无法推断格式。</para></param>
    /// <param name="cancellationToken">Observed by providers whose transformation supports cancellation.
    /// <para>由支持取消的转换实现负责观察。</para></param>
    /// <exception cref="System.ArgumentNullException"><paramref name="input"/> or
    /// <paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Exceptions.OppDecryptionException"><paramref name="keyId"/> is
    /// <see langword="null"/>, the target format is decryption-only, or no key matches.
    /// <para><paramref name="keyId"/> 为 <see langword="null"/>、目标格式仅支持解密，或没有匹配的密钥。</para></exception>
    void Encrypt(Stream input, Stream output, string? keyId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Automatically infers the key and decrypts data.
    /// <para>自动推断密钥并解密数据。</para>
    /// </summary>
    /// <param name="data">Ciphertext buffer probed with every registered provider until one produces
    /// accepted plaintext; it is never modified in place.
    /// <para>依次用各已注册提供者探测的密文缓冲，直到某个提供者产出被接受的明文；不会就地修改。</para></param>
    /// <returns>The plaintext from the first provider that accepts the buffer.
    /// <para>第一个接受该缓冲的提供者所产出的明文。</para></returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Exceptions.OppDecryptionException">No provider is registered, or none accepted
    /// the buffer. <para>未注册任何提供者，或没有任何提供者接受该缓冲。</para></exception>
    /// <exception cref="System.ObjectDisposedException">The facade was disposed and rejects further work.
    /// <para>门面已释放，拒绝后续操作。</para></exception>
    byte[] AutoDecrypt(byte[] data);

    /// <summary>
    /// Automatically infers the key and decrypts stream data.
    /// <para>自动推断密钥并解密流数据。</para>
    /// </summary>
    /// <param name="input">Readable source stream; the facade copies it to the end, so inference always
    /// starts at the current position and the whole payload must fit in memory.
    /// <para>可读取的源流；门面会复制到末尾，因此推断始终从当前位置开始，且整个负载必须能放入内存。</para></param>
    /// <param name="output">Stream that receives the plaintext; it is neither flushed nor closed here.
    /// <para>接收明文的流；此处既不调用刷新也不关闭。</para></param>
    /// <param name="cancellationToken">Declared for signature symmetry; this overload performs a single
    /// uncancellable copy and inference pass.
    /// <para>仅为签名对称而保留；该重载执行一次不可取消的复制与推断过程。</para></param>
    /// <exception cref="System.ArgumentNullException"><paramref name="input"/> or
    /// <paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Exceptions.OppDecryptionException">No registered provider accepted the buffered
    /// payload. <para>没有任何已注册提供者接受缓冲后的负载。</para></exception>
    void AutoDecrypt(Stream input, Stream output, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries whether the specified key identifier is supported.
    /// <para>查询是否支持指定的密钥标识符。</para>
    /// </summary>
    /// <param name="keyId">Key identifier tested against every registered provider, including keys added at
    /// runtime.
    /// <para>在所有已注册提供者（含运行期添加的密钥）中测试的密钥标识符。</para></param>
    /// <returns><see langword="true"/> as soon as one provider recognises the identifier;
    /// <see langword="false"/> for an unknown identifier or when no provider is registered.
    /// <para>任一提供者识别该标识符即返回 <see langword="true"/>；标识符未知或未注册提供者时返回 <see langword="false"/>。</para></returns>
    bool IsSupported(string keyId);

    /// <summary>
    /// Adds a custom key.
    /// <para>添加自定义密钥。</para>
    /// </summary>
    /// <param name="keyEntry">A <see cref="KeyDatabase.QcKeyEntry"/>,
    /// <see cref="KeyDatabase.MtkKeyEntry"/>, <see cref="KeyDatabase.OpsKeyEntry"/> or
    /// <see cref="KeyDatabase.OzipKeyEntry"/>; every provider that accepts the entry type stores it.
    /// <para><see cref="KeyDatabase.QcKeyEntry"/>、<see cref="KeyDatabase.MtkKeyEntry"/>、
    /// <see cref="KeyDatabase.OpsKeyEntry"/> 或 <see cref="KeyDatabase.OzipKeyEntry"/>；
    /// 所有接受该条目类型的提供者都会存储它。</para></param>
    /// <exception cref="System.ArgumentNullException"><paramref name="keyEntry"/> is
    /// <see langword="null"/>. <para><paramref name="keyEntry"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="System.ArgumentException"><paramref name="keyEntry"/> is of a type no registered
    /// provider accepts. <para><paramref name="keyEntry"/> 的类型没有任何已注册提供者接受。</para></exception>
    void AddCustomKey(object keyEntry);

    /// <summary>
    /// Exports the key discovered by auto-detection to a file.
    /// <para>将自动探测发现的密钥导出到文件。</para>
    /// </summary>
    /// <param name="filePath">Destination path handed to every registered provider; providers without
    /// discovered key material write nothing, and a provider that does write replaces any existing file at
    /// this path.
    /// <para>传给所有已注册提供者的目标路径；没有已探测密钥的提供者不写入内容，会写入的提供者则替换该路径上的已有文件。</para></param>
    /// <exception cref="System.ArgumentException"><paramref name="filePath"/> is empty or consists only of
    /// white space. <para><paramref name="filePath"/> 为空或仅由空白字符组成。</para></exception>
    /// <exception cref="System.ArgumentNullException"><paramref name="filePath"/> is <see langword="null"/>.
    /// <para><paramref name="filePath"/> 为 <see langword="null"/>。</para></exception>
    void ExportDiscoveredKey(string filePath);

    /// <summary>
    /// Gets the list of all known key identifiers for the specified format.
    /// <para>获取指定格式的所有已知密钥标识符列表。</para>
    /// </summary>
    /// <param name="format">Container format whose provider is consulted.
    /// <para>需要查询其提供者的容器格式。</para></param>
    /// <returns>Built-in and custom identifiers of that format in provider order, or an empty list when the
    /// format has no registered provider or the format is <see cref="OppFormat.Unknown"/>.
    /// <para>按提供者顺序返回该格式的内置与自定义标识符；该格式未注册提供者或格式为 <see cref="OppFormat.Unknown"/> 时返回空列表。</para></returns>
    IReadOnlyList<string> GetKnownKeys(OppFormat format);
}