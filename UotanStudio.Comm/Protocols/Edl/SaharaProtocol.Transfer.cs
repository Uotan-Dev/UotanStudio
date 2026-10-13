using System.Buffers;

namespace UotanStudio.Comm.Protocols.Edl;

public sealed partial class SaharaProtocol
{
    private const int ImageTransferBufferSize = 64 * 1024;

    /// <summary>
    /// Validates that a READ_DATA/READ_DATA64 offset and length fall within the image length.
    /// <para>验证 READ_DATA/READ_DATA64 的偏移与长度是否落在镜像长度范围内。</para>
    /// </summary>
    /// <param name="offset">The requested data offset. 请求的数据偏移。</param>
    /// <param name="length">The requested data length. 请求的数据长度。</param>
    /// <param name="imageLength">The total image length. 镜像总长度。</param>
    /// <param name="command">The command name (for diagnostics). 命令名（用于诊断）。</param>
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