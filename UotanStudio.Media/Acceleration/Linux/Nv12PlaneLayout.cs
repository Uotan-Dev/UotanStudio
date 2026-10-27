namespace UotanStudio.Media.Acceleration.Linux;

/// <summary>
/// Channel order of an imported NV12 chroma layer, as the sampling shader must know it.
/// <para>导入的 NV12 色度平面的通道次序，采样着色器需要据此取值。</para>
/// </summary>
public enum DmabufChromaOrder
{
    /// <summary>
    /// The first sampled channel is Cb and the second is Cr, matching a <c>DRM_FORMAT_RG88</c> layer.
    /// <para>第一个采样通道为 Cb、第二个为 Cr，对应 <c>DRM_FORMAT_RG88</c> 层。</para>
    /// </summary>
    CbCr,
    /// <summary>
    /// The first sampled channel is Cr and the second is Cb, matching a <c>DRM_FORMAT_GR88</c> layer whose
    /// two components are ordered the other way in memory.
    /// <para>第一个采样通道为 Cr、第二个为 Cb，对应内存中两分量次序相反的 <c>DRM_FORMAT_GR88</c> 层。</para>
    /// </summary>
    CrCb,
}

/// <summary>
/// Classifies the two DRM layers a VA-API NV12 export must present.
/// <para>判定 VA-API 导出 NV12 必须给出的两个 DRM 层。</para>
/// </summary>
/// <remarks>
/// The bridge requests separate layers, so an NV12 surface is a single-plane <c>R8</c> luma layer plus a
/// single-plane interleaved chroma layer in <c>RG88</c> or <c>GR88</c>. Anything else — a composed
/// single-layer descriptor, three planar planes, or an unfamiliar chroma fourcc — cannot be sampled by the
/// shader and must fall back to a downloaded frame instead of being presented. Both chroma fourcc spellings are
/// accepted.
/// <para>桥接层请求分离平面，因此 NV12 表面应当是一个单平面的 <c>R8</c> 亮度层加一个单平面的交错色度层，色度四字符码为
/// <c>RG88</c> 或 <c>GR88</c>。其他情形——合成后的单层描述符、三个平面或不认识的色度四字符码——着色器无法采样，
/// 必须回退为下载帧而不是呈现。两种色度四字符码写法都被接受。</para>
/// </remarks>
internal static class Nv12PlaneLayout
{
    internal const uint LumaR8 = 0x20203852;
    internal const uint ChromaRg88 = 0x38384752;
    internal const uint ChromaGr88 = 0x38385247;

    /// <summary>
    /// Tests a luma/chroma layer pair and reports the chroma channel order.
    /// <para>判定亮度与色度层组合，并报告色度通道次序。</para>
    /// </summary>
    /// <param name="layerCount">
    /// Number of layers in the descriptor; exactly two are accepted.
    /// <para>描述符中的层数；仅接受两层。</para>
    /// </param>
    /// <param name="lumaFourcc">
    /// Fourcc of the first layer, expected to be <c>DRM_FORMAT_R8</c>.
    /// <para>第一层的四字符码，期望为 <c>DRM_FORMAT_R8</c>。</para>
    /// </param>
    /// <param name="lumaPlanes">
    /// Plane count of the first layer, expected to be one.
    /// <para>第一层的平面数，期望为一。</para>
    /// </param>
    /// <param name="chromaFourcc">
    /// Fourcc of the second layer, expected to be <c>RG88</c> or <c>GR88</c>.
    /// <para>第二层的四字符码，期望为 <c>RG88</c> 或 <c>GR88</c>。</para>
    /// </param>
    /// <param name="chromaPlanes">
    /// Plane count of the second layer, expected to be one.
    /// <para>第二层的平面数，期望为一。</para>
    /// </param>
    /// <param name="order">
    /// <see cref="DmabufChromaOrder.CbCr"/> for <c>RG88</c> or <see cref="DmabufChromaOrder.CrCb"/> for
    /// <c>GR88</c>; unset when the pair is rejected.
    /// <para><c>RG88</c> 返回 <see cref="DmabufChromaOrder.CbCr"/>，<c>GR88</c> 返回
    /// <see cref="DmabufChromaOrder.CrCb"/>；组合被拒绝时不赋值。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the pair is a sampleable NV12 layout.
    /// <para>该组合是可采样的 NV12 布局时返回 <see langword="true"/>。</para>
    /// </returns>
    internal static bool TryClassify(uint layerCount, uint lumaFourcc, uint lumaPlanes, uint chromaFourcc, uint chromaPlanes, out DmabufChromaOrder order)
    {
        order = default;
        if (layerCount != 2 || lumaFourcc != LumaR8 || lumaPlanes != 1 || chromaPlanes != 1) return false;
        switch (chromaFourcc)
        {
            case ChromaRg88:
                order = DmabufChromaOrder.CbCr;
                return true;
            case ChromaGr88:
                order = DmabufChromaOrder.CrCb;
                return true;
            default:
                return false;
        }
    }
}