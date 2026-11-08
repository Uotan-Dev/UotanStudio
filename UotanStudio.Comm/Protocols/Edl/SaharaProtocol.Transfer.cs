using System.Buffers;

namespace UotanStudio.Comm.Protocols.Edl;

public sealed partial class SaharaProtocol
{
    private const int ImageTransferBufferSize = 64 * 1024;

    /// <summary>
    /// Validates that a READ_DATA/READ_DATA64 offset and length fall within the image length.
    /// <para>验证 READ_DATA/READ_DATA64 的偏移与长度是否落在镜像长度范围内。</para>
    /// </summary>
    /// <param name="offset">The requested data offset. <para>请求的数据偏移。</para></param>
    /// <param name="length">The requested data length. <para>请求的数据长度。</para></param>
    /// <param name="imageLength">The total image length. <para>镜像总长度。</para></param>
    /// <param name="command">The command name (for diagnostics). <para>命令名（用于诊断）。</para></param>
    private static void ValidateReadRange(long offset, long length, long imageLength, string command)
    {
        if (offset < 0 || length < 0 || imageLength < 0 || offset > imageLength || length > imageLength - offset)
        {
            throw new SaharaException($"{command} requests invalid range: offset={offset}, length={length}, image length={imageLength}");
        }
    }

    private async Task TransferImageRangeAsync(Stream image, long length, string command, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ImageTransferBufferSize);
        try
        {
            var remaining = length;
            while (remaining > 0)
            {
                var count = (int)Math.Min(remaining, buffer.Length);
                var total = 0;
                while (total < count)
                {
                    var read = await image.ReadAsync(buffer.AsMemory(total, count - total), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        throw new SaharaException($"{command} image stream ended before the requested {length} bytes were read.");
                    }
                    total += read;
                }

                long written;
                try
                {
                    written = await _transport.WriteAsync(buffer, 0, count, _timeoutMs, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException ex)
                {
                    throw new EdlException(EdlErrorCode.WritePortTimeout, ex.Message, ex);
                }
                if (written != count)
                {
                    throw new EdlException(EdlErrorCode.WritePortTimeout);
                }
                remaining -= count;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

/// <summary>
/// Carries the progress of one Sahara image upload, reported after each READ_DATA/READ_DATA64 block
/// the target requested has been served.
/// <para>携带一次 Sahara 镜像上传的进度；在目标请求的每个 READ_DATA/READ_DATA64 块被服务完后上报。</para>
/// </summary>
/// <param name="imageId">The Sahara image identifier being uploaded.<para>正在上传的 Sahara 镜像标识。</para></param>
/// <param name="bytesUploaded">The cumulative bytes uploaded for this image so far.<para>该镜像目前已累计上传的字节数。</para></param>
/// <param name="imageBytes">The total image length in bytes, or <see langword="null"/> when the serving stream cannot report one.
/// <para>镜像总字节数；提供镜像的流无法给出时为 <see langword="null"/>。</para></param>
public sealed class SaharaImageProgressEventArgs(uint imageId, long bytesUploaded, long? imageBytes) : EventArgs
{
    /// <summary>Gets the Sahara image identifier being uploaded.
    /// <para>获取正在上传的 Sahara 镜像标识。</para></summary>
    public uint ImageId { get; } = imageId;

    /// <summary>Gets the cumulative bytes uploaded for this image so far.
    /// <para>获取该镜像目前已累计上传的字节数。</para></summary>
    public long BytesUploaded { get; } = bytesUploaded;

    /// <summary>Gets the total image length in bytes, or <see langword="null"/> when unknown.
    /// <para>获取镜像总字节数；未知时为 <see langword="null"/>。</para></summary>
    public long? ImageBytes { get; } = imageBytes;
}