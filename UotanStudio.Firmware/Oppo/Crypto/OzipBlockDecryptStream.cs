using System.Security.Cryptography;
using UotanStudio.Firmware.Oppo.Helpers;

namespace UotanStudio.Firmware.Oppo.Crypto;

public sealed class OzipBlockDecryptStream : Stream
{
    private const int EncryptionUnitSize = OzipDecryptor.EncryptionUnitSize;

    private const int EncryptedSize = OzipDecryptor.EncryptedSize;

    private readonly Stream _inner;
    private readonly Aes _aes;
    private readonly ICryptoTransform _decryptor;
    private readonly bool _leaveOpen;
    private readonly long _dataLength;
    private long _position;

    private readonly byte[] _encBuf = new byte[EncryptedSize];
    private readonly byte[] _decBuf = new byte[EncryptedSize];
    private long _cachedUnitIndex = -1;

    /// <summary>
    /// Initializes a new instance of OzipBlockDecryptStream.
    /// <para>初始化 OzipBlockDecryptStream 的新实例。</para>
    /// </summary>
    public OzipBlockDecryptStream(Stream inner, byte[] aesKey, long dataLength, bool leaveOpen = false)
    {
        Guard.ThrowIfNull(inner);
        Guard.ThrowIfNull(aesKey);

        if (!inner.CanRead || !inner.CanSeek)
            throw new ArgumentException("Stream must be readable and seekable.", nameof(inner));
        if (aesKey.Length != EncryptedSize)
            throw new ArgumentException($"AES key must be {EncryptedSize} bytes.", nameof(aesKey));
        if (dataLength < 0 || dataLength > inner.Length - OzipHeader.DataOffset)
            throw new ArgumentOutOfRangeException(nameof(dataLength));

        _inner = inner;
        _dataLength = dataLength;
        _leaveOpen = leaveOpen;

        _aes = Aes.Create();
        _aes.Mode = CipherMode.ECB;
        _aes.Padding = PaddingMode.None;
        _decryptor = _aes.CreateDecryptor(aesKey, null);
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _dataLength;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (buffer.Length - offset < count)
            throw new ArgumentException("Offset and count exceed the buffer length.");

        if (count == 0 || _position >= _dataLength)
            return 0;

        var totalRead = 0;

        while (totalRead < count && _position < _dataLength)
        {
            var unitIndex = _position / EncryptionUnitSize;
            var offsetInUnit = (int)(_position % EncryptionUnitSize);

            if (offsetInUnit < EncryptedSize)
            {
                EnsurePrefixDecrypted(unitIndex);

                var toCopy = Math.Min(EncryptedSize - offsetInUnit, count - totalRead);
                toCopy = (int)Math.Min(toCopy, _dataLength - _position);
                Array.Copy(_decBuf, offsetInUnit, buffer, offset + totalRead, toCopy);
                totalRead += toCopy;
                _position += toCopy;
                continue;
            }

            var remainingInUnit = EncryptionUnitSize - offsetInUnit;
            var remainingInData = _dataLength - _position;
            var toRead = (int)Math.Min(Math.Min(remainingInUnit, count - totalRead), remainingInData);
            _inner.Position = OzipHeader.DataOffset + _position;
            var bytesRead = _inner.Read(buffer, offset + totalRead, toRead);
            if (bytesRead == 0)
            {
                throw new InvalidOppFormatException("Unexpected end of the OZip encrypted data region.");
            }

            totalRead += bytesRead;
            _position += bytesRead;
        }

        return totalRead;
    }

    /// <summary>
    /// Reads and decrypts the 16-byte prefix of the specified encryption unit.
    /// <para>读取并解密指定加密单元的 16 字节前缀。</para>
    /// </summary>
    private void EnsurePrefixDecrypted(long unitIndex)
    {
        if (_cachedUnitIndex == unitIndex)
            return;

        var unitFileOffset = checked(OzipHeader.DataOffset + unitIndex * EncryptionUnitSize);
        if (_dataLength - unitIndex * EncryptionUnitSize < EncryptedSize)
            throw new InvalidOppFormatException("The OZip data ends with an incomplete encrypted prefix.");

        _inner.Position = unitFileOffset;
        ReadExactly(_inner, _encBuf);

        if (_decryptor.TransformBlock(_encBuf, 0, EncryptedSize, _decBuf, 0) != EncryptedSize)
            throw new CryptographicException("AES did not decrypt a complete OZip block prefix.");

        _cachedUnitIndex = unitIndex;
    }

    private static void ReadExactly(Stream stream, byte[] buffer)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var bytesRead = stream.Read(buffer, totalRead, buffer.Length - totalRead);
            if (bytesRead == 0)
                throw new InvalidOppFormatException("Unexpected end of the OZip encrypted data region.");
            totalRead += bytesRead;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _decryptor.Dispose();
            _aes.Dispose();
            if (!_leaveOpen)
                _inner.Dispose();
        }
        base.Dispose(disposing);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(_dataLength + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        if (target < 0 || target > _dataLength)
            throw new IOException("Attempted to seek outside the OZip data region.");

        _position = target;
        return _position;
    }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
