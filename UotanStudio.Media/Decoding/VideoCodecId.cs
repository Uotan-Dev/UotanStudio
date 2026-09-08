namespace UotanStudio.Media.Decoding;

/// <summary>
/// Identifies the compressed video codec carried by the input stream.
/// <para>标识输入流承载的压缩视频编码格式。</para>
/// </summary>
public enum VideoCodecId
{
    /// <summary>H.264 / MPEG-4 AVC. H.264（MPEG-4 AVC）编码。</summary>
    H264 = 1,

    /// <summary>H.265 / HEVC. H.265（HEVC）编码。</summary>
    H265 = 2,
}
