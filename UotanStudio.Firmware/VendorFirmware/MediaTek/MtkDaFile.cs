using UotanStudio.Firmware.Core.Streams;

namespace UotanStudio.Firmware.VendorFirmware.MediaTek;

/// <summary>
/// Explicit region byte selection, without cryptographic signature validation.
/// <para>显式的区域字节选择，不包含密码学签名校验。</para>
/// </summary>
public enum MtkDaRegionContent
{
    /// <summary>All declared bytes, including any signature suffix. <para>全部声明字节，包含签名后缀（若有）。</para></summary>
    Complete,
    /// <summary>
    /// Exclude exactly the signature suffix length recorded in the directory.
    /// <para>恰好排除目录中记录的签名字节后缀长度。</para>
    /// </summary>
    WithoutSignatureSuffix
}

/// <summary>
/// Owns the file handle used for both metadata parsing and bounded region reads.
/// Region streams have independent cursors; the owner must outlive them.
/// <para>同时用于元数据解析和有界区域读取的文件句柄持有者。区域流拥有独立游标；持有者必须比它们存活更久。</para>
/// </summary>
public sealed class MtkDaFile : IDisposable
{
    private readonly FileStream _source;
    private readonly object _gate = new();
    private bool _disposed;

    private MtkDaFile(FileStream source, MtkDaContainer metadata)
    {
        _source = source;
        Metadata = metadata;
    }

    /// <summary>
    /// Gets the validated directory from the retained file handle.
    /// <para>从已保留的文件句柄获取经过校验的目录。</para>
    /// </summary>
    /// <value>
    /// The parsed container metadata, fixed for the lifetime of this owner.
    /// <para>已解析的容器元数据，在本持有者的整个生存期内保持不变。</para>
    /// </value>
    public MtkDaContainer Metadata { get; }

    /// <summary>
    /// Opens and validates a container without reading its region payloads.
    /// <para>打开并校验容器，但不读取区域载荷。</para>
    /// </summary>
    /// <param name="path">
    /// The explicitly selected loader path.
    /// <para>显式选择的加载器路径。</para>
    /// </param>
    /// <param name="layout">
    /// The expected layout, or unambiguous structural detection.
    /// <para>预期布局，或要求无歧义的结构探测。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// The metadata parsing cancellation token.
    /// <para>元数据解析的取消令牌。</para>
    /// </param>
    /// <returns>
    /// An owner whose disposal closes the retained container handle.
    /// <para>释放时会关闭所保留容器句柄的持有者。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is empty or whitespace.
    /// <para><paramref name="path"/> 为空或仅包含空白字符。</para>
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// No file exists at <paramref name="path"/>.
    /// <para><paramref name="path"/> 指向的文件不存在。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The header, entry count, or directory fails structural validation.
    /// <para>头部、条目数量或目录未通过结构校验。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Cancellation was requested before or during parsing.
    /// <para>在解析之前或期间已请求取消。</para>
    /// </exception>
    /// <example>
    /// <code>
    /// using var da = MtkDaFile.Open(@"C:\images\DA_all_in_one.bin", MtkDaDirectoryLayout.Auto);
    /// var firstRegion = da.OpenRegion(0, 0, MtkDaRegionContent.WithoutSignatureSuffix);
    /// </code>
    /// </example>
    public static MtkDaFile Open(string path, MtkDaDirectoryLayout layout = MtkDaDirectoryLayout.Auto,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.RandomAccess);
        try
        {
            return new MtkDaFile(source, MtkDaContainerReader.Read(source, layout, cancellationToken));
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens a bounded view of an explicitly selected region on the retained file.
    /// This does not validate its suitability for a device or interpret <c>StartOffset</c>.
    /// <para>在已保留的文件上打开显式选定区域的有界视图。这不校验区域对设备的适用性，也不解释 <c>StartOffset</c>。</para>
    /// </summary>
    /// <param name="entryIndex">
    /// The zero-based directory entry index.
    /// <para>从零开始的目录条目索引。</para>
    /// </param>
    /// <param name="regionIndex">
    /// The zero-based region index within that entry.
    /// <para>该条目内从零开始的区域索引。</para>
    /// </param>
    /// <param name="content">
    /// Whether the declared signature suffix is included.
    /// <para>是否包含声明的签名字节后缀。</para>
    /// </param>
    /// <returns>
    /// A caller-owned read-only stream; disposing it does not close this file owner.
    /// <para>调用方拥有的只读流；释放它不会关闭本文件持有者。</para>
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// An index is out of range, or <paramref name="content"/> is not a defined value.
    /// <para>索引超出范围，或 <paramref name="content"/> 不是已定义的值。</para>
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// This owner has already been disposed.
    /// <para>本持有者已被释放。</para>
    /// </exception>
    public Stream OpenRegion(int entryIndex, int regionIndex, MtkDaRegionContent content)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentOutOfRangeException.ThrowIfNegative(entryIndex);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(entryIndex, Metadata.Entries.Count);
            var entry = Metadata.Entries[entryIndex];
            ArgumentOutOfRangeException.ThrowIfNegative(regionIndex);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(regionIndex, entry.Regions.Count);
            if (!Enum.IsDefined(content))
            {
                throw new ArgumentOutOfRangeException(nameof(content));
            }
            var region = entry.Regions[regionIndex];
            var length = content == MtkDaRegionContent.Complete
                ? region.Length : region.Length - region.SignatureLength;
            var cursor = new IndependentReadStream(_source, _gate);
            try
            {
                return new BoundedReadStream(cursor, region.FileOffset, length);
            }
            catch
            {
                cursor.Dispose();
                throw;
            }
        }
    }

    /// <summary>
    /// Closes the retained handle; outstanding region streams cannot be used after this returns.
    /// <para>关闭所保留的句柄；已打开的区域流在此之后不得使用。</para>
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _source.Dispose();
        }
    }
}