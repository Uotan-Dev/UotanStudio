namespace UotanStudio.Firmware.Core.Streams;

/// <summary>
/// Probes whether a destination stream can be resized in place, so builders that pre-size their image
/// can write the destination directly instead of staging through a temporary file.
/// <para>探测目标流是否支持原地改变长度，使需要预设镜像大小的构建器可以直接写目标流而无需经临时文件中转。</para>
/// </summary>
internal static class StreamResize
{
    /// <summary>
    /// Determines whether <see cref="Stream.SetLength"/> works on the stream without disturbing its
    /// content; the probe restores the original position either way.
    /// <para>判断 <see cref="Stream.SetLength"/> 是否可在不扰动内容的前提下工作；无论结果如何都会恢复原位置。</para>
    /// </summary>
    /// <param name="output">Seekable stream to probe. <para>待探测的可定位流。</para></param>
    /// <returns><see langword="true"/> when <see cref="Stream.SetLength"/> is supported.
    /// <para>支持 <see cref="Stream.SetLength"/> 时为 <see langword="true"/>。</para></returns>
    internal static bool SupportsResizeWithoutMutation(Stream output)
    {
        var position = output.Position;
        try
        {
            output.SetLength(output.Length);
            return true;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        finally
        {
            output.Position = position;
        }
    }
}