using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.VendorFirmware.Oppo.Exceptions;
using UotanStudio.Firmware.VendorFirmware.Oppo.Models;

namespace UotanStudio.Firmware.VendorFirmware.Oppo.Crypto;

/// <summary>Facade over the per-format Oppo crypto providers. Key identifiers are routed by prefix to the matching format (<c>MTK</c> to <see cref="OppFormat.OfpMtk"/>, <c>mbox</c> to <see cref="OppFormat.Ops"/>, <c>OZ</c> to <see cref="OppFormat.Ozip"/>, anything else to <see cref="OppFormat.OfpQc"/>); a <see langword="null"/> key identifier switches decryption to a search across the registered providers, in unspecified order.
/// <para> 各 Oppo 格式加密提供者的门面。密钥标识符按前缀路由到对应格式（<c>MTK</c> 对应 <see cref="OppFormat.OfpMtk"/>，<c>mbox</c> 对应 <see cref="OppFormat.Ops"/>，<c>OZ</c> 对应 <see cref="OppFormat.Ozip"/>，其余对应 <see cref="OppFormat.OfpQc"/>）；密钥标识符为 <see langword="null"/> 时，解密以不特定顺序遍历已注册的提供者。 </para></summary>
/// <param name="logger">Optional diagnostics sink for registration and auto-detection events; <see langword="null"/> keeps the provider silent.
/// <para>可选的诊断输出，用于注册与自动探测事件；为 <see langword="null"/> 时不输出任何日志。</para></param>
public sealed class OppCryptoProvider(IOppLogger? logger = null) : IOppCryptoProvider, IDisposable
{
    private readonly Dictionary<OppFormat, IOppFormatCryptoProvider> _providers = [];
    private readonly IOppLogger? _logger = logger;
    private bool _disposed;

    /// <summary>
    /// Registers a format-specific crypto provider.
    /// <para>注册格式特定的加密提供者。</para>
    /// </summary>
    /// <param name="provider">
    /// Provider instance, stored under its <see cref="IOppFormatCryptoProvider.SupportedFormat"/>; a
    /// second registration for the same format replaces the first.
    /// <para>提供者实例，按其 <see cref="IOppFormatCryptoProvider.SupportedFormat"/> 保存；同一格式重复注册时后者替换前者。</para>
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="provider"/> is <see langword="null"/>.
    /// <para><paramref name="provider"/> 为 <see langword="null"/>。</para></exception>
    public void RegisterProvider(IOppFormatCryptoProvider provider)
    {
        Guard.ThrowIfNull(provider);
        _providers[provider.SupportedFormat] = provider;
        _logger?.Debug($"Registered crypto provider for format '{provider.SupportedFormat}'.");
    }

    /// <summary>Decrypts data using the specified key.<para>使用指定密钥解密数据。</para></summary>
    /// <param name="data">Ciphertext bytes; the selected provider decides whether the original array is modified.
    /// <para>密文字节；是否修改原数组由所选提供者决定。</para></param>
    /// <param name="keyId">Key identifier that selects the format provider, or <see langword="null"/> to infer the key by trying every registered provider.
    /// <para>用于选择格式提供者的密钥标识符；为 <see langword="null"/> 时遍历已注册提供者自动推断密钥。</para></param>
    /// <returns>The decrypted bytes produced by the selected provider.
    /// <para>所选提供者产出的解密结果。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="OppDecryptionException">No provider is registered for the format implied by <paramref name="keyId"/>, or — when <paramref name="keyId"/> is <see langword="null"/> — no provider recognizes the payload.
    /// <para> <paramref name="keyId"/> 所隐含的格式没有注册提供者；或 <paramref name="keyId"/> 为 <see langword="null"/> 时没有任何提供者能识别该负载。 </para></exception>
    public byte[] Decrypt(byte[] data, string? keyId = null)
    {
        Guard.ThrowIfNull(data);

        if (keyId is null)
        {
            return AutoDecrypt(data);
        }

        var provider = FindProviderForKey(keyId);
        return provider.Decrypt(data, keyId);
    }

    /// <summary>Encrypts data using the specified key.<para>使用指定密钥加密数据。</para></summary>
    /// <param name="data">Plaintext bytes; the selected provider decides whether the original array is modified.
    /// <para>明文字节；是否修改原数组由所选提供者决定。</para></param>
    /// <param name="keyId">Key identifier that selects the format provider; encryption always needs one, because guessing a key for an unknown payload is a decryption-only capability.
    /// <para>用于选择格式提供者的密钥标识符；加密必须给出该值，因为推断密钥只适用于解密。</para></param>
    /// <returns>The encrypted bytes produced by the selected provider.
    /// <para>所选提供者产出的加密结果。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="OppDecryptionException"><paramref name="keyId"/> is <see langword="null"/>, or no provider is registered for the format it implies.
    /// <para><paramref name="keyId"/> 为 <see langword="null"/>，或其隐含的格式没有注册提供者。</para></exception>
    public byte[] Encrypt(byte[] data, string? keyId = null)
    {
        Guard.ThrowIfNull(data);

        if (keyId is null)
        {
            throw new OppDecryptionException(
                "A key identifier is required for encryption. Auto-encryption is not supported.");
        }

        var provider = FindProviderForKey(keyId);
        return provider.Encrypt(data, keyId);
    }

    /// <summary>Decrypts stream data using the specified key.<para>使用指定密钥解密流数据。</para></summary>
    /// <param name="input">Readable stream positioned at the start of the ciphertext; consumed by the selected provider and left open.
    /// <para>定位在密文起始处的可读流；由所选提供者读取，且保持打开。</para></param>
    /// <param name="output">Writable stream that receives the plaintext at its current position.
    /// <para>在当前写入位置接收明文的可写流。</para></param>
    /// <param name="keyId">Key identifier that selects the format provider, or <see langword="null"/> to infer the key by trying every registered provider.
    /// <para>用于选择格式提供者的密钥标识符；为 <see langword="null"/> 时遍历已注册提供者自动推断密钥。</para></param>
    /// <param name="cancellationToken">Forwarded to the selected provider; the buffering inference path ignores it.
    /// <para>传递给所选提供者；走缓冲推断路径时该令牌不被使用。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="OppDecryptionException">No provider is registered for the format implied by <paramref name="keyId"/>, or — when <paramref name="keyId"/> is <see langword="null"/> — no provider recognizes the payload.
    /// <para> <paramref name="keyId"/> 所隐含的格式没有注册提供者；或 <paramref name="keyId"/> 为 <see langword="null"/> 时没有任何提供者能识别该负载。 </para></exception>
    public void Decrypt(Stream input, Stream output, string? keyId = null, CancellationToken cancellationToken = default)
    {
        Guard.ThrowIfNull(input);
        Guard.ThrowIfNull(output);

        if (keyId is null)
        {
            AutoDecrypt(input, output, cancellationToken);
            return;
        }

        var provider = FindProviderForKey(keyId);
        provider.Decrypt(input, output, keyId, cancellationToken);
    }

    /// <summary>Encrypts stream data using the specified key.<para>使用指定密钥加密流数据。</para></summary>
    /// <param name="input">Readable stream positioned at the start of the plaintext; consumed by the selected provider and left open.
    /// <para>定位在明文起始处的可读流；由所选提供者读取，且保持打开。</para></param>
    /// <param name="output">Writable stream that receives the ciphertext at its current position.
    /// <para>在当前写入位置接收密文的可写流。</para></param>
    /// <param name="keyId">Key identifier that selects the format provider; encryption always needs one, because guessing a key for an unknown payload is a decryption-only capability.
    /// <para>用于选择格式提供者的密钥标识符；加密必须给出该值，因为推断密钥只适用于解密。</para></param>
    /// <param name="cancellationToken">Forwarded to the selected provider.
    /// <para>传递给所选提供者。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="OppDecryptionException"><paramref name="keyId"/> is <see langword="null"/>, or no provider is registered for the format it implies.
    /// <para><paramref name="keyId"/> 为 <see langword="null"/>，或其隐含的格式没有注册提供者。</para></exception>
    public void Encrypt(Stream input, Stream output, string? keyId = null, CancellationToken cancellationToken = default)
    {
        Guard.ThrowIfNull(input);
        Guard.ThrowIfNull(output);

        if (keyId is null)
        {
            throw new OppDecryptionException(
                "A key identifier is required for encryption. Auto-encryption is not supported.");
        }

        var provider = FindProviderForKey(keyId);
        provider.Encrypt(input, output, keyId, cancellationToken);
    }

    /// <summary>Automatically infers the key and decrypts data.<para>自动推断密钥并解密数据。</para></summary>
    /// <param name="data">Ciphertext bytes offered to every registered provider in turn.
    /// <para>依次交给每个已注册提供者的密文字节。</para></param>
    /// <returns>The first successful provider result. A provider that cannot identify the payload returns <see langword="null"/> and the search continues.
    /// <para>第一个成功的提供者结果。无法识别负载的提供者返回 <see langword="null"/>，搜索继续。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ObjectDisposedException">This instance has been disposed.
    /// <para>本实例已被释放。</para></exception>
    /// <exception cref="OppDecryptionException">No provider is registered, or none of them recognized the payload.
    /// <para>没有任何已注册提供者，或所有提供者都无法识别该负载。</para></exception>
    /// <example>
    /// <code>
    /// var provider = new OppCryptoProvider();
    /// provider.RegisterProvider(new OzipFormatCryptoProvider());
    /// byte[] plain = provider.AutoDecrypt(cipherBytes);
    /// </code>
    /// </example>
    public byte[] AutoDecrypt(byte[] data)
    {
        ThrowIfDisposed();
        Guard.ThrowIfNull(data);

        foreach (var provider in _providers.Values)
        {
            var result = provider.AutoDecrypt(data);
            if (result != null)
            {
                _logger?.Debug($"AutoDecrypt: matched provider for format '{provider.SupportedFormat}'.");
                return result;
            }
        }

        throw new OppDecryptionException(
            _providers.Count == 0
                ? "Unable to automatically infer the decryption key. No crypto providers are registered."
                : "Unable to automatically infer the decryption key.");
    }

    /// <summary>Automatically infers the key and decrypts stream data.
    /// <para>自动推断密钥并解密流数据。</para></summary>
    /// <param name="input">Readable stream consumed to its end; the whole payload is buffered in memory because the key can only be chosen once the bytes are known. The stream is left open.
    /// <para> 会被读到末尾的可读流；由于只有拿到字节后才能选定密钥，整个负载会被缓冲到内存中。流保持打开。 </para></param>
    /// <param name="output">Writable stream that receives the plaintext at its current position.
    /// <para>在当前写入位置接收明文的可写流。</para></param>
    /// <param name="cancellationToken">Accepted for signature uniformity; this buffering path never polls the token.
    /// <para>为签名一致而接收；该缓冲路径不检查取消令牌。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> or <paramref name="output"/> is <see langword="null"/>.
    /// <para><paramref name="input"/> 或 <paramref name="output"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ObjectDisposedException">This instance has been disposed.
    /// <para>本实例已被释放。</para></exception>
    /// <exception cref="OppDecryptionException">No provider is registered, or none of them recognized the payload.
    /// <para>没有任何已注册提供者，或所有提供者都无法识别该负载。</para></exception>
    public void AutoDecrypt(Stream input, Stream output, CancellationToken cancellationToken = default)
    {
        Guard.ThrowIfNull(input);
        Guard.ThrowIfNull(output);


        using var tempStream = new MemoryStream();
        input.CopyTo(tempStream);
        var data = tempStream.ToArray();

        var decrypted = AutoDecrypt(data);
        output.Write(decrypted, 0, decrypted.Length);
    }

    /// <summary>Queries whether the specified key identifier is supported.
    /// <para>查询是否支持指定的密钥标识符。</para></summary>
    /// <param name="keyId">Key identifier offered to every registered provider; matching rules are the providers' own.
    /// <para>依次交给每个已注册提供者的密钥标识符；匹配规则由各提供者自行决定。</para></param>
    /// <returns><see langword="true"/> as soon as one registered provider claims the identifier; <see langword="false"/> when none does, including after <see cref="Dispose"/> clears the registry.
    /// <para>任一已注册提供者认领该标识符时即为 <see langword="true"/>；全部不认领时为 <see langword="false"/>，包括 <see cref="Dispose"/> 清空注册表之后。</para></returns>
    public bool IsSupported(string keyId)
    {
        foreach (var provider in _providers.Values)
        {
            if (provider.IsSupported(keyId))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Adds a custom key.<para>添加自定义密钥。</para></summary>
    /// <param name="keyEntry">Key entry object handed to every registered provider until one accepts it; the accepted concrete type depends on the provider, for example <c>OpsKeyEntry</c> or <c>OzipKeyEntry</c>.
    /// <para> 依次交给每个已注册提供者、直到某个提供者接受为止的密钥条目对象；可接受的具体类型取决于提供者， 例如 <c>OpsKeyEntry</c> 或 <c>OzipKeyEntry</c>。 </para></param>
    /// <exception cref="ArgumentNullException"><paramref name="keyEntry"/> is <see langword="null"/>.
    /// <para><paramref name="keyEntry"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException">No registered provider accepts the entry type. A provider that rejects it reports <see cref="ArgumentException"/> and is skipped, so more than one provider may still be consulted.
    /// <para> 没有任何已注册提供者接受该条目类型。拒绝的提供者以 <see cref="ArgumentException"/> 表示并被跳过， 因此仍会询问其余提供者。 </para></exception>
    public void AddCustomKey(object keyEntry)
    {
        Guard.ThrowIfNull(keyEntry);

        var accepted = false;
        foreach (var provider in _providers.Values)
        {
            try
            {
                provider.AddCustomKey(keyEntry);
                accepted = true;
            }
            catch (ArgumentException)
            {

            }
        }

        if (!accepted)
        {
            throw new ArgumentException(
                $"Unsupported key entry type: {keyEntry.GetType().Name}. " +
                "No registered crypto provider accepts this key type.", nameof(keyEntry));
        }
    }

    /// <summary>Exports the key discovered by auto-detection to a file.
    /// <para>将自动探测发现的密钥导出到文件。</para></summary>
    /// <param name="filePath">Destination path handed to every registered provider, which decides whether it writes anything; a format whose provider exports nothing leaves the path untouched.
    /// <para> 交给每个已注册提供者的目标路径，由各提供者决定是否写入；不支持导出的格式不会触碰该路径。 </para></param>
    /// <exception cref="ArgumentNullException"><paramref name="filePath"/> is <see langword="null"/>.
    /// <para><paramref name="filePath"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="filePath"/> is white space only.
    /// <para><paramref name="filePath"/> 仅由空白字符组成。</para></exception>
    public void ExportDiscoveredKey(string filePath)
    {
        Guard.ThrowIfNullOrWhiteSpace(filePath);

        foreach (var provider in _providers.Values)
        {
            provider.ExportDiscoveredKeys(filePath);
        }
    }

    /// <summary>Gets the list of all known key identifiers for the specified format.
    /// <para>获取指定格式的所有已知密钥标识符列表。</para></summary>
    /// <param name="format">Format whose registered provider supplies the identifiers.
    /// <para>由已注册提供者提供标识符的目标格式。</para></param>
    /// <returns>The identifiers known to that format's provider, or an empty list when no provider is registered for it — an absent provider is not an error here.
    /// <para>该格式提供者已知的标识符；未注册该格式提供者时为空列表——此处缺少提供者不算错误。</para></returns>
    public IReadOnlyList<string> GetKnownKeys(OppFormat format) => _providers.TryGetValue(format, out var provider) ? provider.GetKnownKeys() : [];

    /// <summary>
    /// Releases all resources used by this instance.
    /// <para>释放此实例使用的所有资源。</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var provider in _providers.Values)
        {
            provider.Clear();
        }

        _providers.Clear();
        _disposed = true;
    }

    private IOppFormatCryptoProvider FindProviderForKey(string keyId)
    {

        if (keyId.StartsWith("MTK", StringComparison.OrdinalIgnoreCase))
        {
            return GetProvider(OppFormat.OfpMtk);
        }

        if (keyId.StartsWith("mbox", StringComparison.OrdinalIgnoreCase))
        {
            return GetProvider(OppFormat.Ops);
        }

        if (keyId.StartsWith("OZ", StringComparison.OrdinalIgnoreCase))
        {
            return GetProvider(OppFormat.Ozip);
        }


        return GetProvider(OppFormat.OfpQc);
    }

    private IOppFormatCryptoProvider GetProvider(OppFormat format)
    {
        return _providers.TryGetValue(format, out var provider)
            ? provider
            : throw new OppDecryptionException(
            $"No crypto provider registered for format '{format}'.");
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(OppCryptoProvider));
        }
    }
}