using System.Security.Cryptography;
using UotanStudio.Firmware.Oppo.Helpers;

namespace UotanStudio.Firmware.Oppo.Crypto;

/// <summary>
/// <para>提供对 OZip 加密数据的按需解密流。每个加密单元由 16 字节 AES-128-ECB 密文前缀和
/// 随后的 0x4000 字节明文组成。可直接包装在加密文件的流上，
/// 从任意偏移顺序读取，无需先解密整个文件。</para>
/// Provides an on-demand decryption stream for OZip encrypted data. Each encryption unit consists
/// of a 16-byte AES-128-ECB encrypted prefix followed by 0x4000 plaintext bytes, which pass
/// through as-is. Wraps directly over the encrypted file stream so entries can be
/// read sequentially from any offset without pre-decrypting the whole file.
/// </summary>
/// <remarks>
/// <para>
/// OZIP Mode 1 格式布局：
/// <code>
/// [0x00..0x104F]  头部（魔数 + 大小 + 哈希 + 元数据）
/// [0x1050..EOF]   加密数据区域，以 0x4010 字节为一个加密单元：
///                   前 16 字节 → AES-128-ECB 密文
///                   后 0x4000 字节 → 明文原样
/// </code>
/// 本流从 <c>DataOffset</c>（0x1050）开始计算块边界并自行定位底层流；
/// 本流的位置 0 对应 OZip 文件偏移 0x1050。
/// </para>
/// </remarks>
public sealed class OzipBlockDecryptStream : Stream
{
    private const int EncryptionUnitSize = OzipDecryptor.EncryptionUnitSize;

    /// <summary><para>每块中加密部分的大小。</para>Encrypted portion per block.</summary>
    private const int EncryptedSize = OzipDecryptor.EncryptedSize; // 16

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
    /// <para>初始化 <see cref="OzipBlockDecryptStream"/> 的新实例。</para>
    /// Initializes a new instance of <see cref="OzipBlockDecryptStream"/>.
    /// </summary>
    /// <param name="inner">
    /// <para>底层原始文件流（未定位；本流自行管理定位）。</para>
    /// The underlying raw file stream (no pre-positioning required; this stream manages seeks internally).
    /// </param>
    /// <param name="aesKey"><para>AES-128 密钥（16 字节）。</para>The AES-128 key (16 bytes).</param>
    /// <param name="dataLength"><para>加密数据区域的总字节数。</para>Total byte count of the encrypted data region.</param>
    /// <param name="leaveOpen"><para>释放本流时是否保留底层流不关闭。</para>Whether to leave the inner stream open when this stream is disposed.</param>
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
    /// <para>读取并解密指定加密单元的 16 字节前缀。</para>
    /// Reads and decrypts the 16-byte prefix of the specified encryption unit.
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