using System.Buffers;
using System.IO.Compression;

namespace UotanStudio.Firmware.Diff;

// BrotliStream treats an incomplete stream's EOF as a normal read end. Patches need
// the decoder's Done state so that truncation after the last output byte still fails.
internal sealed class StrictBrotliReadStream(Stream input) : Stream
{
    private BrotliDecoder _decoder;
    private readonly byte[] _buffer = new byte[64 * 1024];
    private int _offset;
    private int _count;
    private bool _finished;

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty || _finished) return 0;
        while (true)
        {
            if (_count == 0) { _offset = 0; _count = input.Read(_buffer); }
            var status = _decoder.Decompress(_buffer.AsSpan(_offset, _count), buffer, out var consumed, out var written);
            _offset += consumed; _count -= consumed;
            if (status == OperationStatus.InvalidData) throw new InvalidDataException("Invalid Brotli patch stream.");
            if (status == OperationStatus.Done)
            {
                if (_count != 0 || input.ReadByte() != -1) throw new InvalidDataException("Trailing bytes in Brotli patch stream.");
                _finished = true; return written;
            }
            if (written > 0) return written;
            if (consumed == 0) throw new InvalidDataException("Truncated Brotli patch stream.");
        }
    }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    protected override void Dispose(bool disposing) { if (disposing) _decoder.Dispose(); base.Dispose(disposing); }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}