using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Diagnostics;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Exceptions;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Models;
using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Crypto;

public sealed class OppCryptoProvider(IOppLogger? logger = null) : IOppCryptoProvider, IDisposable
{
    private readonly Dictionary<OppFormat, IOppFormatCryptoProvider> _providers = [];
    private readonly IOppLogger? _logger = logger;
    private bool _disposed;

    /// <summary>
    /// Registers a format-specific crypto provider.
    /// <para>注册格式特定的加密提供者。</para>
    /// </summary>
    public void RegisterProvider(IOppFormatCryptoProvider provider)
    {
        Guard.ThrowIfNull(provider);
        _providers[provider.SupportedFormat] = provider;
        _logger?.Debug($"Registered crypto provider for format '{provider.SupportedFormat}'.");
    }

    /// <summary>
    /// Decrypts data using the specified key.
    /// <para>使用指定密钥解密数据。</para>
    /// </summary>
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

    /// <summary>
    /// Encrypts data using the specified key.
    /// <para>使用指定密钥加密数据。</para>
    /// </summary>
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

    /// <summary>
    /// Decrypts stream data using the specified key.
    /// <para>使用指定密钥解密流数据。</para>
    /// </summary>
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

    /// <summary>
    /// Encrypts stream data using the specified key.
    /// <para>使用指定密钥加密流数据。</para>
    /// </summary>
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

    /// <summary>
    /// Automatically infers the key and decrypts data.
    /// <para>自动推断密钥并解密数据。</para>
    /// </summary>
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

    /// <summary>
    /// Automatically infers the key and decrypts stream data.
    /// <para>自动推断密钥并解密流数据。</para>
    /// </summary>
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

    /// <summary>
    /// Queries whether the specified key identifier is supported.
    /// <para>查询是否支持指定的密钥标识符。</para>
    /// </summary>
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

    /// <summary>
    /// Adds a custom key.
    /// <para>添加自定义密钥。</para>
    /// </summary>
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

    /// <summary>
    /// Exports the key discovered by auto-detection to a file.
    /// <para>将自动探测发现的密钥导出到文件。</para>
    /// </summary>
    public void ExportDiscoveredKey(string filePath)
    {
        Guard.ThrowIfNullOrWhiteSpace(filePath);

        foreach (var provider in _providers.Values)
        {
            provider.ExportDiscoveredKeys(filePath);
        }
    }

    /// <summary>
    /// Gets the list of all known key identifiers for the specified format.
    /// <para>获取指定格式的所有已知密钥标识符列表。</para>
    /// </summary>
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