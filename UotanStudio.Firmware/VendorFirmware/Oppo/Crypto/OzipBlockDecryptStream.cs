using System.Security.Cryptography;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.VendorFirmware.Oppo.Exceptions;

namespace UotanStudio.Firmware.VendorFirmware.Oppo.Crypto;

/// <summary>
/// Read-only, seekable view over the encrypted payload region of an OZip container. The region is a
/// sequence of 0x4010-byte units, each holding a 16-byte AES-128-ECB encrypted prefix followed by up to
/// 0x4000 stored plaintext bytes: only the prefix is decrypted, and it is decrypted once per unit and
/// cached while reads stay inside that unit. Bytes past a unit prefix are copied straight from the
/// inner stream at <c>OzipHeader.DataOffset + position</c>, so a read never buffers more than one unit.
/// The position is a logical offset into the decrypted data and is independent of the inner stream
/// position, which the view repositions on demand.
/// <para>
/// OZip 容器加密负载区的只读、可寻址视图。该区域由若干 0x4010 字节的单元顺序组成，每个单元包含一个
/// 16 字节的 AES-128-ECB 加密前缀，后随最多 0x4000 字节的直存明文：只有前缀会被解密，且在同一单元内
/// 的读取只解密一次并缓存。超出前缀的字节直接从内部流的 <c>OzipHeader.DataOffset + position</c> 处复制，
/// 因此单次读取不会缓冲超过一个单元。当前位置是解密数据内的逻辑偏移，与内部流位置无关，
/// 该视图按需重设内部流位置。
/// </para>
/// </summary>
internal sealed class OzipBlockDecryptStream : Stream
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
    /// <param name="inner">
    /// Underlying container stream holding the payload; readable and seekable, with its own position
    /// managed entirely by this view. Ownership follows <paramref name="leaveOpen"/>.
    /// <para>
    /// 承载负载的底层容器流；必须可读且可寻址，其自身位置完全由本视图管理。所有权由
    /// <paramref name="leaveOpen"/> 决定。
    /// </para>
    /// </param>
    /// <param name="aesKey">
    /// AES-128 key for the encrypted prefixes, exactly 16 bytes; used in ECB mode with an all-zero
    /// implicit IV.
    /// <para>用于加密前缀的 AES-128 密钥，必须为 16 字节；以 ECB 模式使用，隐含全零 IV。</para>
    /// </param>
    /// <param name="dataLength">
    /// Logical length in bytes of the decrypted data region, measured from <c>OzipHeader.DataOffset</c>.
    /// <para>解密数据区的逻辑长度（字节），从 <c>OzipHeader.DataOffset</c> 起算。</para>
    /// </param>
    /// <param name="leaveOpen">
    /// <see langword="true"/> to keep <paramref name="inner"/> open when this stream is disposed;
    /// <see langword="false"/> disposes it together with this stream.
    /// <para>
    /// 为 <see langword="true"/> 时，本流释放后保持 <paramref name="inner"/> 打开；为 <see langword="false"/>
    /// 时与之一起释放。
    /// </para>
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="inner"/> or <paramref name="aesKey"/> is <see langword="null"/>.
    /// <para><paramref name="inner"/> 或 <paramref name="aesKey"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="inner"/> cannot be read or seek, or <paramref name="aesKey"/> is not 16 bytes.
    /// <para><paramref name="inner"/> 不可读或不可寻址，或 <paramref name="aesKey"/> 长度不是 16 字节。</para>
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="dataLength"/> is negative, or it extends past the end of <paramref name="inner"/>.
    /// <para><paramref name="dataLength"/> 为负数，或超出 <paramref name="inner"/> 的末尾。</para>
    /// </exception>
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

    /// <summary>
    /// Whether bytes can be read through this view.
    /// <para>是否可以通过本视图读取字节。</para>
    /// </summary>
    /// <value>Always <see langword="true"/>. <para>恒为 <see langword="true"/>。</para></value>
    public override bool CanRead => true;
    /// <summary>
    /// Whether the logical position can be moved.
    /// <para>是否可以移动逻辑位置。</para>
    /// </summary>
    /// <value>Always <see langword="true"/>. <para>恒为 <see langword="true"/>。</para></value>
    public override bool CanSeek => true;
    /// <summary>
    /// Whether bytes can be written through this view.
    /// <para>是否可以通过本视图写入字节。</para>
    /// </summary>
    /// <value>
    /// Always <see langword="false"/>; the view decrypts, it does not produce ciphertext.
    /// <para>恒为 <see langword="false"/>；本视图只负责解密，不产生密文。</para>
    /// </value>
    public override bool CanWrite => false;
    /// <summary>
    /// Length of the decrypted data region.
    /// <para>解密数据区的长度。</para>
    /// </summary>
    /// <value>
    /// The <c>dataLength</c> given at construction, in bytes; reads past it return <c>0</c>.
    /// <para>构造时给定的 <c>dataLength</c>（字节）；越过该位置的读取返回 <c>0</c>。</para>
    /// </value>
    public override long Length => _dataLength;

    /// <summary>
    /// Current logical position inside the decrypted data region.
    /// <para>解密数据区内的当前逻辑位置。</para>
    /// </summary>
    /// <value>
    /// A byte offset in <c>[0, Length]</c>; setting it performs the same bounds check as
    /// <see cref="Seek(long, SeekOrigin)"/>.
    /// <para>范围为 <c>[0, Length]</c> 的字节偏移；赋值执行与 <see cref="Seek(long, SeekOrigin)"/> 相同的边界检查。</para>
    /// </value>
    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    /// <summary>
    /// Reads decrypted bytes from the current position into <paramref name="buffer"/> and advances the
    /// position. Unit prefixes are decrypted on demand; plaintext runs are copied straight from the
    /// inner stream. A short return only means the region end was reached.
    /// <para>
    /// 从当前位置向 <paramref name="buffer"/> 读取解密字节并前移位置。单元前缀按需解密，明文段直接从内部流复制。
    /// 返回数量偏少只表示已到达数据区末尾。
    /// </para>
    /// </summary>
    /// <param name="buffer">
    /// Destination array; must have <paramref name="count"/> writable bytes at <paramref name="offset"/>.
    /// <para>目标数组；在 <paramref name="offset"/> 处必须可写 <paramref name="count"/> 字节。</para>
    /// </param>
    /// <param name="offset">
    /// Zero-based index in <paramref name="buffer"/> where writing starts.
    /// <para><paramref name="buffer"/> 中开始写入位置的从零开始序号。</para>
    /// </param>
    /// <param name="count">
    /// Maximum number of bytes requested; <c>0</c> returns <c>0</c> without touching the inner stream.
    /// <para>请求的最大字节数；为 <c>0</c> 时直接返回 <c>0</c>，不访问内部流。</para>
    /// </param>
    /// <returns>
    /// The number of bytes actually written, which is <c>0</c> once the position reaches
    /// <see cref="Length"/>.
    /// <para>实际写入的字节数；位置到达 <see cref="Length"/> 后为 <c>0</c>。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="offset"/> and <paramref name="count"/> do not denote a valid range of
    /// <paramref name="buffer"/>.
    /// <para><paramref name="offset"/> 与 <paramref name="count"/> 未构成 <paramref name="buffer"/> 的有效区间。</para>
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="buffer"/> is <see langword="null"/>.
    /// <para><paramref name="buffer"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="offset"/> or <paramref name="count"/> is negative.
    /// <para><paramref name="offset"/> 或 <paramref name="count"/> 为负数。</para>
    /// </exception>
    /// <exception cref="InvalidOppFormatException">
    /// The inner stream ends before the requested data, or the unit prefix being decrypted is incomplete.
    /// <para>内部流在所需数据之前结束，或正在解密的单元前缀不完整。</para>
    /// </exception>
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

    /// <summary>
    /// Releases the AES decryptor and, unless <c>leaveOpen</c> was requested, the inner stream. Calling
    /// it more than once has the same effect as calling it once.
    /// <para>释放 AES 解密器，并在未要求 <c>leaveOpen</c> 时释放内部流。重复调用的效果与调用一次相同。</para>
    /// </summary>
    /// <param name="disposing">
    /// <see langword="true"/> when called from <see cref="Stream.Dispose()"/>; <see langword="false"/>
    /// only from a finalizer, where managed helpers are unavailable.
    /// <para>由 <see cref="Stream.Dispose()"/> 调用时为 <see langword="true"/>；仅在终结器中为 <see langword="false"/>，此时无法释放托管对象。</para>
    /// </param>
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

    /// <summary>
    /// Does nothing; the view holds no buffered output.
    /// <para>空操作；本视图不缓存待写出数据。</para>
    /// </summary>
    public override void Flush() { }
    /// <summary>
    /// Moves the logical read position within the decrypted data region.
    /// <para>在解密数据区内移动逻辑读取位置。</para>
    /// </summary>
    /// <param name="offset">
    /// Byte displacement from <paramref name="origin"/>; may be negative for <see cref="SeekOrigin.Current"/>
    /// and <see cref="SeekOrigin.End"/>.
    /// <para>相对 <paramref name="origin"/> 的字节偏移；在 <see cref="SeekOrigin.Current"/> 与 <see cref="SeekOrigin.End"/> 下可为负数。</para>
    /// </param>
    /// <param name="origin">
    /// Reference point for <paramref name="offset"/>, resolved against this view's position rather than
    /// the inner stream position.
    /// <para><paramref name="offset"/> 的基准点，基于本视图的位置解析，而非内部流位置。</para>
    /// </param>
    /// <returns>The new zero-based position within the data region.
    /// <para>数据区内新的从零开始位置。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="origin"/> is not a member of <see cref="SeekOrigin"/>.
    /// <para><paramref name="origin"/> 不是 <see cref="SeekOrigin"/> 的成员。</para>
    /// </exception>
    /// <exception cref="OverflowException">
    /// The computed target overflows a <see cref="long"/>.
    /// <para>计算出的目标位置超出 <see cref="long"/> 范围。</para>
    /// </exception>
    /// <exception cref="IOException">
    /// The computed target lies outside <c>[0, Length]</c>.
    /// <para>计算出的目标位置落在 <c>[0, Length]</c> 之外。</para>
    /// </exception>
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
    /// <summary>
    /// Resizing is unsupported: the data region length is fixed by the container header.
    /// <para>不支持改变长度：数据区长度由容器头部确定。</para>
    /// </summary>
    /// <param name="value">Ignored. <para>已忽略。</para></param>
    /// <exception cref="NotSupportedException">Always. <para>始终抛出。</para></exception>
    public override void SetLength(long value) => throw new NotSupportedException();
    /// <summary>
    /// Writing is unsupported: the view only decrypts an existing payload.
    /// <para>不支持写入：本视图只解密已有负载。</para>
    /// </summary>
    /// <param name="buffer">Ignored. <para>已忽略。</para></param>
    /// <param name="offset">Ignored. <para>已忽略。</para></param>
    /// <param name="count">Ignored. <para>已忽略。</para></param>
    /// <exception cref="NotSupportedException">Always. <para>始终抛出。</para></exception>
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}