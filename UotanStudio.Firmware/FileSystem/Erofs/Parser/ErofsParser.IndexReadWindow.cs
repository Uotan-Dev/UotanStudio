namespace UotanStudio.Firmware.FileSystem.Erofs.Parser;

/// <summary>
/// EROFS parser part that owns the bounded per-file index read window, so repeated compact-index lookups reuse one cached buffer instead of re-reading the image.
/// <para>EROFS 解析器的组成部分，提供按文件受限的索引读取窗口，使重复的紧凑索引查询复用同一缓存缓冲区，而不是反复读取镜像。</para>
/// </summary>
public sealed partial class ErofsParser
{
    /// <summary>A bounded, per-file index window; only source reads share the parser lock.
    /// <para>按文件划分的有界索引窗口；只有源读取会共享解析器锁。</para></summary>
    private sealed class IndexReadWindow
    {
        private readonly ErofsParser _parser;
        private readonly ulong _nid;
        private readonly long _start;
        private readonly long _length;
        private readonly byte[] _buffer;
        private long _offset = -1;
        private int _count;

        internal IndexReadWindow(ErofsParser parser, ulong nid, long start, long length)
        {
            if (start < 0 || length < 0 || start > parser.InodeMetadataLength(nid)
                || length > parser.InodeMetadataLength(nid) - start)
            {
                throw new FileSystemCorruptedException("Compressed index exceeds the inode metadata stream.");
            }
            _parser = parser;
            _nid = nid;
            _start = start;
            _length = length;
            _buffer = new byte[(int)Math.Min(65536, length)];
        }

        internal byte[] Get(long offset, int count, out int relative)
        {
            if (offset < 0 || count < 0 || offset > _length || count > _length - offset || count > _buffer.Length)
                throw new FileSystemCorruptedException("Compressed index entry exceeds the image bounds.");
            if (_offset < 0 || offset < _offset || offset - _offset > _count - count)
            {
                _offset = offset;
                _count = (int)Math.Min(_buffer.Length, _length - offset);
                if (_parser.IsMetaboxInode(_nid))
                {
                    _parser.ReadMetaboxSpan(_start + offset, _count, "Compressed index")
                        .CopyTo(_buffer.AsSpan(0, _count));
                }
                else
                {
                    _parser.ReadPositioned(_parser._stream,
                        checked(_parser._baseOffset + _start + offset), _buffer, _count);
                }
            }
            relative = checked((int)(offset - _offset));
            return _buffer;
        }
    }
}