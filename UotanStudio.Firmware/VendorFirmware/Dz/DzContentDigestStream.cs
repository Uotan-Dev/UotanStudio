using UotanStudio.Hash.Crc32Ieee;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Firmware.VendorFirmware.Dz;

/// <summary>
/// Read-only pass-through stream that folds every consumed byte into the three DZ content digests in the same read that
/// feeds the compressor.
/// <para>只读透传流，在供给压缩器的同一次读取中把每个被消费的字节并入 DZ 的三种内容摘要。</para>
/// </summary>
/// <remarks>
/// The stream is single-use: call <see cref="GetHashesAndReset"/> once the reader has consumed the content, then
/// dispose it. Position reads forward to the inner stream while writes and seeking are rejected, so the digests always
/// cover exactly the bytes handed to the compressor.
/// <para>该流为一次性使用：读取方消费完内容后调用 <see cref="GetHashesAndReset"/>，随后释放。位置读取转发到底层流，
/// 写入与定位被拒绝，因此摘要始终恰好覆盖交给压缩器的字节。</para>
/// </remarks>
internal sealed class DzContentDigestStream(Stream inner, bool leaveOpen) : Stream
{
    private readonly DigestAccumulator _sha256 = Sha256.CreateAccumulator();
    private readonly DigestAccumulator _md5 = Md5.CreateAccumulator();
    private readonly Crc32IeeeAccumulator _crc32 = new();
    private bool _disposed;

    /// <summary>The inner source consumed from its current position. <para>从当前位置开始消费的底层来源。</para></summary>
    private Stream Inner { get; } = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <summary>
    /// Returns the SHA-256, MD5 and CRC-32 of the bytes consumed so far and resets the accumulators.
    /// <para>返回至今已消费字节的 SHA-256、MD5 与 CRC-32，并重置累加器。</para>
    /// </summary>
    /// <returns>The three digests in the order the DZ chunk header records them. <para>按 DZ 块头记录顺序排列的三种摘要。</para></returns>
    public (byte[] Sha256, byte[] Md5, uint Crc32) GetHashesAndReset()
        => (_sha256.GetHashAndReset(), _md5.GetHashAndReset(), _crc32.GetCurrentHash());

    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => Inner.Length;
    public override long Position
    {
        get => Inner.Position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = Inner.Read(buffer, offset, count);
        Append(buffer.AsSpan(offset, read));
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var read = Inner.Read(buffer);
        Append(buffer[..read]);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await Inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Append(buffer[..read].Span);
        return read;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _sha256.Dispose();
            _md5.Dispose();
            if (!leaveOpen)
            {
                Inner.Dispose();
            }
            _disposed = true;
        }

        base.Dispose(disposing);
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        _sha256.Append(data);
        _md5.Append(data);
        _crc32.Append(data);
    }
}