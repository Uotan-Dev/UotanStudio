using System.Text;

namespace UotanStudio.Firmware.BootImage;

public class SamsungBootParser : IDisposable
{
    private const string BootMagic = "ANDROID!";
    private const int SamsungHeaderSize = 1664;
    private bool _disposed;

    public SamsungHeader Header { get; private set; } = new SamsungHeader();

    public byte[] Kernel { get; private set; } = [];

    public byte[] Ramdisk { get; private set; } = [];

    public byte[] Second { get; private set; } = [];

    public byte[] Dtb { get; private set; } = [];

    public byte[] Extra { get; private set; } = [];

    /// <summary>
    /// Checks if the data is in Samsung PXA format.
    /// <para>检查数据是否为 Samsung PXA 格式。</para>
    /// </summary>
    public static bool IsSamsungFormat(byte[] data)
    {
        if (data == null || data.Length < 44)
        {
            return false;
        }

        var magic = Encoding.ASCII.GetString(data, 0, 8);
        if (magic != BootMagic)
        {
            return false;
        }


        var headerOrExtra = BitConverter.ToUInt32(data, 40);
        return headerOrExtra > 4;
    }

    /// <summary>
    /// Parses a Samsung PXA boot image from a file.
    /// <para>从文件解析 Samsung PXA Boot 镜像。</para>
    /// </summary>
    public void ParseFile(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var headerBufSize = (int)Math.Min(stream.Length, 1680);
        var headerBuf = new byte[headerBufSize];
        stream.ReadExactly(headerBuf);
        ParseHeader(headerBuf);
        ExtractComponentsFromStream(stream);
    }

    /// <summary>
    /// Parses a Samsung PXA boot image from a byte array.
    /// <para>从字节数组解析 Samsung PXA Boot 镜像。</para>
    /// </summary>
    public void Parse(byte[] data)
    {
        ParseHeader(data);
        ExtractComponents(data);
    }

    private void ParseHeader(byte[] data)
    {
        if (data.Length < 8)
        {
            throw new ImageParseException($"文件太小，不是有效的 Boot 镜像: {data.Length} 字节");
        }

        var magic = Encoding.ASCII.GetString(data, 0, 8);
        if (magic != BootMagic)
        {
            throw new ImageParseException($"无效的 Boot 镜像 magic: {magic}");
        }

        Header = new SamsungHeader
        {
            KernelSize = ReadUInt32LE(data, 8),
            KernelAddr = ReadUInt32LE(data, 12),
            RamdiskSize = ReadUInt32LE(data, 16),
            RamdiskAddr = ReadUInt32LE(data, 20),
            SecondSize = ReadUInt32LE(data, 24),
            SecondAddr = ReadUInt32LE(data, 28),
            TagsAddr = ReadUInt32LE(data, 32),
            PageSize = ReadUInt32LE(data, 36)
        };

        var headerOrExtra = ReadUInt32LE(data, 40);
        if (headerOrExtra == 0)
        {
            Header.HeaderVersion = 0;
            Header.HasExtra = false;
        }
        else if (headerOrExtra <= 4)
        {
            Header.HeaderVersion = headerOrExtra;
            Header.HasExtra = false;
        }
        else
        {
            Header.HeaderVersion = 0;
            Header.ExtraSize = headerOrExtra;
            Header.HasExtra = true;
        }

        Header.OsVersion = ReadUInt32LE(data, 44);
        Header.ProductName = ReadString(data, 48, 16).TrimEnd('\0');
        Header.CommandLine = ReadString(data, 64, 512).TrimEnd('\0');

        for (var i = 0; i < 8; i++)
        {
            Header.Ids[i] = ReadUInt32LE(data, 576 + (i * 4));
        }

        Header.ExtraCmdLine = ReadString(data, 608, 1024).TrimEnd('\0');

        if (Header.HasExtra && data.Length >= 1664)
        {
            Header.RecoveryDtboSize = ReadUInt32LE(data, 1632);
            Header.RecoveryDtboOffset = ReadUInt64LE(data, 1636);
        }

        if (data.Length >= 1680)
        {
            Header.KernelLoadAddr = ReadUInt64LE(data, 1652);
        }
    }

    /// <summary>
    /// Parses the extra data block in Samsung boot images.
    /// <para>解析 Samsung boot 镜像的 extra 数据块。</para>
    /// </summary>
    public byte[] ParseExtra(byte[] data)
    {
        if (!Header.HasExtra || Header.ExtraSize == 0)
        {
            return [];
        }

        if (data.Length < SamsungHeaderSize)
        {
            return [];
        }

        var extra = new byte[Header.ExtraSize];
        Array.Copy(data, SamsungHeaderSize, extra, 0, Math.Min(Header.ExtraSize, (uint)(data.Length - SamsungHeaderSize)));
        return extra;
    }

    private void ExtractComponents(byte[] data)
    {
        var pageSize = Header.PageSize > 0 ? Header.PageSize : 2048;
        var headerSize = Header.HasExtra ? Header.ExtraSize : SamsungHeaderSize;

        var kernelOffset = AlignToPage(headerSize, pageSize);
        var ramdiskOffset = AlignToPage(kernelOffset + Header.KernelSize, pageSize);
        var secondOffset = AlignToPage(ramdiskOffset + Header.RamdiskSize, pageSize);

        if (kernelOffset + Header.KernelSize <= data.Length)
        {
            Kernel = new byte[Header.KernelSize];
            Array.Copy(data, kernelOffset, Kernel, 0, Header.KernelSize);
        }

        if (Header.RamdiskSize > 0 && ramdiskOffset + Header.RamdiskSize <= data.Length)
        {
            Ramdisk = new byte[Header.RamdiskSize];
            Array.Copy(data, ramdiskOffset, Ramdisk, 0, Header.RamdiskSize);
        }

        if (Header.SecondSize > 0 && secondOffset + Header.SecondSize <= data.Length)
        {
            Second = new byte[Header.SecondSize];
            Array.Copy(data, secondOffset, Second, 0, Header.SecondSize);
        }

        if (Header.ExtraSize > 0)
        {
            var extraOffset = AlignToPage(secondOffset + Header.SecondSize, pageSize);
            if (extraOffset + Header.ExtraSize <= data.Length)
            {
                Extra = new byte[Header.ExtraSize];
                Array.Copy(data, extraOffset, Extra, 0, Header.ExtraSize);
            }
        }
    }

    private void ExtractComponentsFromStream(Stream stream)
    {
        var pageSize = Header.PageSize > 0 ? Header.PageSize : 2048;
        var headerSize = Header.HasExtra ? Header.ExtraSize : SamsungHeaderSize;

        var kernelOffset = AlignToPage(headerSize, pageSize);
        var ramdiskOffset = AlignToPage(kernelOffset + Header.KernelSize, pageSize);
        var secondOffset = AlignToPage(ramdiskOffset + Header.RamdiskSize, pageSize);

        var fileLength = stream.Length;

        if (Header.KernelSize > 0 && kernelOffset + Header.KernelSize <= fileLength)
        {
            stream.Seek(kernelOffset, SeekOrigin.Begin);
            Kernel = new byte[Header.KernelSize];
            stream.ReadExactly(Kernel);
        }

        if (Header.RamdiskSize > 0 && ramdiskOffset + Header.RamdiskSize <= fileLength)
        {
            stream.Seek(ramdiskOffset, SeekOrigin.Begin);
            Ramdisk = new byte[Header.RamdiskSize];
            stream.ReadExactly(Ramdisk);
        }

        if (Header.SecondSize > 0 && secondOffset + Header.SecondSize <= fileLength)
        {
            stream.Seek(secondOffset, SeekOrigin.Begin);
            Second = new byte[Header.SecondSize];
            stream.ReadExactly(Second);
        }

        if (Header.ExtraSize > 0)
        {
            var extraOffset = AlignToPage(secondOffset + Header.SecondSize, pageSize);
            if (extraOffset + Header.ExtraSize <= fileLength)
            {
                stream.Seek(extraOffset, SeekOrigin.Begin);
                Extra = new byte[Header.ExtraSize];
                stream.ReadExactly(Extra);
            }
        }
    }

    private static uint ReadUInt32LE(byte[] data, int offset) => offset + 4 > data.Length ? 0 : BitConverter.ToUInt32(data, offset);

    private static ulong ReadUInt64LE(byte[] data, int offset) => offset + 8 > data.Length ? 0 : BitConverter.ToUInt64(data, offset);

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
            Extra = [];
        }
        _disposed = true;
    }
}

public class SamsungHeader
{
    public uint KernelSize { get; set; }

    public uint KernelAddr { get; set; }

    public uint RamdiskSize { get; set; }

    public uint RamdiskAddr { get; set; }

    public uint SecondSize { get; set; }

    public uint SecondAddr { get; set; }

    public uint TagsAddr { get; set; }

    public uint PageSize { get; set; }

    public uint HeaderVersion { get; set; }

    public bool HasExtra { get; set; }

    public uint ExtraSize { get; set; }

    public uint ExtraOffset { get; set; }

    public uint OsVersion { get; set; }

    public string ProductName { get; set; } = string.Empty;

    public string CommandLine { get; set; } = string.Empty;

    public string ExtraCmdLine { get; set; } = string.Empty;

    public uint[] Ids { get; set; } = new uint[8];

    public uint RecoveryDtboSize { get; set; }

    public ulong RecoveryDtboOffset { get; set; }

    public ulong KernelLoadAddr { get; set; }
}
