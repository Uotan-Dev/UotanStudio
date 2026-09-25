namespace UotanStudio.Media.Decoding;

/// <summary>
/// Identifies the compressed video codec carried by the input stream.
/// <para>标识输入流承载的压缩视频编码格式。</para>
/// </summary>
public enum VideoCodecId
{
    /// <summary>
    /// H.264 / MPEG-4 AVC.
    /// <para>H.264（MPEG-4 AVC）编码。</para>
    /// </summary>
    H264 = 1,

    /// <summary>
    /// H.265 / HEVC.
    /// <para>H.265（HEVC）编码。</para>
    /// </summary>
    H265 = 2,
    /// <summary>
    /// AV1 low-overhead OBU stream.
    /// <para>AV1 低开销 OBU 码流。</para>
    /// </summary>
    Av1 = 3,
}