namespace UotanStudio.Assets;

/// <summary>
/// One file inside an asset bundle, as recorded by the generated manifest.
/// <para>资源包中的一个文件，由生成的清单记录。</para>
/// </summary>
public sealed class AssetEntry
{
    internal AssetEntry(string bundle, string path, long length, string sha256, AssetEncoding encoding)
    {
        Bundle = bundle;
        Path = path;
        Length = length;
        Sha256 = sha256;
        Encoding = encoding;
    }

    /// <summary>Key of the bundle that owns this entry, for example <c>ohos/device</c>.
    /// <para>拥有该条目的资源包键，例如 <c>ohos/device</c>。</para>
    /// </summary>
    public string Bundle { get; }

    /// <summary>File name inside the bundle directory; it never contains a separator.
    /// <para>资源包目录内的文件名；不含任何分隔符。</para>
    /// </summary>
    public string Path { get; }

    /// <summary>Decoded length in bytes, which is also the exact length the frame must produce.
    /// <para>解码后的字节长度，也是压缩帧必须产出的精确长度。</para>
    /// </summary>
    public long Length { get; }

    /// <summary>Lowercase hexadecimal SHA-256 of the decoded bytes.
    /// <para>解码字节的小写十六进制 SHA-256。</para>
    /// </summary>
    public string Sha256 { get; }

    /// <summary>Encoding of the payload inside the assembly.
    /// <para>负载在程序集内的编码。</para>
    /// </summary>
    public AssetEncoding Encoding { get; }
}