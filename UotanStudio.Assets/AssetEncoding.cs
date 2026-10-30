namespace UotanStudio.Assets;

/// <summary>
/// Encoding a bundle payload uses inside the assembly.
/// <para>资源包负载在程序集内使用的编码。</para>
/// </summary>
public enum AssetEncoding
{
    /// <summary>A standalone zstd frame that decodes to the manifest length.
    /// <para>可解出清单长度的独立 zstd 帧。</para></summary>
    Zstd,
}