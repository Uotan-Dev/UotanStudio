namespace UotanStudio.Firmware.Payload.Models;

/// <summary>
/// Represents the parsed contents of a payload_properties.txt file from an Android OTA package.
/// <para>表示从 Android OTA 包的 payload_properties.txt 文件解析的内容。</para>
/// </summary>
public class PayloadProperties
{
    /// <summary>
    /// Gets the FILE_HASH value from the properties file.
    /// <para>获取属性文件中的 FILE_HASH 值。</para>
    /// </summary>
    public string FileHash { get; internal set; } = string.Empty;

    /// <summary>
    /// Gets the FILE_SIZE value from the properties file.
    /// <para>获取属性文件中的 FILE_SIZE 值。</para>
    /// </summary>
    public long FileSize { get; internal set; }

    /// <summary>
    /// Gets the METADATA_HASH value from the properties file.
    /// <para>获取属性文件中的 METADATA_HASH 值。</para>
    /// </summary>
    public string MetadataHash { get; internal set; } = string.Empty;

    /// <summary>
    /// Gets the METADATA_SIZE value from the properties file.
    /// <para>获取属性文件中的 METADATA_SIZE 值。</para>
    /// </summary>
    public long MetadataSize { get; internal set; }

    /// <summary>
    /// Gets the raw key-value pairs from the properties file.
    /// <para>获取属性文件中的原始键值对。</para>
    /// </summary>
    public IReadOnlyDictionary<string, string> Properties { get; internal set; } = new Dictionary<string, string>();
}