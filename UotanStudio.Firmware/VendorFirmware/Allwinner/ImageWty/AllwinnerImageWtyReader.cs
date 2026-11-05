namespace UotanStudio.Firmware.VendorFirmware.Allwinner.ImageWty;

/// <summary>
/// Public reading facade of the Allwinner IMAGEWTY container: header and entry metadata, per-entry payload
/// extraction and effective length, all bounded like the internal format reader they delegate to.
/// <para>Allwinner IMAGEWTY 容器的公共读取门面：头与条目元数据、按条目的负载提取与有效长度；
/// 全部有界，实际工作委托给内部格式读取器。</para>
/// </summary>
public static class AllwinnerImageWtyReader
{
    /// <summary>
    /// Reports whether the container starts with the plaintext <c>IMAGEWTY</c> magic.
    /// <para>判断容器是否以明文 <c>IMAGEWTY</c> 魔数开头。</para>
    /// </summary>
    /// <param name="header">Leading bytes of the file, at least 8. <para>文件起始字节，至少 8 个。</para></param>
    /// <returns><see langword="true"/> when the magic matches. <para>魔数匹配时为 <see langword="true"/>。</para></returns>
    public static bool HasPlainMagic(ReadOnlySpan<byte> header) => AllwinnerImageWtyFormat.HasPlainMagic(header);

    /// <summary>
    /// Reads and validates the header record and the file table of a container.
    /// <para>读取并校验容器的头部记录与文件表。</para>
    /// </summary>
    /// <param name="input">Readable, seekable container stream; position is restored on return.
    /// <para>可读、可寻址的容器流；返回时恢复位置。</para></param>
    /// <param name="cancellationToken">Cancels the read. <para>取消本次读取。</para></param>
    /// <returns>The validated header with its records. <para>已校验的头部及其记录。</para></returns>
    /// <exception cref="ArgumentException">The stream cannot be read or cannot seek.
    /// <para>流不可读或不可寻址。</para></exception>
    /// <exception cref="InvalidDataException">The header is truncated or carries an unknown version, the magic is
    /// absent both before and after RC6 decryption, the record count or table range is invalid, or a record
    /// declares an out-of-range payload. <para>头部被截断或版本未知、魔数在 RC6 解密前后都不匹配、记录数量或表
    /// 区间无效，或记录声明了越界负载。</para></exception>
    /// <exception cref="OperationCanceledException">The token was canceled. <para>令牌被取消。</para></exception>
    public static ImageWtyHeader Read(Stream input, CancellationToken cancellationToken = default) =>
        AllwinnerImageWtyFormat.Read(input, cancellationToken);

    /// <summary>
    /// Copies one entry payload into the output stream, RC6-decrypting first when the container is encrypted.
    /// <para>把一个条目的负载复制到输出流；容器加密时先经 RC6 解密。</para>
    /// </summary>
    /// <param name="input">Container stream the entry was read from. <para>读取条目所用容器流。</para></param>
    /// <param name="entry">Entry to extract. <para>待提取的条目。</para></param>
    /// <param name="output">Destination stream. <para>目标流。</para></param>
    /// <param name="decrypt">Whether the container payloads are RC6-encrypted.
    /// <para>容器负载是否经 RC6 加密。</para></param>
    /// <param name="cancellationToken">Cancels the copy. <para>取消本次复制。</para></param>
    /// <returns>The number of bytes written. <para>写入的字节数。</para></returns>
    /// <exception cref="ArgumentNullException">Any argument is null. <para>任一参数为 null。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The entry offset or length is negative.
    /// <para>条目偏移或长度为负。</para></exception>
    /// <exception cref="EndOfStreamException">The container ends before the declared payload is complete.
    /// <para>容器在声明的负载完成前结束。</para></exception>
    /// <exception cref="OperationCanceledException">The token was canceled. <para>令牌被取消。</para></exception>
    public static long ExtractEntry(Stream input, ImageWtyEntry entry, Stream output, bool decrypt,
        CancellationToken cancellationToken = default) =>
        AllwinnerImageWtyFormat.ExtractEntry(input, entry, output, decrypt, cancellationToken);

    /// <summary>
    /// Returns the number of payload bytes an entry contributes to an unpacked file: its stored
    /// length, truncated to the original length only when that length is declared and smaller.
    /// <para>返回条目对解包文件贡献的负载字节数：即其已存储长度，仅当原始长度已声明且更小时截断到该长度。</para>
    /// </summary>
    /// <param name="entry">Entry to measure. <para>待度量的条目。</para></param>
    /// <returns>Byte count written for the entry. <para>为该条目写出的字节数。</para></returns>
    public static long EffectiveLength(ImageWtyEntry entry) => AllwinnerImageWtyFormat.EffectiveLength(entry);

    /// <summary>
    /// Opens a seekable, decrypted view of one entry payload, which lets callers parse a stored image
    /// (for example an Android sparse container) without materializing the whole entry.
    /// <para>打开单个条目负载的可寻址解密视图，使调用方无需整条目落盘即可解析其中存储的镜像（例如 Android
    /// 稀疏容器）。</para>
    /// </summary>
    /// <param name="input">Container stream the entry was read from; stays open and is shared by every view.
    /// <para>读取条目所用的容器流；保持打开并与所有视图共享。</para></param>
    /// <param name="entry">Entry to open. <para>待打开的条目。</para></param>
    /// <param name="decrypt">Whether container payloads are RC6-encrypted. <para>容器负载是否经 RC6 加密。</para></param>
    /// <returns>A read-only stream over the entry's effective payload. <para>条目有效负载之上的只读流。</para></returns>
    /// <exception cref="ArgumentNullException">An argument is null. <para>某个参数为 null。</para></exception>
    public static Stream OpenEntryStream(Stream input, ImageWtyEntry entry, bool decrypt)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(entry);
        return new ImageWtyEntryStream(input, entry, decrypt);
    }

    /// <summary>
    /// Copies one 16-byte-aligned segment of an entry payload into the output stream, decrypting complete
    /// blocks in place when the container is encrypted; the caller keeps segment offsets at multiples of the
    /// 16-byte block. <para>把一个条目负载的 16 字节对齐段复制到输出流；容器加密时对完整分组就地解密；
    /// 调用方应让段偏移保持 16 字节分组对齐。</para>
    /// </summary>
    /// <param name="input">Container stream the entry was read from. <para>读取条目所用容器流。</para></param>
    /// <param name="entry">Entry to extract from. <para>提取来源条目。</para></param>
    /// <param name="offset">Segment offset from the entry payload start; a multiple of the block size.
    /// <para>段相对条目负载起点的偏移；须为分组大小倍数。</para></param>
    /// <param name="count">Bytes to copy; the segment must stay inside the effective payload.
    /// <para>复制的字节数；段必须留在有效负载之内。</para></param>
    /// <param name="output">Destination stream. <para>目标流。</para></param>
    /// <param name="decrypt">Whether the container payloads are RC6-encrypted.
    /// <para>容器负载是否经 RC6 加密。</para></param>
    /// <param name="cancellationToken">Cancels the copy. <para>取消本次复制。</para></param>
    /// <returns>A task that produces the number of bytes written, which is <paramref name="count"/>.
    /// <para>产生写出的字节数（即 <paramref name="count"/>）的任务。</para></returns>
    /// <exception cref="ArgumentNullException">Any argument is null. <para>任一参数为 null。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The offset or count is negative, or the segment leaves the
    /// effective payload. <para>偏移或长度为负，或段离开有效负载范围。</para></exception>
    /// <exception cref="EndOfStreamException">The container ends before the segment is complete.
    /// <para>容器在段完成前结束。</para></exception>
    /// <exception cref="OperationCanceledException">The token was canceled. <para>令牌被取消。</para></exception>
    public static Task<long> ExtractEntrySegmentAsync(Stream input, ImageWtyEntry entry, long offset, long count,
        Stream output, bool decrypt, CancellationToken cancellationToken = default) =>
        AllwinnerImageWtyFormat.ExtractEntrySegmentAsync(input, entry, offset, count, output, decrypt,
            cancellationToken);
}