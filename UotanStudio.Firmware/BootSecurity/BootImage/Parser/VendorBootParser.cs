using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.BootSecurity.BootImage.Components;
using UotanStudio.Firmware.BootSecurity.BootImage.Models;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Streams;

namespace UotanStudio.Firmware.BootSecurity.BootImage.Parser;

/// <summary>
/// Parses vendor boot images (magic <c>VNDRBOOT</c>) of header version 3 and 4, keeping the seekable source
/// stream so components can be materialized lazily or exposed as bounded read-only views.
/// <para>解析头部版本为 3 或 4 的 Vendor Boot 镜像（魔数 <c>VNDRBOOT</c>），并保留可寻址的源流，组件可惰性物化或以有界只读视图暴露。</para>
/// </summary>
/// <remarks>
/// All public members are guarded by an internal lock; a failed parse disposes the candidate stream and resets
/// state, and a successful parse closes and replaces the source attached by an earlier parse.
/// <para>所有公共成员均由内部锁保护；解析失败会释放候选流并重置状态，解析成功则关闭并替换更早一次解析附加的源。</para>
/// </remarks>
public class VendorBootParser : IDisposable
{
    private const string VendorBootMagic = "VNDRBOOT";
    private const int V3HeaderSize = 2112;
    private const int V4HeaderSize = 2128;
    private const int VendorCmdlineSize = 2048;
    private const int RamdiskNameSize = 32;
    private const int RamdiskBoardIdCount = 16;

    private const int RamdiskTableEntryMinSize = 108;


    private const uint MaximumComponentSize = 512 * 1024 * 1024;
    private const uint MaximumRamdiskTableEntries = 65_536;
    private const uint MaximumRamdiskTableSize = 64 * 1024 * 1024;
    private bool _disposed;
    private readonly object _sourceLock = new();


    private Stream? _sourceStream;
    private long _vendorRamdiskOffset;
    private long _dtbOffset;
    private long _bootconfigOffset;
    private byte[]? _vendorRamdisk;
    private byte[]? _dtb;
    private byte[]? _bootconfig;


    private uint _vendorRamdiskSize;
    private uint _dtbSize;
    private uint _bootconfigSize;

    /// <summary>
    /// Parsed vendor boot header fields (magic, version, sizes, addresses, command line, table metadata).
    /// <para>已解析的 Vendor Boot 头部字段（魔数、版本、大小、地址、命令行与表元数据）。</para>
    /// </summary>
    /// <value>
    /// A zero-initialized header before any successful parse; each parse replaces it entirely, including after a failed re-parse.
    /// <para>任何成功解析之前为全零默认头部；每次解析都会整体替换，解析失败回滚后同样如此。</para>
    /// </value>
    public VendorBootHeader Header { get; private set; } = new();

    /// <summary>
    /// Main vendor ramdisk bytes, read from the source stream on first access and cached afterwards.
    /// <para>主 Vendor RAMDisk 字节，首次访问时从源流读取并缓存。</para>
    /// </summary>
    /// <value>
    /// Exactly <see cref="VendorBootHeader.VendorRamdiskSize"/> bytes, or an empty array when the declared size is <c>0</c>.
    /// <para>恰为 <see cref="VendorBootHeader.VendorRamdiskSize"/> 字节；声明大小为 <c>0</c> 时返回空数组。</para>
    /// </value>
    /// <exception cref="ObjectDisposedException">The parser has been disposed. <para>解析器已被释放。</para></exception>
    /// <exception cref="ImageParseException">The declared range is truncated or exceeds the 512 MiB single-component bound.
    /// <para>声明范围被截断或超过 512 MiB 单组件上限。</para></exception>
    public byte[] VendorRamdisk
    {
        get
        {
            lock (_sourceLock)
            {
                ThrowIfDisposed();
                return _vendorRamdisk ??= ReadComponent(_vendorRamdiskOffset, _vendorRamdiskSize, "vendor ramdisk");
            }
        }
    }

    /// <summary>
    /// Embedded DTB blob, read from the source stream on first access and cached afterwards.
    /// <para>内嵌 DTB 数据块，首次访问时从源流读取并缓存。</para>
    /// </summary>
    /// <value>
    /// Exactly <see cref="VendorBootHeader.DtbSize"/> bytes, or an empty array when the declared size is <c>0</c>.
    /// <para>恰为 <see cref="VendorBootHeader.DtbSize"/> 字节；声明大小为 <c>0</c> 时返回空数组。</para>
    /// </value>
    /// <exception cref="ObjectDisposedException">The parser has been disposed. <para>解析器已被释放。</para></exception>
    /// <exception cref="ImageParseException">The declared range is truncated or exceeds the 512 MiB single-component bound.
    /// <para>声明范围被截断或超过 512 MiB 单组件上限。</para></exception>
    public byte[] Dtb
    {
        get
        {
            lock (_sourceLock)
            {
                ThrowIfDisposed();
                return _dtb ??= ReadComponent(_dtbOffset, _dtbSize, "DTB");
            }
        }
    }

    /// <summary>
    /// Bootconfig payload, read from the source stream on first access and cached afterwards.
    /// <para>bootconfig 载荷，首次访问时从源流读取并缓存。</para>
    /// </summary>
    /// <value>
    /// Exactly <see cref="VendorBootHeader.VendorBootconfigSize"/> bytes; empty for version 3 images and whenever the declared size is <c>0</c>.
    /// <para>恰为 <see cref="VendorBootHeader.VendorBootconfigSize"/> 字节；版本 3 镜像以及声明大小为 <c>0</c> 时为空。</para>
    /// </value>
    /// <exception cref="ObjectDisposedException">The parser has been disposed. <para>解析器已被释放。</para></exception>
    /// <exception cref="ImageParseException">The declared range is truncated or exceeds the 512 MiB single-component bound.
    /// <para>声明范围被截断或超过 512 MiB 单组件上限。</para></exception>
    public byte[] Bootconfig
    {
        get
        {
            lock (_sourceLock)
            {
                ThrowIfDisposed();
                return _bootconfig ??= ReadComponent(_bootconfigOffset, _bootconfigSize, "bootconfig");
            }
        }
    }

    /// <summary>
    /// Vendor ramdisk table entries decoded from the version 4 table region.
    /// <para>从版本 4 表区域解码出的 Vendor RAMDisk 表条目。</para>
    /// </summary>
    /// <value>
    /// Empty for version 3 images, when the table declares no entries, and before parsing; cleared and repopulated by every subsequent parse.
    /// <para>版本 3 镜像、表声明零条目以及解析之前均为空；每次后续解析都会清空并重新填充。</para>
    /// </value>
    public List<VendorRamdiskTableEntry> RamdiskTable { get; } = [];

    /// <summary>
    /// Opens a vendor boot image file and parses it, transferring the opened stream's ownership to the parser.
    /// <para>打开 Vendor Boot 镜像文件并解析，打开的流归解析器持有。</para>
    /// </summary>
    /// <param name="filePath">
    /// Path of an existing file opened with shared read access; the parser keeps the handle until disposal or the next parse.
    /// <para>已存在文件的路径，以共享读方式打开；解析器持有句柄直至释放或下一次解析。</para>
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="filePath"/> is <see langword="null"/> or empty. <para><paramref name="filePath"/> 为 <see langword="null"/> 或空。</para></exception>
    /// <exception cref="ObjectDisposedException">The parser has been disposed. <para>解析器已被释放。</para></exception>
    /// <exception cref="ImageParseException">
    /// The magic, version, geometry or any component/table range is invalid; the file stream is closed and state reset.
    /// <para>魔数、版本、几何信息或任一组件/表范围非法；此时文件流被关闭且状态被重置。</para>
    /// </exception>
    public void ParseFile(string filePath)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        AttachSource(stream);
    }

    /// <summary>
    /// Parses a vendor boot image from a byte array; the array is copied, so later caller mutations do not affect the parse.
    /// <para>从字节数组解析 Vendor Boot 镜像；数组会被复制，调用方后续的修改不影响解析结果。</para>
    /// </summary>
    /// <param name="data">
    /// Complete image bytes; the parser owns an in-memory copy until disposal or the next parse.
    /// <para>完整镜像字节；该内存副本由解析器持有，直至释放或下一次解析。</para>
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>. <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ObjectDisposedException">The parser has been disposed. <para>解析器已被释放。</para></exception>
    /// <exception cref="ImageParseException">
    /// The magic, version, geometry or any component/table range is invalid; state is reset.
    /// <para>魔数、版本、几何信息或任一组件/表范围非法；状态被重置。</para>
    /// </exception>
    public void Parse(byte[] data)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(data);


        var stream = new MemoryStream(data.ToArray(), writable: false);
        AttachSource(stream);
    }

    private void AttachSource(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        lock (_sourceLock)
        {
            try
            {
                ThrowIfDisposed();
                Reset();
                _sourceStream?.Dispose();
                _sourceStream = stream;
                ParseStream(stream);
            }
            catch
            {
                stream.Dispose();
                if (ReferenceEquals(_sourceStream, stream))
                {
                    _sourceStream = null;
                }


                Reset();
                throw;
            }
        }
    }

    private void ParseStream(Stream stream)
    {
        if (!stream.CanSeek || !stream.CanRead)
        {
            throw new ImageParseException("Vendor Boot 输入流必须可读且可定位");
        }

        if (stream.Length < 12)
        {
            throw new ImageParseException($"文件太小，无法读取 Vendor Boot 基本头: {stream.Length} 字节");
        }

        var prefix = ReadRange(stream, 0, 12, "Vendor Boot 基本头");
        var magic = Encoding.ASCII.GetString(prefix, 0, 8);
        if (magic != VendorBootMagic)
        {
            throw new ImageParseException($"无效的 Vendor Boot 镜像 magic: {magic}");
        }

        Header.HeaderVersion = BinaryReaderHelpers.ReadUInt32LE(prefix, 8);
        var minimumHeaderSize = Header.HeaderVersion switch
        {
            3 => V3HeaderSize,
            4 => V4HeaderSize,
            _ => throw new ImageParseException($"不支持的 Vendor Boot 镜像版本: {Header.HeaderVersion}")
        };
        var header = ReadRange(stream, 0, minimumHeaderSize, $"V{Header.HeaderVersion} Vendor Boot 头部");
        ParseHeader(header, minimumHeaderSize);
        ExtractComponents(stream);
        if (Header.HeaderVersion == 4)
        {
            ExtractRamdiskTable(stream);
            ExtractBootconfig(stream);
        }
        if (Header.VendorRamdiskSize > 0)
        {
            Header.VendorRamdiskCompression = DetectCompression(stream, _vendorRamdiskOffset, Header.VendorRamdiskSize);
        }
    }

    private void ParseHeader(byte[] data, int minimumHeaderSize)
    {
        Header.PageSize = BinaryReaderHelpers.ReadUInt32LE(data, 12);
        Header.KernelLoadAddress = BinaryReaderHelpers.ReadUInt32LE(data, 16);
        Header.RamdiskLoadAddress = BinaryReaderHelpers.ReadUInt32LE(data, 20);
        Header.VendorRamdiskSize = BinaryReaderHelpers.ReadUInt32LE(data, 24);
        Header.VendorCmdline = ReadString(data, 28, VendorCmdlineSize).TrimEnd('\0');
        Header.TagsLoadAddress = BinaryReaderHelpers.ReadUInt32LE(data, 2076);
        Header.VendorName = ReadString(data, 2080, 16).TrimEnd('\0');
        Header.VendorHeaderSize = BinaryReaderHelpers.ReadUInt32LE(data, 2096);
        Header.DtbSize = BinaryReaderHelpers.ReadUInt32LE(data, 2100);
        Header.DtbAddress = BinaryReaderHelpers.ReadUInt64LE(data, 2104);

        if (Header.PageSize == 0 || (Header.PageSize & (Header.PageSize - 1)) != 0)
        {
            throw new ImageParseException($"无效的 Vendor Boot 页大小: {Header.PageSize}");
        }
        if (Header.VendorHeaderSize < minimumHeaderSize)
        {
            throw new ImageParseException($"Vendor Boot 头部大小过小: {Header.VendorHeaderSize}");
        }

        if (Header.HeaderVersion == 4)
        {
            Header.VendorRamdiskTableSize = BinaryReaderHelpers.ReadUInt32LE(data, 2112);
            Header.VendorRamdiskTableEntryNum = BinaryReaderHelpers.ReadUInt32LE(data, 2116);
            Header.VendorRamdiskTableEntrySize = BinaryReaderHelpers.ReadUInt32LE(data, 2120);
            Header.VendorBootconfigSize = BinaryReaderHelpers.ReadUInt32LE(data, 2124);
            ValidateTableFields();
        }

        _vendorRamdiskSize = Header.VendorRamdiskSize;
        _dtbSize = Header.DtbSize;
        _bootconfigSize = Header.VendorBootconfigSize;
    }

    private void ExtractComponents(Stream stream)
    {
        var ramdiskOffset = AlignToPage(Header.VendorHeaderSize, Header.PageSize);
        var dtbOffset = AlignToPage(CheckedAdd(ramdiskOffset, Header.VendorRamdiskSize), Header.PageSize);


        ValidateComponentRange(stream, ramdiskOffset, Header.VendorRamdiskSize, "vendor ramdisk");
        ValidateComponentRange(stream, dtbOffset, Header.DtbSize, "DTB");

        _vendorRamdiskOffset = ramdiskOffset;
        _dtbOffset = dtbOffset;

        if (Header.HeaderVersion == 4)
        {
            var tableOffset = AlignToPage(CheckedAdd(dtbOffset, Header.DtbSize), Header.PageSize);
            if (tableOffset > uint.MaxValue)
            {
                throw new ImageParseException("Vendor ramdisk 表偏移超出支持范围");
            }
            Header.VendorRamdiskTableOffset = (uint)tableOffset;
        }
    }

    private void ValidateTableFields()
    {
        if (Header.VendorRamdiskTableSize > MaximumRamdiskTableSize)
        {
            throw new ImageParseException($"Vendor ramdisk 表元数据超过上限: {Header.VendorRamdiskTableSize}");
        }
        if (Header.VendorRamdiskTableEntryNum == 0)
        {


            return;
        }
        if (Header.VendorRamdiskTableEntryNum > MaximumRamdiskTableEntries)
        {
            throw new ImageParseException($"Vendor ramdisk 表条目数超过上限: {Header.VendorRamdiskTableEntryNum}");
        }
        if (Header.VendorRamdiskTableEntrySize != RamdiskTableEntryMinSize)
        {
            throw new InvalidDataException(
                $"Vendor ramdisk table entry size must equal {RamdiskTableEntryMinSize} bytes for header version 4; " +
                $"this is the fixed size of the AOSP boot_image.h vendor_ramdisk_table_entry_v4 record. Found " +
                $"{Header.VendorRamdiskTableEntrySize}. vendor ramdisk 表条目大小必须为 {RamdiskTableEntryMinSize} 字节。");
        }
        var required = checked((ulong)Header.VendorRamdiskTableEntryNum * Header.VendorRamdiskTableEntrySize);
        if (required > Header.VendorRamdiskTableSize)
        {
            throw new ImageParseException("Vendor ramdisk 表字段范围不一致");
        }
    }

    private void ExtractRamdiskTable(Stream stream)
    {


        ValidateRange(stream, Header.VendorRamdiskTableOffset, Header.VendorRamdiskTableSize, "vendor ramdisk 表");
        if (Header.VendorRamdiskTableEntryNum == 0)
        {
            return;
        }

        for (uint i = 0; i < Header.VendorRamdiskTableEntryNum; i++)
        {
            var entryOffset = CheckedAdd(Header.VendorRamdiskTableOffset,
                checked((long)i * Header.VendorRamdiskTableEntrySize));


            var data = ReadRange(stream, entryOffset, RamdiskTableEntryMinSize, $"vendor ramdisk 表条目 {i}");
            var entry = new VendorRamdiskTableEntry
            {
                RamdiskSize = BinaryReaderHelpers.ReadUInt32LE(data, 0),
                RamdiskOffset = BinaryReaderHelpers.ReadUInt32LE(data, 4),
                RamdiskType = BinaryReaderHelpers.ReadUInt32LE(data, 8),
                RamdiskName = ReadString(data, 12, RamdiskNameSize).TrimEnd('\0')
            };
            if ((ulong)entry.RamdiskOffset + entry.RamdiskSize > _vendorRamdiskSize)
            {
                throw new ImageParseException($"Vendor ramdisk 表条目 {i} 超出 ramdisk 范围");
            }
            for (var j = 0; j < RamdiskBoardIdCount; j++)
            {
                entry.RamdiskBoardId[j] = BinaryReaderHelpers.ReadUInt32LE(data, 12 + RamdiskNameSize + (j * 4));
            }
            RamdiskTable.Add(entry);
        }
    }

    private void ExtractBootconfig(Stream stream)
    {
        if (Header.VendorBootconfigSize == 0)
        {
            return;
        }

        var offset = AlignToPage(
            CheckedAdd(Header.VendorRamdiskTableOffset, Header.VendorRamdiskTableSize), Header.PageSize);
        ValidateComponentRange(stream, offset, Header.VendorBootconfigSize, "bootconfig");
        _bootconfigOffset = offset;
    }

    /// <summary>
    /// Gets the declared length in bytes of a component without materializing any data.
    /// <para>获得指定组件的声明长度（字节），不物化任何数据。</para>
    /// </summary>
    /// <param name="component">
    /// Component to measure.
    /// <para>要度量的组件。</para>
    /// </param>
    /// <returns>
    /// The declared size in bytes; <c>0</c> when the component is absent from the image (for example bootconfig on a version 3 header).
    /// <para>声明的字节大小；组件在镜像中缺失时为 <c>0</c>（例如版本 3 头部的 bootconfig）。</para>
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="component"/> is not a defined <see cref="VendorBootComponent"/> value.
    /// <para><paramref name="component"/> 不是已定义的 <see cref="VendorBootComponent"/> 值。</para>
    /// </exception>
    /// <exception cref="InvalidOperationException">No image has been parsed yet. <para>尚未解析任何镜像。</para></exception>
    /// <exception cref="ObjectDisposedException">The parser has been disposed. <para>解析器已被释放。</para></exception>
    public long GetComponentLength(VendorBootComponent component)
    {
        lock (_sourceLock)
        {
            EnsureParsed();
            return component switch
            {
                VendorBootComponent.VendorRamdisk => _vendorRamdiskSize,
                VendorBootComponent.Dtb => _dtbSize,
                VendorBootComponent.Bootconfig => _bootconfigSize,
                _ => throw new ArgumentOutOfRangeException(nameof(component))
            };
        }
    }

    /// <summary>
    /// Exposes a component as a bounded read-only stream without copying its data.
    /// <para>以有界只读流形式暴露指定组件（不复制组件数据；流的生命周期由调用方管理，底层源流由解析器持有）。</para>
    /// </summary>
    /// <param name="component">
    /// Component to expose.
    /// <para>要暴露的组件。</para>
    /// </param>
    /// <returns>
    /// A stream covering exactly the declared range; closing it leaves the source open. The view stays readable
    /// only while the parser is undisposed and no newer parse has been started.
    /// <para>恰好覆盖声明范围的流；关闭它不会关闭底层源。视图仅在解析器未释放且未开始更新的解析期间可读。</para>
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="component"/> is not a defined value.
    /// <para><paramref name="component"/> 不是已定义的值。</para>
    /// </exception>
    /// <exception cref="InvalidOperationException">No image has been parsed yet. <para>尚未解析任何镜像。</para></exception>
    /// <exception cref="ObjectDisposedException">The parser has been disposed. <para>解析器已被释放。</para></exception>
    /// <exception cref="ImageParseException">The component range does not fit within the source. <para>组件范围超出源流。</para></exception>
    public Stream ExtractComponentStream(VendorBootComponent component)
    {
        lock (_sourceLock)
        {
            EnsureParsed();
            var (offset, size, name) = component switch
            {
                VendorBootComponent.VendorRamdisk => (_vendorRamdiskOffset, _vendorRamdiskSize, "vendor ramdisk"),
                VendorBootComponent.Dtb => (_dtbOffset, _dtbSize, "DTB"),
                VendorBootComponent.Bootconfig => (_bootconfigOffset, (long)_bootconfigSize, "bootconfig"),
                _ => throw new ArgumentOutOfRangeException(nameof(component))
            };
            var source = _sourceStream!;
            ValidateRange(source, offset, size, name);
            return new Core.Streams.BoundedReadStream(source, offset, size, leaveOpen: true, syncRoot: _sourceLock);
        }
    }

    /// <summary>
    /// Copies a component exactly to the destination stream (throws EndOfStreamException on premature EOF).
    /// <para>将指定组件精确复制到目标流（提前 EOF 抛 EndOfStreamException）。</para>
    /// </summary>
    /// <param name="destination">
    /// Writable stream that receives exactly the declared component bytes; its position advances and it stays open.
    /// <para>接收恰好声明组件字节数的可写流；位置前移且保持打开。</para>
    /// </param>
    /// <param name="component">
    /// Component to copy.
    /// <para>要复制的组件。</para>
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="destination"/> is <see langword="null"/>.
    /// <para><paramref name="destination"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="component"/> is not a defined value. <para><paramref name="component"/> 不是已定义的值。</para></exception>
    /// <exception cref="InvalidOperationException">No image has been parsed yet. <para>尚未解析任何镜像。</para></exception>
    /// <exception cref="ObjectDisposedException">The parser has been disposed. <para>解析器已被释放。</para></exception>
    public void ExtractComponentToFile(Stream destination, VendorBootComponent component)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using var source = ExtractComponentStream(component);
        StreamCopy.CopyExactly(source, destination, source.Length);
    }

    /// <summary>
    /// Streams a component to a file, creating or overwriting destinationPath.
    /// <para>将指定组件流式写入文件（创建或覆盖 destinationPath）。</para>
    /// </summary>
    /// <param name="destinationPath">
    /// File path opened with <c>FileMode.Create</c>; an existing file is replaced.
    /// <para>以 <c>FileMode.Create</c> 打开的文件路径；已存在的文件会被替换。</para>
    /// </param>
    /// <param name="component">
    /// Component to write.
    /// <para>要写入的组件。</para>
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="destinationPath"/> is <see langword="null"/> or empty.
    /// <para><paramref name="destinationPath"/> 为 <see langword="null"/> 或空。</para>
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="component"/> is not a defined value. <para><paramref name="component"/> 不是已定义的值。</para></exception>
    /// <exception cref="InvalidOperationException">No image has been parsed yet. <para>尚未解析任何镜像。</para></exception>
    /// <exception cref="ObjectDisposedException">The parser has been disposed. <para>解析器已被释放。</para></exception>
    public void ExtractComponentToFile(string destinationPath, VendorBootComponent component)
    {
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        using var source = ExtractComponentStream(component);
        using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        StreamCopy.CopyExactly(source, destination, source.Length);
    }

    /// <summary>
    /// Exposes one ramdisk table entry as a bounded read-only stream (the entry offset is relative to the start of the vendor ramdisk).
    /// <para>以有界只读流形式暴露 ramdisk 表中的某个条目（偏移相对于 Vendor RAMDisk 起始处）。</para>
    /// </summary>
    /// <param name="entry">
    /// Table entry, typically taken from <see cref="RamdiskTable"/>.
    /// <para>表条目，通常取自 <see cref="RamdiskTable"/>。</para>
    /// </param>
    /// <returns>
    /// A stream covering exactly <see cref="VendorRamdiskTableEntry.RamdiskSize"/> bytes inside the vendor ramdisk region;
    /// closing it leaves the source open.
    /// <para>恰好覆盖 Vendor RAMDisk 区域内 <see cref="VendorRamdiskTableEntry.RamdiskSize"/> 字节的流；关闭它不会关闭底层源。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="entry"/> is <see langword="null"/>.
    /// <para><paramref name="entry"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="InvalidOperationException">No image has been parsed yet. <para>尚未解析任何镜像。</para></exception>
    /// <exception cref="ObjectDisposedException">The parser has been disposed. <para>解析器已被释放。</para></exception>
    /// <exception cref="ImageParseException">
    /// The entry range falls outside the vendor ramdisk, the offset overflows, or the range does not fit within the source.
    /// <para>条目范围超出 Vendor RAMDisk、偏移溢出，或范围超出源流。</para>
    /// </exception>
    public Stream ExtractRamdiskStream(VendorRamdiskTableEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_sourceLock)
        {
            EnsureParsed();
            if (entry.RamdiskOffset > _vendorRamdiskSize ||
                entry.RamdiskSize > _vendorRamdiskSize - entry.RamdiskOffset)
            {
                throw new ImageParseException("Vendor ramdisk 表条目超出 ramdisk 范围");
            }
            long offset;
            try
            {
                offset = checked(_vendorRamdiskOffset + entry.RamdiskOffset);
            }
            catch (OverflowException ex)
            {
                throw new ImageParseException("Vendor ramdisk 表条目偏移溢出", ex);
            }
            var size = (long)entry.RamdiskSize;
            var source = _sourceStream!;
            ValidateRange(source, offset, size, "vendor ramdisk 表条目");
            return new Core.Streams.BoundedReadStream(source, offset, size, leaveOpen: true, syncRoot: _sourceLock);
        }
    }

    /// <summary>
    /// Streams one ramdisk table entry to a file, creating or overwriting destinationPath.
    /// <para>将 ramdisk 表中的某个条目流式写入文件（创建或覆盖 destinationPath）。</para>
    /// </summary>
    /// <param name="destinationPath">
    /// File path opened with <c>FileMode.Create</c>; an existing file is replaced.
    /// <para>以 <c>FileMode.Create</c> 打开的文件路径；已存在的文件会被替换。</para>
    /// </param>
    /// <param name="entry">
    /// Table entry, typically taken from <see cref="RamdiskTable"/>.
    /// <para>表条目，通常取自 <see cref="RamdiskTable"/>。</para>
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="destinationPath"/> is <see langword="null"/> or empty.
    /// <para><paramref name="destinationPath"/> 为 <see langword="null"/> 或空。</para>
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="entry"/> is <see langword="null"/>. <para><paramref name="entry"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidOperationException">No image has been parsed yet. <para>尚未解析任何镜像。</para></exception>
    /// <exception cref="ObjectDisposedException">The parser has been disposed. <para>解析器已被释放。</para></exception>
    /// <exception cref="ImageParseException">The entry range falls outside the vendor ramdisk or the offset overflows. <para>条目范围超出 Vendor RAMDisk 或偏移溢出。</para></exception>
    public void ExtractRamdiskToFile(string destinationPath, VendorRamdiskTableEntry entry)
    {
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        using var source = ExtractRamdiskStream(entry);
        using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        StreamCopy.CopyExactly(source, destination, source.Length);
    }

    private byte[] ReadComponent(long offset, uint size, string component)
    {
        if (_sourceStream is null || size == 0)
        {
            return [];
        }
        return ReadRange(_sourceStream, offset, size, component);
    }

    private void EnsureParsed()
    {
        ThrowIfDisposed();
        if (_sourceStream is null)
        {
            throw new InvalidOperationException("尚未解析任何 Vendor Boot 镜像");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static CompressionType DetectCompression(Stream stream, long offset, uint size)
    {
        var length = (int)Math.Min(size, 8U);


        var prefix = new byte[length];
        stream.Position = offset;
        stream.ReadExactly(prefix);
        return BootCompressor.Detect(prefix);
    }

    private static void ValidateComponentRange(Stream stream, long offset, uint size, string component)
    {


        ValidateRange(stream, offset, size, component);
    }

    private static byte[] ReadRange(Stream stream, long offset, uint size, string component)
    {
        if (size > MaximumComponentSize)
        {
            throw new ImageParseException($"{component} 超过单组件上限 {MaximumComponentSize} 字节，无法安全读取");
        }
        return ReadRange(stream, offset, checked((int)size), component);
    }

    private static byte[] ReadRange(Stream stream, long offset, int size, string component)
    {
        ValidateRange(stream, offset, size, component);
        var position = checked(offset);
        var result = new byte[size];
        stream.Position = position;
        stream.ReadExactly(result);
        return result;
    }

    /// <summary>
    /// Routes the component bounds check through the shared kernel validator and translates the failure into
    /// <see cref="ImageParseException"/> so parse callers keep the boot-image exception type and message.
    /// <para>将组件范围校验交由共享内核校验器完成，并把失败转换为 <see cref="ImageParseException"/>，
    /// 使解析调用方保持原有的启动镜像异常类型与消息。</para>
    /// </summary>
    private static void ValidateRange(Stream stream, long offset, long size, string component)
    {
        try
        {
            Core.FileExtraction.ValidateRange(offset, size, stream.Length, component);
        }
        catch (InvalidDataException)
        {
            throw new ImageParseException($"{component} 范围超出或文件被截断");
        }
    }

    private static string ReadString(byte[] data, int offset, int length) =>
        Encoding.ASCII.GetString(data, offset, length);

    private static long CheckedAdd(long left, long right)
    {
        try { return checked(left + right); }
        catch (OverflowException ex) { throw new ImageParseException("Vendor Boot 组件偏移溢出", ex); }
    }

    private static long AlignToPage(long offset, uint pageSize)
    {
        var mask = (long)pageSize - 1;
        return CheckedAdd(offset, mask) & ~mask;
    }

    private void Reset()
    {
        Header = new VendorBootHeader();
        _vendorRamdiskOffset = 0;
        _dtbOffset = 0;
        _bootconfigOffset = 0;
        _vendorRamdisk = null;
        _dtb = null;
        _bootconfig = null;
        _vendorRamdiskSize = 0;
        _dtbSize = 0;
        _bootconfigSize = 0;
        RamdiskTable.Clear();
    }

    /// <summary>
    /// Releases the parser, closing the owned source stream and clearing cached components and the ramdisk table.
    /// <para>释放解析器：关闭其持有的源流，并清空已缓存组件与 ramdisk 表。</para>
    /// </summary>
    /// <remarks>
    /// Idempotent; once the source is closed, the bounded streams this parser handed out are not readable.
    /// <para>可重复调用；源关闭后，本解析器交出的有界流不可读取。</para>
    /// </remarks>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Core disposal logic: closes the source stream under the parser lock and resets all parsed state.
    /// <para>核心释放逻辑：在解析器锁内关闭源流并重置全部解析状态。</para>
    /// </summary>
    /// <param name="disposing">
    /// <see langword="true"/> to release managed resources; <see langword="false"/> when called from a finalizer.
    /// <para><see langword="true"/> 表示释放托管资源；由终结器调用时为 <see langword="false"/>。</para>
    /// </param>
    protected virtual void Dispose(bool disposing)
    {
        lock (_sourceLock)
        {
            if (_disposed) return;
            _disposed = true;
            if (disposing)
            {
                _sourceStream?.Dispose();
                _sourceStream = null;
                Reset();
            }
        }
    }


}

/// <summary>
/// Selectable image components of a parsed vendor boot image.
/// <para>已解析 Vendor Boot 镜像中可选择暴露的组件。</para>
/// </summary>
public enum VendorBootComponent
{
    /// <summary>The main vendor ramdisk blob. <para>主 Vendor RAMDisk 数据块。</para></summary>
    VendorRamdisk,

    /// <summary>The embedded DTB blob. <para>内嵌 DTB 数据块。</para></summary>
    Dtb,

    /// <summary>The bootconfig payload (present on version 4 headers). <para>bootconfig 载荷（仅版本 4 头部具备）。</para></summary>
    Bootconfig
}