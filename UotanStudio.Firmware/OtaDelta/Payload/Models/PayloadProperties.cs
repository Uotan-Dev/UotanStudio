namespace UotanStudio.Firmware.OtaDelta.Payload.Models;

/// <summary>
/// Parsed contents of an Android OTA <c>payload_properties.txt</c> file.
/// <para>Android OTA <c>payload_properties.txt</c> 文件的解析结果。</para>
/// </summary>
public class PayloadProperties
{
    /// <summary>
    /// <c>FILE_HASH</c> value: hex-encoded digest of the whole payload as written in the properties file.
    /// <para><c>FILE_HASH</c> 值：属性文件所记载的完整载荷十六进制摘要。</para>
    /// </summary>
    /// <value>
    /// The raw hex text from the file; <see cref="string.Empty"/> when the key is absent. The digest is not
    /// verified against payload bytes by this type.
    /// <para>来自文件的原始十六进制文本；键缺失时为 <see cref="string.Empty"/>。本类型不据此校验载荷字节。</para>
    /// </value>
    public string FileHash { get; internal set; } = string.Empty;

    /// <summary>
    /// <c>FILE_SIZE</c> value: total byte size of the payload file recorded in the properties.
    /// <para><c>FILE_SIZE</c> 值：属性中记载的载荷文件总字节大小。</para>
    /// </summary>
    /// <value>
    /// Size in bytes; <c>0</c> when the key is absent or its value does not parse as <see langword="long"/>.
    /// <para>字节大小；键缺失或其值无法解析为 <see langword="long"/> 时为 <c>0</c>。</para>
    /// </value>
    public long FileSize { get; internal set; }

    /// <summary>
    /// <c>METADATA_HASH</c> value: hex-encoded digest of the payload metadata block.
    /// <para><c>METADATA_HASH</c> 值：载荷元数据区块的十六进制摘要。</para>
    /// </summary>
    /// <value>
    /// The raw hex text from the file; <see cref="string.Empty"/> when the key is absent.
    /// <para>来自文件的原始十六进制文本；键缺失时为 <see cref="string.Empty"/>。</para>
    /// </value>
    public string MetadataHash { get; internal set; } = string.Empty;

    /// <summary>
    /// <c>METADATA_SIZE</c> value: byte size of the payload metadata block.
    /// <para><c>METADATA_SIZE</c> 值：载荷元数据区块的字节大小。</para>
    /// </summary>
    /// <value>
    /// Size in bytes; <c>0</c> when the key is absent or its value does not parse as <see langword="long"/>.
    /// <para>字节大小；键缺失或其值无法解析为 <see langword="long"/> 时为 <c>0</c>。</para>
    /// </value>
    public long MetadataSize { get; internal set; }

    /// <summary>
    /// Every key/value pair parsed from the properties file, keyed case-insensitively with whitespace trimmed.
    /// <para>从属性文件解析出的全部键值对；键不区分大小写，两端空白已裁剪。</para>
    /// </summary>
    /// <value>
    /// The complete property map including the well-known keys; empty when the file has no valid <c>key=value</c> lines.
    /// <para>包含所有已知键的完整属性映射；文件不含有效 <c>key=value</c> 行时为空。</para>
    /// </value>
    public IReadOnlyDictionary<string, string> Properties { get; internal set; } = new Dictionary<string, string>();
}