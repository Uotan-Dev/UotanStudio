using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using UotanStudio.Firmware.Oppo.Crypto.Algorithms;
using UotanStudio.Firmware.Oppo.Crypto.KeyDatabase;
using UotanStudio.Firmware.Oppo.Helpers;
using UotanStudio.Firmware.Oppo.Models;

namespace UotanStudio.Firmware.Oppo.Crypto;

/// <summary>
/// <para>提供 OPS 格式的加解密操作实现，封装 OppOpsCipher 和 OpsKeyDatabase。</para>
/// Provides OPS format encryption and decryption operations, encapsulating OppOpsCipher and OpsKeyDatabase.
/// </summary>
public sealed class OpsFormatCryptoProvider : IOppFormatCryptoProvider
{
    private readonly ConcurrentDictionary<string, uint[]> _opsRkeyCache = new();
    private readonly ConcurrentDictionary<string, OpsKeyEntry> _customKeys = new();

    /// <summary>
    /// <para>获取此提供者支持的固件格式。</para>
    /// Gets the firmware format supported by this provider.
    /// </summary>
    public OppFormat SupportedFormat => OppFormat.Ops;

    /// <summary>
    /// <para>使用指定密钥解密数据。</para>
    /// Decrypts data using the specified key.
    /// </summary>
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
    /// <para>使用指定密钥加密数据。</para>
    /// Encrypts data using the specified key.
    /// </summary>
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
    /// <para>使用指定密钥解密流数据。</para>
    /// Decrypts stream data using the specified key.
    /// </summary>
    public void Decrypt(Stream input, Stream output, string keyId, CancellationToken cancellationToken)
    {
        Guard.ThrowIfNull(input);
        Guard.ThrowIfNull(output);

        var entry = FindKey(keyId)
            ?? throw new OppDecryptionException($"OPS key '{keyId}' not found in the key database.");

        // OPS cipher operates in-place on byte arrays, so read the stream into memory
        using var tempStream = new MemoryStream();
        input.CopyTo(tempStream);
        var data = tempStream.ToArray();

        var rkey = GetOrInitOpsRkey(entry);
        OppOpsCipher.Decrypt(data, rkey, OpsKeyDatabase.ExpandedSBox, entry.MBox);
        output.Write(data, 0, data.Length);
    }

    /// <summary>
    /// <para>使用指定密钥加密流数据。</para>
    /// Encrypts stream data using the specified key.
    /// </summary>
    public void Encrypt(Stream input, Stream output, string keyId, CancellationToken cancellationToken)
    {
        Guard.ThrowIfNull(input);
        Guard.ThrowIfNull(output);

        var entry = FindKey(keyId)
            ?? throw new OppDecryptionException($"OPS key '{keyId}' not found in the key database.");

        // OPS cipher operates in-place on byte arrays, so read the stream into memory
        using var tempStream = new MemoryStream();
        input.CopyTo(tempStream);
        var data = tempStream.ToArray();

        var rkey = GetOrInitOpsRkey(entry);
        OppOpsCipher.Encrypt(data, rkey, OpsKeyDatabase.ExpandedSBox, entry.MBox);
        output.Write(data, 0, data.Length);
    }

    /// <summary>
    /// <para>自动推断密钥并解密数据，尝试所有 OPS 密钥。</para>
    /// Automatically infers the key and decrypts data, trying all OPS keys.
    /// </summary>
    /// <param name="data"><para>要解密的数据。</para>The data to decrypt.</param>
    /// <returns><para>解密后的数据；若所有密钥均失败则返回 null。</para>The decrypted data; or null if all keys fail.</returns>
    public byte[]? AutoDecrypt(byte[] data)
    {
        Guard.ThrowIfNull(data);

        // Pre-allocate a reusable buffer since OppOpsCipher.Decrypt modifies data in place
        var buffer = new byte[data.Length];

        foreach (var entry in OpsKeyDatabase.Entries)
        {
            try
            {
                data.AsSpan().CopyTo(buffer);

                // Initialize fresh rkey from OpsCoreKey
                var rkey = GetOrInitOpsRkey(entry);

                OppOpsCipher.Decrypt(buffer, rkey, OpsKeyDatabase.ExpandedSBox, entry.MBox);

                var text = Encoding.UTF8.GetString(buffer);

                // Check if decrypted data contains "<?xml" (strict XML declaration detection).
                if (text.IndexOf("<?xml", StringComparison.Ordinal) >= 0)
                {
                    return TruncateAndCopy(buffer, text);
                }
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                // Key failed, try next
            }
        }

        return null;
    }

    /// <summary>
    /// <para>自动探测 OPS 密钥并解密 XML 数据。</para>
    /// Automatically detects OPS key and decrypts XML data.
    /// </summary>
    /// <param name="encryptedXml"><para>加密的 XML 数据。</para>The encrypted XML data.</param>
    /// <returns><para>解密后的 XML 字符串和密钥标识，若无密钥可用则返回 null。</para>The decrypted XML string and key ID, or null if no key works.</returns>
    public (string Xml, string KeyId)? AutoDecryptOpsXml(byte[] encryptedXml)
    {
        Guard.ThrowIfNull(encryptedXml);

        // Pre-allocate a reusable buffer since OppOpsCipher.Decrypt modifies data in place
        var buffer = new byte[encryptedXml.Length];

        foreach (var entry in OpsKeyDatabase.Entries)
        {
            try
            {
                encryptedXml.AsSpan().CopyTo(buffer);

                // Initialize fresh rkey from OpsCoreKey
                var rkey = GetOrInitOpsRkey(entry);

                OppOpsCipher.Decrypt(buffer, rkey, OpsKeyDatabase.ExpandedSBox, entry.MBox);

                var text = Encoding.UTF8.GetString(buffer);

                // Check if decrypted data contains "<?xml" (strict XML declaration detection).
                var xmlDeclIndex = text.IndexOf("<?xml", StringComparison.Ordinal);
                if (xmlDeclIndex >= 0)
                {
                    // Strip any leading garbage (e.g., UTF-8 BOM or other bytes) before the XML declaration
                    var cleaned = xmlDeclIndex > 0 ? text[xmlDeclIndex..] : text;
                    return (TruncateXmlAtLastCloseTag(cleaned), entry.KeyId);
                }
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                // Key failed, try next
            }
        }

        return null;
    }

    /// <summary>
    /// <para>查询是否支持指定的密钥标识符。</para>
    /// Queries whether the specified key identifier is supported.
    /// </summary>
    public bool IsSupported(string keyId) => OpsKeyDatabase.FindByKeyId(keyId) != null || _customKeys.ContainsKey(keyId);

    /// <summary>
    /// <para>获取此格式的所有已知密钥标识符列表。</para>
    /// Gets the list of all known key identifiers for this format.
    /// </summary>
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
    /// <para>添加自定义密钥。</para>
    /// Adds a custom key.
    /// </summary>
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
    /// <para>导出发现的密钥。</para>
    /// Exports discovered keys.
    /// </summary>
    public void ExportDiscoveredKeys(string filePath)
    {
        // No-op: the OPS provider currently only uses built-in keys
    }

    /// <summary>
    /// <para>清除所有密钥数据。</para>
    /// Clears all key data.
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

        // Return a copy because OppOpsCipher.Decrypt/Encrypt modifies the rkey in-place.
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