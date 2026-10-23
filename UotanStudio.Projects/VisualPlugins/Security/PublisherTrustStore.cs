using System.Text.Json;
using System.Text.Json.Serialization;

namespace UotanStudio.Projects;

/// <summary>
/// Trust relationship between a signed package publisher and this user profile.
/// <para>已签名包发布者与当前用户配置之间的信任关系。</para>
/// </summary>
public enum PublisherTrustStatus
{
    /// <summary>The package carries no signature. <para>包未携带签名。</para></summary>
    Unsigned,

    /// <summary>The publisher id and fingerprint match a pinned record. <para>发布者标识与指纹与已固定记录一致。</para></summary>
    Trusted,

    /// <summary>The publisher id is not pinned yet (trust on first use). <para>发布者标识尚未固定（首次使用即信任）。</para></summary>
    UnknownPublisher,

    /// <summary>The publisher id is pinned with a different fingerprint; a rotated key needs re-confirmation.
    /// <para>发布者标识已固定但指纹不同；轮换后的密钥需要重新确认。</para></summary>
    FingerprintMismatch
}

/// <summary>
/// One pinned publisher identity.
/// <para>一条已固定的发布者身份。</para>
/// </summary>
public sealed class PublisherTrustRecord
{
    /// <summary>Stable publisher identifier used as the trust key. <para>作为信任键的稳定发布者标识。</para></summary>
    /// <value>The id from <see cref="VisualPluginPublisher.Id"/>.
    /// <para>来自 <see cref="VisualPluginPublisher.Id"/> 的标识。</para></value>
    public string Id { get; set; } = string.Empty;

    /// <summary>Display name captured when the publisher was trusted. <para>信任该发布者时捕获的显示名称。</para></summary>
    /// <value>Publisher label for the manager list; it carries no trust meaning.
    /// <para>管理器列表使用的发布者标签；不承载信任含义。</para></value>
    public string Name { get; set; } = string.Empty;

    /// <summary>Pinned public-key fingerprint. <para>固定的公钥指纹。</para></summary>
    /// <value>Canonical <c>SHA256:</c> fingerprint; the trust key is <see cref="Id"/> and this value must match on every upgrade.
    /// <para>规范的 <c>SHA256:</c> 指纹；信任键是 <see cref="Id"/>，每次升级都必须与该值一致。</para></value>
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>When the current fingerprint was pinned. <para>当前指纹被固定的时间。</para></summary>
    /// <value>UTC timestamp of the last explicit trust action.
    /// <para>最近一次显式信任操作的时间戳。</para></value>
    public DateTimeOffset TrustedAt { get; set; }
}

/// <summary>
/// Persists publisher id to public-key fingerprint pins in <c>%AppData%/UotanStudio/publishers/trust.json</c>.
/// <para>把发布者标识到公钥指纹的固定关系持久化到 <c>%AppData%/UotanStudio/publishers/trust.json</c>。</para>
/// </summary>
/// <remarks>
/// Pins are trust-on-first-use: the first signed package of an id asks the user to confirm the fingerprint, later
/// packages of the same id must match it, and a rotated key needs another explicit confirmation. The store is
/// read once per instance and every change is published through a temporary file plus an atomic replace.
/// <para>固定采用首次使用即信任：某标识的首个已签名包要求用户确认指纹，之后同一标识的包必须与该指纹一致，
/// 轮换密钥需要再次显式确认。每个实例只读取一次，每次变更都经临时文件加原子替换发布。</para>
/// </remarks>
public sealed class PublisherTrustStore
{
    /// <summary>Maximum number of pinned publishers. <para>允许固定的发布者数量上限。</para></summary>
    public const int MaximumRecordCount = 256;

    /// <summary>Maximum accepted trust file size. <para>允许的信任文件最大字节数。</para></summary>
    public const long MaximumFileSize = 256 * 1024;

    private readonly Lock _gate = new();
    private Dictionary<string, PublisherTrustRecord>? _records;

    /// <summary>
    /// Creates a store over an explicit file path or the default per-user location.
    /// <para>基于显式文件路径或默认按用户位置创建信任库。</para>
    /// </summary>
    /// <param name="path">Trust file path; when omitted, the per-user application data directory is used.
    /// <para>信任文件路径；省略时使用按用户的应用数据目录。</para></param>
    public PublisherTrustStore(string? path = null) => Path = System.IO.Path.GetFullPath(path ?? System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UotanStudio", "publishers", "trust.json"));

    /// <summary>Absolute trust file path used by this store. <para>本信任库使用的信任文件绝对路径。</para></summary>
    /// <value>The file read on first access and replaced on every change.
    /// <para>首次访问时读取、每次变更时替换的文件。</para></value>
    public string Path { get; }

    /// <summary>
    /// Lists every pinned publisher ordered by id.
    /// <para>按标识排序列出所有已固定的发布者。</para>
    /// </summary>
    /// <returns>Independent snapshots; empty when the file is absent or unreadable.
    /// <para>独立快照；文件缺失或不可读时为空。</para></returns>
    public IReadOnlyList<PublisherTrustRecord> List()
    {
        lock (_gate)
        {
            return [.. Load().Values.OrderBy(record => record.Id, StringComparer.Ordinal)
                .Select(record => Snapshot(record))];
        }
    }

    /// <summary>
    /// Finds the pin for one publisher id.
    /// <para>查找单个发布者标识的固定记录。</para>
    /// </summary>
    /// <param name="publisherId">Publisher identifier to match. <para>待匹配的发布者标识。</para></param>
    /// <returns>A copy of the record, or <see langword="null"/> when the id is not pinned.
    /// <para>记录副本；该标识未固定时为 <see langword="null"/>。</para></returns>
    public PublisherTrustRecord? Find(string? publisherId)
    {
        if (string.IsNullOrWhiteSpace(publisherId))
        {
            return null;
        }
        lock (_gate)
        {
            return Load().TryGetValue(publisherId, out var record) ? Snapshot(record) : null;
        }
    }

    /// <summary>
    /// Resolves the trust status of a package manifest.
    /// <para>解析包清单的信任状态。</para>
    /// </summary>
    /// <param name="manifest">Manifest to inspect; an unsigned manifest reports <see cref="PublisherTrustStatus.Unsigned"/>.
    /// <para>待检查的清单；未签名清单返回 <see cref="PublisherTrustStatus.Unsigned"/>。</para></param>
    /// <returns>The trust relationship for the embedded publisher identity.
    /// <para>内嵌发布者身份对应的信任关系。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="manifest"/> is <see langword="null"/>.
    /// <para><paramref name="manifest"/> 为 <see langword="null"/>。</para></exception>
    public PublisherTrustStatus GetStatus(VisualPluginManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var signature = manifest.Signature;
        if (signature is null || string.IsNullOrEmpty(signature.Algorithm) || string.IsNullOrEmpty(signature.Value))
        {
            return PublisherTrustStatus.Unsigned;
        }
        var publisher = manifest.Publisher;
        if (publisher is null || string.IsNullOrWhiteSpace(publisher.Id)
            || string.IsNullOrWhiteSpace(publisher.Fingerprint))
        {
            return PublisherTrustStatus.UnknownPublisher;
        }
        lock (_gate)
        {
            return Load().TryGetValue(publisher.Id, out var record)
                ? string.Equals(record.Fingerprint, publisher.Fingerprint, StringComparison.OrdinalIgnoreCase)
                    ? PublisherTrustStatus.Trusted
                    : PublisherTrustStatus.FingerprintMismatch
                : PublisherTrustStatus.UnknownPublisher;
        }
    }

    /// <summary>
    /// Resolves the trust status of a package.
    /// <para>解析包的信任状态。</para>
    /// </summary>
    /// <param name="package">Package to inspect. <para>待检查的包。</para></param>
    /// <returns>The trust relationship for the embedded publisher identity.
    /// <para>内嵌发布者身份对应的信任关系。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="package"/> is <see langword="null"/>.
    /// <para><paramref name="package"/> 为 <see langword="null"/>。</para></exception>
    public PublisherTrustStatus GetStatus(VisualPluginPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return GetStatus(package.Manifest);
    }

    /// <summary>
    /// Pins the publisher identity of a signed manifest, replacing a rotated fingerprint.
    /// <para>固定已签名清单的发布者身份；指纹轮换时替换旧指纹。</para>
    /// </summary>
    /// <param name="manifest">Manifest whose publisher is pinned; the caller must have obtained explicit user confirmation.
    /// <para>要固定其发布者的清单；调用方必须已经取得用户的显式确认。</para></param>
    /// <returns>The pin now stored for the publisher. <para>当前为该发布者保存的固定记录。</para></returns>
    /// <exception cref="ArgumentException">The manifest is unsigned, or its publisher id, name or fingerprint is malformed.
    /// <para>清单未签名，或其发布者标识、名称或指纹格式非法。</para></exception>
    /// <exception cref="InvalidOperationException">The store already holds <see cref="MaximumRecordCount"/> publishers.
    /// <para>信任库已保存 <see cref="MaximumRecordCount"/> 个发布者。</para></exception>
    public PublisherTrustRecord Trust(VisualPluginManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var publisher = manifest.Publisher;
        if (GetStatus(manifest) == PublisherTrustStatus.Unsigned || publisher is null)
        {
            throw new ArgumentException("Only a signed package with a publisher identity can be trusted.", nameof(manifest));
        }
        if (!VisualPluginValidation.IsValidPluginId(publisher.Id) || string.IsNullOrWhiteSpace(publisher.Name)
            || publisher.Name.Length > 128 || !PluginSignature.IsValidFingerprint(publisher.Fingerprint))
        {
            throw new ArgumentException("Publisher id, name or fingerprint is malformed.", nameof(manifest));
        }
        lock (_gate)
        {
            var records = Load();
            if (!records.ContainsKey(publisher.Id) && records.Count >= MaximumRecordCount)
            {
                throw new InvalidOperationException("The publisher trust store is full.");
            }
            var record = new PublisherTrustRecord
            {
                Id = publisher.Id,
                Name = publisher.Name,
                Fingerprint = publisher.Fingerprint,
                TrustedAt = DateTimeOffset.UtcNow
            };
            records[publisher.Id] = record;
            Save(records.Values);
            return Snapshot(record);
        }
    }

    /// <summary>
    /// Removes the pin for one publisher id.
    /// <para>移除单个发布者标识的固定记录。</para>
    /// </summary>
    /// <param name="publisherId">Publisher identifier to unpin. <para>要取消固定的发布者标识。</para></param>
    /// <returns><see langword="true"/> when a pin was removed. <para>移除了固定记录时为 <see langword="true"/>。</para></returns>
    public bool Untrust(string? publisherId)
    {
        if (string.IsNullOrWhiteSpace(publisherId))
        {
            return false;
        }
        lock (_gate)
        {
            var records = Load();
            if (!records.Remove(publisherId))
            {
                return false;
            }
            Save(records.Values);
            return true;
        }
    }

    private Dictionary<string, PublisherTrustRecord> Load()
    {
        if (_records is not null)
        {
            return _records;
        }
        var records = new Dictionary<string, PublisherTrustRecord>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(Path) && new FileInfo(Path).Length is > 0 and <= MaximumFileSize)
            {
                var bytes = File.ReadAllBytes(Path);
                var state = JsonSerializer.Deserialize(bytes, PublisherTrustJsonContext.Default.PublisherTrustState);
                if (state?.Publishers is { } stored)
                {
                    foreach (var record in stored)
                    {
                        if (record is null || !VisualPluginValidation.IsValidPluginId(record.Id)
                            || !PluginSignature.IsValidFingerprint(record.Fingerprint))
                        {
                            continue;
                        }
                        records[record.Id] = record;
                    }
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
                                          or InvalidDataException)
        {
            ProjectDiagnostics.Write(ProjectLogLevel.Warn, "PublisherTrust",
                "Publisher trust store cannot be read; treating every publisher as unknown. " + error.Message);
        }
        _records = records;
        return records;
    }

    private void Save(IEnumerable<PublisherTrustRecord> records)
    {
        var state = new PublisherTrustState
        {
            Publishers = [.. records.OrderBy(record => record.Id, StringComparer.Ordinal)]
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, PublisherTrustJsonContext.Default.PublisherTrustState);
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        var temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, Path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static PublisherTrustRecord Snapshot(PublisherTrustRecord record) => new()
    {
        Id = record.Id,
        Name = record.Name,
        Fingerprint = record.Fingerprint,
        TrustedAt = record.TrustedAt
    };
}

internal sealed class PublisherTrustState
{
    public List<PublisherTrustRecord> Publishers { get; set; } = [];
}

[JsonSerializable(typeof(PublisherTrustState))]
[JsonSerializable(typeof(PublisherTrustRecord))]
[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class PublisherTrustJsonContext : JsonSerializerContext;