namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;

public sealed partial class ErofsParser
{
    /// <summary>A bounded, per-file index window; only source reads share the parser lock.</summary>
    private sealed class IndexReadWindow
    {
        private readonly ErofsParser _parser;
        private readonly long _start;
        private readonly long _length;
        private readonly byte[] _buffer;
        private long _offset = -1;
        private int _count;

        internal IndexReadWindow(ErofsParser parser, long start, long length)
        {
            parser.CheckRange(start, length, "Compressed index");
            _parser = parser;
            _start = start;
            _length = length;
            _buffer = new byte[(int)Math.Min(65536, length)];
        }

        internal byte[] Get(long offset, int count, out int relative)
        {
            if (offset < 0 || count < 0 || offset > _length || count > _length - offset || count > _buffer.Length)
                throw new InvalidDataException("Compressed index entry exceeds the image bounds.");
            if (_offset < 0 || offset < _offset || offset - _offset > _count - count)
            {
                _offset = offset;
                _count = (int)Math.Min(_buffer.Length, _length - offset);
                lock (_parser._streamLock)
                {
                    _parser._stream.Position = checked(_parser._baseOffset + _start + offset);
                    _parser._stream.ReadExactly(_buffer, 0, _count);
                }
            }
            relative = checked((int)(offset - _offset));
            return _buffer;
        }
    }
}