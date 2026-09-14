using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage;

public class MtkBootParser : IDisposable
{
    private const uint MtkMagic = 0x58881688;
    private const int MtkHeaderSize = 512;
    private const int MtkNameSize = 32;
    private bool _disposed;

    public MtkHeader Header { get; private set; } = new MtkHeader();

    public byte[] Kernel { get; private set; } = [];

    public byte[] Ramdisk { get; private set; } = [];

    public byte[] Second { get; private set; } = [];

    public byte[] Dtb { get; private set; } = [];

    /// <summary>
    /// Checks if the data is in MTK format.
    /// <para>检查数据是否为 MTK 格式。</para>
    /// </summary>
    public static bool IsMtkFormat(byte[] data) => data != null && data.Length >= 8 && ReadUInt32LE(data, 0) == MtkMagic;

    /// <summary>
    /// Checks if the data is in MTK compressed format.
    /// <para>检查数据是否为 MTK 压缩格式。</para>
    /// </summary>
    public static bool IsMtkCompressed(byte[] data) => data != null && data.Length >= 4 && ReadUInt32LE(data, 0) == MtkMagic;

    /// <summary>
    /// Parses an MTK boot image from a file.
    /// <para>从文件解析 MTK Boot 镜像。</para>
    /// </summary>
    public void ParseFile(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Parse(stream);
    }

    /// <summary>
    /// Parses an MTK boot image from a stream using streaming reads.
    /// <para>从流解析 MTK Boot 镜像（流式读取，避免一次性加载整个文件）。</para>
    /// </summary>
    public void Parse(Stream stream)
    {
        if (stream.Length < MtkHeaderSize)
        {
            throw new ImageParseException($"文件太小，不是有效的 MTK Boot 镜像: {stream.Length} 字节");
        }

        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

        var magic = reader.ReadUInt32();
        if (magic != MtkMagic)
        {
            throw new ImageParseException($"无效的 MTK Boot 镜像 magic: 0x{magic:X8}");
        }

        Header = new MtkHeader
        {
            Name = ReadStringFromStream(reader, MtkNameSize).TrimEnd('\0')
        };
        Header.HeaderName = Header.Name;
        Header.Size = reader.ReadUInt32();
        Header.KernelSize = reader.ReadUInt32();
        Header.KernelOffset = reader.ReadUInt32();
        Header.RamdiskSize = reader.ReadUInt32();
        Header.RamdiskOffset = reader.ReadUInt32();
        Header.SecondSize = reader.ReadUInt32();
        Header.SecondOffset = reader.ReadUInt32();
        Header.TagsAddr = reader.ReadUInt32();
        Header.PageSize = reader.ReadUInt32();
        Header.HeaderVersion = reader.ReadUInt32();
        Header.KernelAddr = reader.ReadUInt32();
        Header.RamdiskAddr = reader.ReadUInt32();
        Header.SecondAddr = reader.ReadUInt32();
        Header.DtbSize = reader.ReadUInt32();
        Header.DtbOffset = reader.ReadUInt32();

        ExtractComponentsFromStream(stream);
    }

    /// <summary>
    /// Parses an MTK boot image from a byte array.
    /// <para>从字节数组解析 MTK Boot 镜像。</para>
    /// </summary>
    public void Parse(byte[] data)
    {
        if (data.Length < MtkHeaderSize)
        {
            throw new ImageParseException($"文件太小，不是有效的 MTK Boot 镜像: {data.Length} 字节");
        }

        var magic = ReadUInt32LE(data, 0);
        if (magic != MtkMagic)
        {
            throw new ImageParseException($"无效的 MTK Boot 镜像 magic: 0x{magic:X8}");
        }

        Header = new MtkHeader
        {
            Name = ReadString(data, 4, MtkNameSize).TrimEnd('\0')
        };
        Header.HeaderName = Header.Name;
        Header.Size = ReadUInt32LE(data, 36);
        Header.KernelSize = ReadUInt32LE(data, 40);
        Header.KernelOffset = ReadUInt32LE(data, 44);
        Header.RamdiskSize = ReadUInt32LE(data, 48);
        Header.RamdiskOffset = ReadUInt32LE(data, 52);
        Header.SecondSize = ReadUInt32LE(data, 56);
        Header.SecondOffset = ReadUInt32LE(data, 60);
        Header.TagsAddr = ReadUInt32LE(data, 64);
        Header.PageSize = ReadUInt32LE(data, 68);
        Header.HeaderVersion = ReadUInt32LE(data, 72);
        Header.KernelAddr = ReadUInt32LE(data, 76);
        Header.RamdiskAddr = ReadUInt32LE(data, 80);
        Header.SecondAddr = ReadUInt32LE(data, 84);
        Header.DtbSize = ReadUInt32LE(data, 88);
        Header.DtbOffset = ReadUInt32LE(data, 92);

        ExtractComponents(data);
    }

    /// <summary>
    /// Decompresses MTK compressed kernel data.
    /// <para>解压 MTK 压缩的内核数据。</para>
    /// </summary>
    public static byte[] DecompressKernel(byte[] data)
    {
        return data == null || data.Length < 4
            ? data ?? []
            : ReadUInt32LE(data, 0) == MtkMagic ? data : BootCompressor.DecompressOrReturn(data);
    }

    /// <summary>
    /// Detects the compression format of MTK data.
    /// <para>检测 MTK 数据的压缩格式。</para>
    /// </summary>
    public static CompressionType DetectCompression(byte[] data)
    {
        return data == null || data.Length < 4
            ? CompressionType.None
            : ReadUInt32LE(data, 0) == MtkMagic ? CompressionType.None : BootCompressor.Detect(data);
    }

    private void ExtractComponents(byte[] data)
    {
        var pageSize = Header.PageSize > 0 ? Header.PageSize : 2048;

        if (Header.KernelSize > 0 && Header.KernelOffset > 0)
        {
            var kernelOffset = AlignToPage(MtkHeaderSize + Header.KernelOffset, pageSize);
            if (kernelOffset + Header.KernelSize <= data.Length)
            {
                Kernel = new byte[Header.KernelSize];
                Array.Copy(data, kernelOffset, Kernel, 0, Header.KernelSize);
            }
        }

        if (Header.RamdiskSize > 0 && Header.RamdiskOffset > 0)
        {
            var ramdiskOffset = AlignToPage(MtkHeaderSize + Header.RamdiskOffset, pageSize);
            if (ramdiskOffset + Header.RamdiskSize <= data.Length)
            {
                Ramdisk = new byte[Header.RamdiskSize];
                Array.Copy(data, ramdiskOffset, Ramdisk, 0, Header.RamdiskSize);
            }
        }

        if (Header.SecondSize > 0 && Header.SecondOffset > 0)
        {
            var secondOffset = AlignToPage(MtkHeaderSize + Header.SecondOffset, pageSize);
            if (secondOffset + Header.SecondSize <= data.Length)
            {
                Second = new byte[Header.SecondSize];
                Array.Copy(data, secondOffset, Second, 0, Header.SecondSize);
            }
        }

        if (Header.DtbSize > 0 && Header.DtbOffset > 0)
        {
            var dtbOffset = AlignToPage(MtkHeaderSize + Header.DtbOffset, pageSize);
            if (dtbOffset + Header.DtbSize <= data.Length)
            {
                Dtb = new byte[Header.DtbSize];
                Array.Copy(data, dtbOffset, Dtb, 0, Header.DtbSize);
            }
        }
    }

    private void ExtractComponentsFromStream(Stream stream)
    {
        var pageSize = Header.PageSize > 0 ? Header.PageSize : 2048;
        var fileLength = (uint)stream.Length;

        if (Header.KernelSize > 0 && Header.KernelOffset > 0)
        {
            var kernelOffset = AlignToPage(MtkHeaderSize + Header.KernelOffset, pageSize);
            if (kernelOffset + Header.KernelSize <= fileLength)
            {
                Kernel = new byte[Header.KernelSize];
                stream.Seek(kernelOffset, SeekOrigin.Begin);
                stream.ReadExactly(Kernel);
            }
        }

        if (Header.RamdiskSize > 0 && Header.RamdiskOffset > 0)
        {
            var ramdiskOffset = AlignToPage(MtkHeaderSize + Header.RamdiskOffset, pageSize);
            if (ramdiskOffset + Header.RamdiskSize <= fileLength)
            {
                Ramdisk = new byte[Header.RamdiskSize];
                stream.Seek(ramdiskOffset, SeekOrigin.Begin);
                stream.ReadExactly(Ramdisk);
            }
        }

        if (Header.SecondSize > 0 && Header.SecondOffset > 0)
        {
            var secondOffset = AlignToPage(MtkHeaderSize + Header.SecondOffset, pageSize);
            if (secondOffset + Header.SecondSize <= fileLength)
            {
                Second = new byte[Header.SecondSize];
                stream.Seek(secondOffset, SeekOrigin.Begin);
                stream.ReadExactly(Second);
            }
        }

        if (Header.DtbSize > 0 && Header.DtbOffset > 0)
        {
            var dtbOffset = AlignToPage(MtkHeaderSize + Header.DtbOffset, pageSize);
            if (dtbOffset + Header.DtbSize <= fileLength)
            {
                Dtb = new byte[Header.DtbSize];
                stream.Seek(dtbOffset, SeekOrigin.Begin);
                stream.ReadExactly(Dtb);
            }
        }
    }

    private static uint ReadUInt32LE(byte[] data, int offset) => offset + 4 > data.Length ? 0 : BitConverter.ToUInt32(data, offset);

    private static string ReadString(byte[] data, int offset, int length)
    {
        var actualLength = Math.Min(length, data.Length - offset);
        if (actualLength <= 0)
        {
            return string.Empty;
        }

        var bytes = new byte[actualLength];
        Array.Copy(data, offset, bytes, 0, actualLength);
        return Encoding.ASCII.GetString(bytes);
    }

    private static string ReadStringFromStream(BinaryReader reader, int length)
    {
        var bytes = reader.ReadBytes(length);
        return Encoding.ASCII.GetString(bytes);
    }

    private static uint AlignToPage(uint offset, uint pageSize) => (offset + pageSize - 1) & ~(pageSize - 1);

    /// <summary>
    /// Releases all resources used by the parser.
    /// <para>释放资源。</para>
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Core method for releasing resources.
    /// <para>释放资源的核心方法。</para>
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            Kernel = [];
            Ramdisk = [];
            Second = [];
            Dtb = [];
        }
        _disposed = true;
    }
}

public class MtkHeader
{
    public string Name { get; set; } = string.Empty;

    public string HeaderName { get; set; } = string.Empty;

    public uint Size { get; set; }

    public uint KernelSize { get; set; }

    public uint KernelOffset { get; set; }

    public uint KernelAddr { get; set; }

    public uint RamdiskSize { get; set; }

    public uint RamdiskOffset { get; set; }

    public uint RamdiskAddr { get; set; }

    public uint SecondSize { get; set; }

    public uint SecondOffset { get; set; }

    public uint SecondAddr { get; set; }

    public uint TagsAddr { get; set; }

    public uint PageSize { get; set; }

    public uint HeaderVersion { get; set; }

    public uint DtbSize { get; set; }

    public uint DtbOffset { get; set; }
}