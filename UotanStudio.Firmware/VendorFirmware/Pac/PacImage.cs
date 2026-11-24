using UotanStudio.Firmware.Core.Streams;

namespace UotanStudio.Firmware.VendorFirmware.Pac;

/// <summary>
/// Read-only streaming view over one SPRD/UNISOC PAC package: the declared entries can be enumerated and each
/// entry's payload can be read in place through a bounded slice of the package file, so consumers (such as the
/// UNISOC flash protocol) never materialize the whole image or stage temporary copies of a loader.
/// <para>SPRD/展锐 PAC 包的只读流式视图：可以枚举声明的条目，并经由包文件的有界切片原位读取每个条目的负载，
/// 因此消费方（如紫光刷机协议）无需物化整包，也不会把加载器临时落地。</para>
/// </summary>
/// <remarks>
/// The package header, directory table and entry ranges are treated as untrusted input: the magic, directory
/// offset, entry count, struct sizes and every offset/length pair are validated before any payload stream is
/// produced, mirroring <see cref="PacExtractor"/>.
/// <para>包头、目录表与条目范围都按不可信输入处理：魔数、目录偏移、条目数、结构大小与每个偏移/长度对都在
/// 产出任何负载流之前完成校验，与 <see cref="PacExtractor"/> 的规则一致。</para>
/// </remarks>
public sealed class PacImage : IDisposable
{
    private readonly FileStream _file;
    private readonly object _syncRoot = new();
    private bool _disposed;

    private PacImage(FileStream file, string productName, IReadOnlyList<PacEntry> entries)
    {
        _file = file;
        ProductName = productName;
        Entries = entries;
    }

    /// <summary>Gets the product name recorded in the package header, or an empty string when every code unit is NUL;
    /// this is the value the official tool matches against <c>BMAConfig</c> <c>ProductList</c> entries.
    /// <para>获取包头记录的产品名；全部码元为 NUL 时为空字符串。官方工具用它匹配 <c>BMAConfig</c> 的
    /// <c>ProductList</c> 条目。</para></summary>
    public string ProductName { get; }

    /// <summary>Gets the package entries in directory-table order.
    /// <para>按目录表顺序获取包条目。</para></summary>
    /// <value>One immutable entry per declared directory row.
    /// <para>每个声明的目录行对应一个不可变条目。</para></value>
    public IReadOnlyList<PacEntry> Entries { get; }

    /// <summary>
    /// Verifies the package header and data CRC against the values stored in the PAC image. The scan is streamed
    /// with bounded buffers and rejects a package whose declared size falls outside the physical file.
    /// <para>校验 PAC 镜像的头部与数据 CRC 是否与存储值一致。扫描使用有界缓冲流式进行，声明大小落在物理文件之外的包会被拒绝。</para>
    /// </summary>
    /// <param name="path">Path of the PAC image. <para>PAC 镜像路径。</para></param>
    /// <param name="cancellationToken">Cancels the scan. <para>取消扫描。</para></param>
    /// <exception cref="InvalidDataException">The header or data CRC does not match the stored value.
    /// <para>头部或数据 CRC 与存储值不一致。</para></exception>
    public static void ValidateCrc(string path, CancellationToken cancellationToken = default)
    {
        var (headOk, dataOk) = PacExtractor.Check(path, cancellationToken);
        if (!headOk)
        {
            throw new InvalidDataException("The PAC header CRC does not match the stored value.");
        }

        if (!dataOk)
        {
            throw new InvalidDataException("The PAC data CRC does not match the stored value.");
        }
    }

    /// <summary>Opens a PAC package and validates its header and directory table.
    /// <para>打开 PAC 包并校验其头部与目录表。</para></summary>
    /// <param name="path">Path of the PAC image; must be an existing readable file.
    /// <para>PAC 镜像路径；必须是存在且可读的文件。</para></param>
    /// <returns>The opened package view; dispose it to close the underlying file.
    /// <para>已打开的包视图；释放它以关闭底层文件。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or empty.
    /// <para><paramref name="path"/> 为 <see langword="null"/> 或空。</para></exception>
    /// <exception cref="FileNotFoundException">The image file does not exist.
    /// <para>镜像文件不存在。</para></exception>
    /// <exception cref="InvalidDataException">The header magic, directory offset, entry count or an entry struct size fails validation, or an entry range falls outside the image.
    /// <para>头部魔数、目录偏移、条目数或条目结构大小未通过校验，或条目范围超出镜像。</para></exception>
    public static PacImage Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var head = SprdHead.Read(file);
            var fileLength = file.Length;
            ValidateHeader(head, fileLength);
            var entries = new List<PacEntry>((int)head.FileCount);
            for (var i = 0; i < head.FileCount; i++)
            {
                var fileRecord = SprdFile.Read(file);
                if (fileRecord.StructSize != SprdFile.StructFixedSize)
                {
                    throw new InvalidDataException("unexpected struct size");
                }

                var length = ((long)fileRecord.SizeHigh << 32) | fileRecord.Size;
                var offset = ((long)fileRecord.OffsetHigh << 32) | fileRecord.PacOffset;
                if (offset < 0 || length < 0 || offset > fileLength || length > fileLength - offset)
                {
                    throw new InvalidDataException("entry range falls outside the image");
                }

                entries.Add(new PacEntry(
                    fileRecord.GetId(),
                    fileRecord.GetName(),
                    (PacFileType)fileRecord.Type,
                    length,
                    offset,
                    fileRecord.AddrNum,
                    [.. fileRecord.Addr]));
            }

            return new PacImage(file, head.GetFwName(), entries);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens the bounded payload slice of one entry. Streams over the same package share one file position, so
    /// concurrent reads from different entries must each hold their own stream and callers must not share them
    /// across threads.
    /// <para>打开某个条目的有界负载切片。同一个包上的流共享一个文件位置，因此并发读取不同条目必须各自持有
    /// 自己的流，调用方不得跨线程共享。</para>
    /// </summary>
    /// <param name="entry">An entry of this package. <para>本包中的条目。</para></param>
    /// <returns>A read-only stream positioned at the entry start, ending at the entry end.
    /// <para>定位在条目起点、止于条目末尾的只读流。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is <see langword="null"/>.
    /// <para><paramref name="entry"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException">The entry does not belong to this package or declares an empty range.
    /// <para>条目不属于本包，或其范围为空。</para></exception>
    /// <exception cref="ObjectDisposedException">The package has been disposed.
    /// <para>包已被释放。</para></exception>
    public Stream OpenEntryStream(PacEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Entries.Contains(entry))
        {
            throw new ArgumentException("The entry does not belong to this package.", nameof(entry));
        }

        lock (_syncRoot)
        {
            _file.Position = entry.Offset;
            return new BoundedReadStream(_file, entry.Length, leaveOpen: true, _syncRoot);
        }
    }

    /// <summary>Releases the underlying package file. Entry streams opened earlier keep their own windows.
    /// <para>释放底层包文件。先前打开的条目流仍保留各自的窗口。</para></summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _file.Dispose();
            _disposed = true;
        }
    }

    private static void ValidateHeader(SprdHead head, long fileLength)
    {
        if (head.PacMagic != PacExtractor.ExpectedMagic)
        {
            throw new InvalidDataException("Bad pac_magic!");
        }
        if (head.DirOffset != SprdHead.Size)
        {
            throw new InvalidDataException("unexpected directory offset");
        }
        if ((head.FileCount >> 10) != 0)
        {
            throw new InvalidDataException("too many files");
        }
        if (head.FileCount * SprdFile.StructFixedSize > (ulong)(fileLength - SprdHead.Size))
        {
            throw new InvalidDataException("directory table exceeds the image");
        }
    }
}

/// <summary>
/// One immutable PAC directory entry: identifiers, payload range, category word and the recorded target addresses.
/// <para>不可变的 PAC 目录条目：标识、负载范围、类别字与记录的目标地址。</para>
/// </summary>
/// <param name="id">Entry identifier string, such as <c>FDL</c>, <c>FDL2</c>, <c>XML</c> or a partition name.
/// <para>条目标识字符串，如 <c>FDL</c>、<c>FDL2</c>、<c>XML</c> 或分区名。</para></param>
/// <param name="name">Entry file name string. <para>条目文件名字符串。</para></param>
/// <param name="type">Raw category word of the entry. <para>条目的原始类别字。</para></param>
/// <param name="length">Declared payload length in bytes. <para>声明的负载长度（字节）。</para></param>
/// <param name="offset">Absolute payload offset within the package. <para>负载在包内的绝对偏移。</para></param>
/// <param name="addressCount">Number of meaningful entries in <paramref name="addresses"/>.
/// <para><paramref name="addresses"/> 中有效地址的个数。</para></param>
/// <param name="addresses">The up-to-five recorded target addresses. <para>记录的至多五个目标地址。</para></param>
public sealed class PacEntry(
    string id,
    string name,
    PacFileType type,
    long length,
    long offset,
    uint addressCount,
    uint[] addresses)
{
    /// <summary>Gets the entry identifier string. <para>获取条目标识字符串。</para></summary>
    public string Id { get; } = id;

    /// <summary>Gets the entry file name string. <para>获取条目文件名字符串。</para></summary>
    public string Name { get; } = name;

    /// <summary>Gets the raw category word of the entry. <para>获取条目的原始类别字。</para></summary>
    public PacFileType Type { get; } = type;

    /// <summary>Gets the declared payload length in bytes. <para>获取声明的负载长度（字节）。</para></summary>
    public long Length { get; } = length;

    /// <summary>Gets the absolute payload offset within the package. <para>获取负载在包内的绝对偏移。</para></summary>
    public long Offset { get; } = offset;

    /// <summary>Gets the number of meaningful recorded target addresses. <para>获取有效记录目标地址的个数。</para></summary>
    public uint AddressCount { get; } = addressCount;

    /// <summary>Gets the recorded target addresses. <para>获取记录的目标地址。</para></summary>
    public IReadOnlyList<uint> Addresses { get; } = addresses;
}