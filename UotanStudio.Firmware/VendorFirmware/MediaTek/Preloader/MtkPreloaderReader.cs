namespace UotanStudio.Firmware.VendorFirmware.MediaTek.Preloader;

/// <summary>
/// Bounded reader for a MediaTek preloader boot region: identifies the declared boot storage from the leading
/// magic, reads the code offset from the <c>0x20D/0x21D/0x211/0x212/0x221/0x222</c> bytes, and locates the
/// <c>AND_ROMINFO_v</c> flag block without reading the region into memory.
/// <para>MediaTek 预加载器引导区的有界读取器：按前导魔数识别声明的引导存储，从 <c>0x20D/0x21D/0x211/0x212/0x221/0x222</c>
/// 读取代码偏移，并在不把区域载入内存的前提下定位 <c>AND_ROMINFO_v</c> 标志块。</para>
/// </summary>
public static class MtkPreloaderReader
{
    private const int MagicProbeLength = 0x10;

    private const int ScanBlockSize = 64 * 1024;

    /// <summary>
    /// Determines whether the header bytes start a supported preloader boot region.
    /// <para>判断头部字节是否启动受支持的预加载器引导区。</para>
    /// </summary>
    /// <param name="header">At least eight leading bytes.<para>至少八个前导字节。</para></param>
    /// <returns><see langword="true"/> for a boot-region or raw magic.<para>引导区或裸镜像魔数时为 <see langword="true"/>。</para></returns>
    public static bool IsPreloader(ReadOnlySpan<byte> header) =>
        header.StartsWith(MtkPreloaderConstants.EmmcBootMagic)
        || header.StartsWith(MtkPreloaderConstants.UfsBootMagic)
        || header.StartsWith(MtkPreloaderConstants.ComboBootMagic)
        || header.StartsWith(MtkPreloaderConstants.RawMagic);

    /// <summary>
    /// Parses a preloader boot region from disk.
    /// <para>从磁盘解析预加载器引导区。</para>
    /// </summary>
    /// <param name="path">Region path.<para>区域路径。</para></param>
    /// <param name="cancellationToken">Cancels the scan.<para>取消本次扫描。</para></param>
    /// <returns>The parsed region.<para>已解析的区域。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is blank.<para><paramref name="path"/> 为空白。</para></exception>
    /// <exception cref="InvalidDataException">The region is shorter than the header window, the magic is unknown, or the flag block is truncated.<para>区域短于头部窗口、魔数未知，或标志块被截断。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.<para>已请求取消。</para></exception>
    public static async Task<MtkPreloaderImage> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, ScanBlockSize, useAsync: true);
        return await ReadAsync(stream, path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Parses a preloader boot region from a caller-owned seekable stream whose position is restored afterwards.
    /// <para>从调用方持有的可寻址流解析预加载器引导区，结束后还原流位置。</para>
    /// </summary>
    /// <param name="source">Readable, seekable stream.<para>可读、可寻址的流。</para></param>
    /// <param name="path">Display path used in the result.<para>结果中使用的展示路径。</para></param>
    /// <param name="cancellationToken">Cancels the scan.<para>取消本次扫描。</para></param>
    /// <returns>The parsed region.<para>已解析的区域。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.<para><paramref name="source"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="source"/> cannot be read or seeked.<para><paramref name="source"/> 不可读或不可寻址。</para></exception>
    /// <exception cref="InvalidDataException">The region layout is unsupported.<para>区域布局不受支持。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.<para>已请求取消。</para></exception>
    public static async Task<MtkPreloaderImage> ReadAsync(Stream source, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek)
        {
            throw new ArgumentException("The preloader source must be readable and seekable.", nameof(source));
        }

        var origin = source.Position;
        try
        {
            var length = source.Length;
            if (length > MtkPreloaderConstants.MaximumRegionBytes)
            {
                throw new InvalidDataException("The preloader region exceeds the supported size limit.");
            }

            if (length < MtkPreloaderConstants.CodeOffsetLow4 + 1)
            {
                throw new InvalidDataException("The preloader region is shorter than its offset header.");
            }

            var header = new byte[MagicProbeLength];
            source.Position = 0;
            var read = source.Read(header, 0, header.Length);
            var storage = ReadStorage(header.AsSpan(0, read));
            var codeOffset = ReadCodeOffset(source, cancellationToken);
            var flagBlock = storage == MtkPreloaderStorage.Raw
                ? null
                : await FindFlagBlockAsync(source, length, cancellationToken).ConfigureAwait(false);
            return new MtkPreloaderImage(path, length, storage, codeOffset, flagBlock);
        }
        finally
        {
            source.Position = origin;
        }
    }

    private static MtkPreloaderStorage ReadStorage(ReadOnlySpan<byte> header)
    {
        if (header.StartsWith(MtkPreloaderConstants.EmmcBootMagic))
        {
            return MtkPreloaderStorage.EmmcBoot;
        }

        if (header.StartsWith(MtkPreloaderConstants.UfsBootMagic))
        {
            return MtkPreloaderStorage.UfsBoot;
        }

        if (header.StartsWith(MtkPreloaderConstants.ComboBootMagic))
        {
            return MtkPreloaderStorage.ComboBoot;
        }

        if (header.StartsWith(MtkPreloaderConstants.RawMagic))
        {
            return MtkPreloaderStorage.Raw;
        }

        return MtkPreloaderStorage.Unknown;
    }

    private static long ReadCodeOffset(Stream source, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var window = new byte[MtkPreloaderConstants.CodeOffsetLow4 - MtkPreloaderConstants.CodeOffsetByte + 1];
        source.Position = MtkPreloaderConstants.CodeOffsetByte;
        source.ReadExactly(window);
        return (long)window[0] * 256;
    }

    private static async Task<MtkPreloaderFlagBlock?> FindFlagBlockAsync(
        Stream source,
        long length,
        CancellationToken cancellationToken)
    {
        if (length > int.MaxValue)
        {
            throw new InvalidDataException("The preloader region is too large to scan.");
        }

        var buffer = new byte[(int)length];
        source.Position = 0;
        await source.ReadExactlyAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
        var magic = MtkPreloaderConstants.FlagMagic;
        var index = new ReadOnlySpan<byte>(buffer).IndexOf(magic);
        if (index < 0)
        {
            return null;
        }

        if (index + MtkPreloaderConstants.FlagBlockLength > length)
        {
            throw new InvalidDataException("The preloader flag block is truncated.");
        }

        var state = buffer[index + MtkPreloaderConstants.LockStateOffsetInBlock];
        return new MtkPreloaderFlagBlock(index, MtkPreloaderConstants.FlagBlockLength, state);
    }

    internal static void ValidatePatchable(MtkPreloaderImage image)
    {
        if (image.Storage == MtkPreloaderStorage.Raw)
        {
            throw new InvalidDataException("A raw MMM preloader carries no boot-region offset header and cannot be patched.");
        }

        if (image.Storage == MtkPreloaderStorage.Unknown)
        {
            throw new InvalidDataException("The preloader boot-storage magic is unknown.");
        }

        if (image.FlagBlock is null)
        {
            throw new InvalidDataException("The AND_ROMINFO_v flag block was not found.");
        }

        if (image.CodeOffset >= MtkPreloaderConstants.CodeTarget)
        {
            throw new InvalidDataException("The declared code offset already starts at or beyond 0x2000.");
        }

        if (image.FileSize <= image.CodeOffset + MtkPreloaderConstants.TrailerReserve)
        {
            throw new InvalidDataException("The preloader region is too small for the code move.");
        }
    }
}