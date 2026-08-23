namespace UotanStudio.Firmware.Oppo.Models;

/// <summary>
/// <para>表示完全解析的 Oppo 固件归档。</para>
/// Represents a fully parsed Oppo firmware archive.
/// </summary>
/// <remarks>
/// <para>此类型的实例不是线程安全的，不应从多个线程同时访问。</para>
/// This type is not thread-safe and should not be accessed concurrently from multiple threads.
/// </remarks>
public sealed class OppArchive
{
    /// <summary>
    /// <para>获取固件归档的元信息。</para>
    /// Gets the metadata of the firmware archive.
    /// </summary>
    public OppMetadata Metadata { get; init; } = new();

    /// <summary>
    /// <para>获取固件归档中的条目列表。</para>
    /// Gets the list of entries in the firmware archive.
    /// </summary>
    public IReadOnlyList<OppEntry> Entries { get; init; } = [];
}