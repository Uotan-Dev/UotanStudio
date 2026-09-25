namespace UotanStudio.Comm.Protocols.Bootrom;

public sealed partial class RkdlSession
{
    /// <summary>
    /// Downloads a complete RKBOOT loader image: parses the entry tables, sends every 471
    /// (first-stage) entry followed by every 472 (second-stage) entry through the
    /// 0x0471/0x0472 vendor requests, honouring each entry's delay, and finally waits one
    /// second for the device to re-enumerate in loader mode (rkdeveloptool CRKDevice::DownloadBoot).
    /// <para>下载完整的 RKBOOT loader 镜像：解析条目表，先通过 0x0471 vendor 请求发送全部 471
    /// （第一阶段）条目，再通过 0x0472 请求发送全部 472（第二阶段）条目，遵循每个条目的延时，
    /// 最后等待一秒让设备重新枚举为 loader 模式（rkdeveloptool 的 CRKDevice::DownloadBoot）。</para>
    /// </summary>
    /// <param name="loaderImage">The loader image bytes (RKBOOT .bin). <para>loader 镜像字节（RKBOOT .bin）。</para></param>
    /// <returns>The total number of bytes transferred. <para>实际传输的字节总数。</para></returns>
    public int DownloadBootLoader(byte[] loaderImage)
    {
        ArgumentNullException.ThrowIfNull(loaderImage);
        if (!IsMaskromMode)
        {
            throw new RkdlException(RkdlError.NotSupported, "Loader download requires a maskrom-mode device (0x0471/0x0472 vendor requests).");
        }

        var image = RkBootImage.Parse(loaderImage);
        var total = 0;

        foreach (var entry in image.Entries471)
        {
            total += DeviceRequest(RkdlConstants.RkdlWIndex471, SliceEntry(loaderImage, entry));
            if (entry.DataDelay > 0)
            {
                Thread.Sleep((int)entry.DataDelay);
            }
        }

        foreach (var entry in image.Entries472)
        {
            total += DeviceRequest(RkdlConstants.RkdlWIndex472, SliceEntry(loaderImage, entry));
            if (entry.DataDelay > 0)
            {
                Thread.Sleep((int)entry.DataDelay);
            }
        }

        Thread.Sleep(1000);
        return total;
    }

    /// <summary>
    /// Asynchronously downloads a complete RKBOOT loader image; see
    /// <see cref="DownloadBootLoader(byte[])"/> for the protocol flow.
    /// <para>异步下载完整的 RKBOOT loader 镜像；协议流程见
    /// <see cref="DownloadBootLoader(byte[])"/>。</para>
    /// </summary>
    /// <param name="loaderImage">The loader image bytes (RKBOOT .bin). <para>loader 镜像字节（RKBOOT .bin）。</para></param>
    /// <param name="cancellationToken">A cancellation token. <para>取消令牌。</para></param>
    /// <returns>A task that resolves to the total number of bytes transferred. <para>返回实际传输字节总数的任务。</para></returns>
    public Task<int> DownloadBootLoaderAsync(byte[] loaderImage, CancellationToken cancellationToken = default)
        => Task.Run(() => DownloadBootLoader(loaderImage), cancellationToken);

    /// <summary>
    /// Extracts the data slice of a loader entry. <para>提取 loader 条目的数据切片。</para>
    /// </summary>
    private static byte[] SliceEntry(byte[] loaderImage, RkBootEntry entry)
    {
        var end = (long)entry.DataOffset + entry.DataSize;
        if (entry.DataOffset >= loaderImage.Length || end > loaderImage.Length)
        {
            throw new RkdlException(RkdlError.InvalidParameter, $"Loader entry '{entry.Name}' data out of range.");
        }

        var data = new byte[entry.DataSize];
        Array.Copy(loaderImage, entry.DataOffset, data, 0, entry.DataSize);
        return data;
    }

    /// <summary>
    /// Downloads the first-stage loader via the 0x0471 vendor request
    /// (BmRequestType = 0x40, BRequest = 0x0C, wIndex = 0x0471). Data is sent in 4096-byte
    /// chunks with a trailing CRC-16/CCITT, including the 4094/4095 padding rules from
    /// rkdeveloptool RKU_DeviceRequest.
    /// <para>通过 0x0471 vendor 请求下载第一阶段 loader
    /// （BmRequestType = 0x40，BRequest = 0x0C，wIndex = 0x0471）。数据按 4096 字节分块发送，
    /// 尾部追加 CRC-16/CCITT，并遵循 rkdeveloptool RKU_DeviceRequest 的 4094/4095 填充规则。</para>
    /// </summary>
    /// <param name="loaderData">The loader image data. <para>loader 镜像数据。</para></param>
    /// <returns>The total number of bytes transferred. <para>实际传输的字节总数。</para></returns>
    public int DownloadBootLoader471(byte[] loaderData)
        => DeviceRequest(RkdlConstants.RkdlWIndex471, loaderData);

    /// <summary>
    /// Downloads the second-stage loader via the 0x0472 vendor request
    /// (BmRequestType = 0x40, BRequest = 0x0C, wIndex = 0x0472). See
    /// <see cref="DownloadBootLoader471"/> for the framing rules.
    /// <para>通过 0x0472 vendor 请求下载第二阶段 loader
    /// （BmRequestType = 0x40，BRequest = 0x0C，wIndex = 0x0472）。
    /// 帧规则见 <see cref="DownloadBootLoader471"/>。</para>
    /// </summary>
    /// <param name="loaderData">The loader image data. <para>loader 镜像数据。</para></param>
    /// <returns>The total number of bytes transferred. <para>实际传输的字节总数。</para></returns>
    public int DownloadBootLoader472(byte[] loaderData)
        => DeviceRequest(RkdlConstants.RkdlWIndex472, loaderData);

}