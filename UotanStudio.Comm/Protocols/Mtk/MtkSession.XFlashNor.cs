namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    /// <summary>Erases a NOR range with an explicitly supplied device-profile erase-block size. The NOR information query reports page size only; no erase geometry is guessed.
    /// <para>使用显式提供的设备配置擦除块大小擦除 NOR 范围。NOR 信息查询只上报页大小，不猜测擦除几何信息。</para></summary>
    /// <param name="offset">The nonnegative erase-block-aligned byte offset.
    /// <para>非负且按擦除块对齐的字节偏移。</para></param>
    /// <param name="length">The positive erase-block-aligned byte count.
    /// <para>正数且按擦除块对齐的字节数。</para></param>
    /// <param name="eraseBlockSize">A caller-selected power of two up to 16 MiB, no smaller than the reported page.
    /// <para>调用方选定的不超过 16 MiB 的 2 的幂，不小于上报页大小。</para></param>
    /// <param name="timeoutMs">The entire exchange deadline after acquiring the operation gate, from 1 ms to 24 hours.
    /// <para>获取操作门后整次交换的截止时间，范围为 1 毫秒至 24 小时。</para></param>
    /// <param name="cancellationToken">Cancellation for waiting and all wire exchanges.
    /// <para>等待与所有线协议交换的取消令牌。</para></param>
    /// <returns>A task completed only after the agent explicitly confirms completion.
    /// <para>仅在代理显式确认完成后结束的任务。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">The block size, timeout or range is invalid.
    /// <para>块大小、超时或范围无效。</para></exception>
    /// <exception cref="InvalidOperationException">The discovered storage is not NOR or the session is not ready.
    /// <para>发现的存储不是 NOR 或会话尚未就绪。</para></exception>
    /// <exception cref="TimeoutException">The completion deadline expired; the device may still be erasing.
    /// <para>完成截止时间已到；设备可能仍在擦除。</para></exception>
    public async Task EraseXFlashNorStorageAsync(long offset, long length, int eraseBlockSize,
        int timeoutMs = 120000, CancellationToken cancellationToken = default)
    {
        if (eraseBlockSize <= 0 || eraseBlockSize > 16 * 1024 * 1024 || (eraseBlockSize & (eraseBlockSize - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(eraseBlockSize));
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMs);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(timeoutMs, 86400000);
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EraseXFlashStorageOwnedAsync(8, offset, length, timeoutMs, cancellationToken, eraseBlockSize).ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
    }

    private sealed class NorReadWindowStream(Stream destination, long prefix, long length) : Stream
    {
        private long _position;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var start = (int)Math.Min(buffer.Length, Math.Max(0, prefix - _position));
            var end = (int)Math.Min(buffer.Length, Math.Max(0, prefix + length - _position));
            if (end > start)
            {
                await destination.WriteAsync(buffer[start..end], cancellationToken).ConfigureAwait(false);
            }
            _position += buffer.Length;
        }
    }
}