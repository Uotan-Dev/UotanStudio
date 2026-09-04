using System.Security.Cryptography;
using System.Text;

namespace UotanStudio.Firmware.BootImage;

/// <summary>
/// Android Boot 镜像解析器。
/// <para>
/// 负责解析、提取和验证 Android 启动镜像文件，支持头部版本 V0 至 V4。</para>
/// <para>
/// Parses, extracts and verifies Android boot image files, supporting header versions V0 to V4.</para>
/// </summary>
public class BootImageParser : IDisposable
{
    private const string BootMagic = "ANDROID!";
    private bool _disposed;

    /// <summary>
    /// 解析后的头部信息。
    /// <para>The parsed header information.</para>
    /// </summary>
    public BootImageHeader Header { get; private set; } = new BootImageHeader();

    /// <summary>
    /// 获取内核数据。
    /// <para>Gets the kernel data.</para>
    /// </summary>
    public byte[] Kernel { get; private set; } = [];

    /// <summary>
    /// 获取 RAMDisk 数据。
    /// <para>Gets the ramdisk data.</para>
    /// </summary>
    public byte[] Ramdisk { get; private set; } = [];

    /// <summary>
    /// 获取第二阶段引导程序数据。
    /// <para>Gets the second stage loader data.</para>
    /// </summary>
    public byte[] Second { get; private set; } = [];

    /// <summary>
    /// 获取 DTB 数据。
    /// <para>Gets the DTB data.</para>
    /// </summary>
    public byte[] Dtb { get; private set; } = [];

    /// <summary>
    /// 获取额外数据。
    /// <para>Gets the extra data.</para>
    /// </summary>
    public byte[] Extra { get; private set; } = [];

    /// <summary>
    /// 获取 Recovery DTBO 数据。
    /// <para>Gets the recovery DTBO data.</para>
    /// </summary>
    public byte[] RecoveryDtbo { get; private set; } = [];

    /// <summary>
    /// 获取 V4 签名数据。
    /// <para>Gets the V4 signature data.</para>
    /// </summary>
    public byte[] Signature { get; private set; } = [];

    /// <summary>
    /// 从文件解析 Boot 镜像。
    /// <para>Parses a boot image from a file.</para>
    /// <para>流式解析：先读 header 区段判定版本，再按偏移分段读取各组件，避免大镜像全量驻留内存。</para>
    /// <para>Streaming parse: reads the header slice first, then reads each component by offset from the file stream.</para>
    /// </summary>
    /// <param name="filePath">镜像文件路径。<para>The image file path.</para></param>
    /// <exception cref="ImageParseException">镜像格式无效时抛出。<para>Thrown when the image format is invalid.</para></exception>
    public void ParseFile(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 65536, FileOptions.SequentialScan);

        // header 最长 1664 字节（V2）/ 1584 字节（V4），读 2 KiB 足以解析所有版本 header 字段
        var headerBuf = new byte[2048];
        var read = fs.Read(headerBuf, 0, headerBuf.Length);
        if (read < 8)
        {
            throw new ImageParseException($"文件太小，不是有效的 Boot 镜像: {read} 字节");
        }

        var magic = Encoding.ASCII.GetString(headerBuf, 0, 8);
        if (magic != BootMagic)
        {
            throw new ImageParseException($"无效的 Boot 镜像 magic: {magic}");
        }

        Header = new BootImageHeader();
        if (read < 44)
        {
            throw new ImageParseException($"文件太小，无法读取头部版本字段: {read} 字节");
        }

        var headerVersionField = ReadUInt32LE(headerBuf, 40);
        if (headerVersionField <= 2)
        {
            ParseV0V2(headerBuf, headerVersionField, fs);
        }
        else if (headerVersionField == 3)
        {
            ParseV3(headerBuf, fs);
        }
        else if (headerVersionField == 4)
        {
            ParseV4(headerBuf, fs);
        }
        else
        {
            ParseV0V2(headerBuf, 0, fs);
        }

        DetectCompression();
    }

    /// <summary>
    /// 从字节数组解析 Boot 镜像。
    /// <para>Parses a boot image from a byte array.</para>
    /// </summary>
    /// <param name="data">镜像数据。<para>The image data.</para></param>
    /// <exception cref="ImageParseException">镜像格式无效时抛出。<para>Thrown when the image format is invalid.</para></exception>
    public void Parse(byte[] data)
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

        Header = new BootImageHeader();
        if (data.Length < 44)
        {
            throw new ImageParseException($"文件太小，无法读取头部版本字段: {data.Length} 字节");
        }

        var headerVersionField = ReadUInt32LE(data, 40);
        if (headerVersionField <= 2)
        {
            ParseV0V2(data, headerVersionField);
        }
        else if (headerVersionField == 3)
        {
            ParseV3(data);
        }
        else if (headerVersionField == 4)
        {
            ParseV4(data);
        }
        else
        {
            ParseV0V2(data, 0);
        }

        DetectCompression();
    }

    private void DetectCompression()
    {
        if (Kernel.Length > 0)
        {
            Header.KernelCompression = BootCompressor.Detect(Kernel);
        }

        if (Ramdisk.Length > 0)
        {
            Header.RamdiskCompression = BootCompressor.Detect(Ramdisk);
        }
    }

    private void ParseV0V2(byte[] data, uint headerVersion)
    {
        Header.HeaderVersion = headerVersion switch
        {
            0 => BootImageVersion.V0,
            1 => BootImageVersion.V1,
            2 => BootImageVersion.V2,
            _ => BootImageVersion.V0
        };

        Header.KernelSize = ReadUInt32LE(data, 8);
        Header.KernelAddr = ReadUInt32LE(data, 12);
        Header.RamdiskSize = ReadUInt32LE(data, 16);
        Header.RamdiskAddr = ReadUInt32LE(data, 20);
        Header.SecondSize = ReadUInt32LE(data, 24);
        Header.SecondAddr = ReadUInt32LE(data, 28);
        Header.TagsAddr = ReadUInt32LE(data, 32);
        Header.PageSize = ReadUInt32LE(data, 36);
        Header.OsVersion = ReadUInt32LE(data, 44);
        Header.ImageName = ReadString(data, 48, 16).TrimEnd('\0');
        Header.CommandLine = (ReadString(data, 64, 512) + ReadString(data, 608, 1024)).TrimEnd('\0');
        Header.ProductName = Header.ImageName;

        for (var i = 0; i < 8; i++)
        {
            Header.Ids[i] = ReadUInt32LE(data, 576 + (i * 4));
        }

        if (headerVersion >= 1 && data.Length >= 1644)
        {
            Header.RecoveryDtboSize = ReadUInt32LE(data, 1632);
            Header.RecoveryDtboOffset = ReadUInt64LE(data, 1636);
        }

        if (headerVersion >= 2 && data.Length >= 1660)
        {
            Header.DtbSize = ReadUInt32LE(data, 1648);
            Header.KernelLoadAddr = ReadUInt64LE(data, 1652);
        }

        Header.PageSize = Header.PageSize > 0 ? Header.PageSize : 2048;
        Header.HeaderSize = headerVersion switch
        {
            0 => 1632,
            1 => 1648,
            2 => 1664,
            _ => 1632
        };

        ExtractComponentsV0V2(data);
    }

    private void ParseV0V2(byte[] headerBuf, uint headerVersion, FileStream fs)
    {
        // 复用 byte[] 重载解析 header 字段（headerBuf 长 2048，足解各版本 header）
        ParseV0V2(headerBuf, headerVersion);
        // 组件从 fs 分段读取
        ExtractComponentsV0V2Stream(fs);
    }

    private void ParseV3(byte[] data)
    {
        Header.HeaderVersion = BootImageVersion.V3;
        Header.KernelSize = ReadUInt32LE(data, 8);
        Header.RamdiskSize = ReadUInt32LE(data, 12);
        Header.OsVersion = ReadUInt32LE(data, 16);
        Header.HeaderSize = ReadUInt32LE(data, 20);
        Header.CommandLine = ReadString(data, 44, 1536).TrimEnd('\0');
        Header.PageSize = 4096;
        Header.SecondSize = 0;
        Header.DtbSize = 0;
        Header.RecoveryDtboSize = 0;

        ExtractComponentsV3(data);
    }

    private void ParseV3(byte[] headerBuf, FileStream fs)
    {
        ParseV3(headerBuf);
        ExtractComponentsV3Stream(fs);
    }

    private void ParseV4(byte[] data)
    {
        Header.HeaderVersion = BootImageVersion.V4;
        Header.KernelSize = ReadUInt32LE(data, 8);
        Header.RamdiskSize = ReadUInt32LE(data, 12);
        Header.OsVersion = ReadUInt32LE(data, 16);
        Header.HeaderSize = ReadUInt32LE(data, 20);
        Header.CommandLine = ReadString(data, 44, 1536).TrimEnd('\0');
        Header.PageSize = 4096;
        Header.SecondSize = 0;
        Header.DtbSize = 0;
        Header.RecoveryDtboSize = 0;

        if (data.Length >= 1584)
        {
            Header.SignatureSize = ReadUInt32LE(data, 1580);
        }

        ExtractComponentsV3(data);

        if (Header.SignatureSize > 0)
        {
            var pageSize = Header.PageSize;
            var kernelOffset = AlignToPage(Header.HeaderSize, pageSize);
            var ramdiskOffset = AlignToPage(kernelOffset + Header.KernelSize, pageSize);
            var signatureOffset = AlignToPage(ramdiskOffset + Header.RamdiskSize, pageSize);

            if (signatureOffset + Header.SignatureSize <= data.Length)
            {
                Signature = new byte[Header.SignatureSize];
                Array.Copy(data, signatureOffset, Signature, 0, Header.SignatureSize);
            }
        }
    }

    private void ParseV4(byte[] headerBuf, FileStream fs)
    {
        ParseV4(headerBuf);
        // ParseV4(byte[]) 已用 headerBuf 解析 header，组件需从 fs 分段读取以支持大镜像
        Kernel = [];
        Ramdisk = [];
        Signature = [];
        ExtractComponentsV3Stream(fs);

        if (Header.SignatureSize > 0)
        {
            var pageSize = Header.PageSize;
            var kernelOffset = AlignToPage(Header.HeaderSize, pageSize);
            var ramdiskOffset = AlignToPage(kernelOffset + Header.KernelSize, pageSize);
            var signatureOffset = AlignToPage(ramdiskOffset + Header.RamdiskSize, pageSize);

            Signature = ReadSegment(fs, signatureOffset, Header.SignatureSize);
        }
    }

    private void ExtractComponentsV0V2(byte[] data)
    {
        var pageSize = Header.PageSize;
        var kernelOffset = AlignToPage(Header.HeaderSize, pageSize);
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

        if (Header.DtbSize > 0)
        {
            var dtbOffset = AlignToPage(secondOffset + Header.SecondSize, pageSize);
            if (dtbOffset + Header.DtbSize <= data.Length)
            {
                Dtb = new byte[Header.DtbSize];
                Array.Copy(data, dtbOffset, Dtb, 0, Header.DtbSize);
            }
        }

        if (Header.RecoveryDtboSize > 0)
        {
            var dtboOffset = (uint)Header.RecoveryDtboOffset;
            if (dtboOffset > 0 && dtboOffset + Header.RecoveryDtboSize <= data.Length)
            {
                RecoveryDtbo = new byte[Header.RecoveryDtboSize];
                Array.Copy(data, dtboOffset, RecoveryDtbo, 0, Header.RecoveryDtboSize);
            }
        }
    }

    private void ExtractComponentsV3(byte[] data)
    {
        var pageSize = Header.PageSize;
        var kernelOffset = AlignToPage(Header.HeaderSize, pageSize);
        var ramdiskOffset = AlignToPage(kernelOffset + Header.KernelSize, pageSize);

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
    }

    /// <summary>
    /// 流式提取 V0/V2 组件：从 fs 按偏移分段读取 kernel/ramdisk/second/dtb/dtbo。
    /// <para>Streaming V0/V2 component extraction by offset from fs.</para>
    /// </summary>
    private void ExtractComponentsV0V2Stream(FileStream fs)
    {
        var pageSize = Header.PageSize;
        var kernelOffset = AlignToPage(Header.HeaderSize, pageSize);
        var ramdiskOffset = AlignToPage(kernelOffset + Header.KernelSize, pageSize);
        var secondOffset = AlignToPage(ramdiskOffset + Header.RamdiskSize, pageSize);

        Kernel = ReadSegment(fs, kernelOffset, Header.KernelSize);
        Ramdisk = Header.RamdiskSize > 0 ? ReadSegment(fs, ramdiskOffset, Header.RamdiskSize) : [];
        Second = Header.SecondSize > 0 ? ReadSegment(fs, secondOffset, Header.SecondSize) : [];

        if (Header.DtbSize > 0)
        {
            var dtbOffset = AlignToPage(secondOffset + Header.SecondSize, pageSize);
            Dtb = ReadSegment(fs, dtbOffset, Header.DtbSize);
        }

        if (Header.RecoveryDtboSize > 0)
        {
            var dtboOffset = (uint)Header.RecoveryDtboOffset;
            if (dtboOffset > 0)
            {
                RecoveryDtbo = ReadSegment(fs, dtboOffset, Header.RecoveryDtboSize);
            }
        }
    }

    /// <summary>
    /// 流式提取 V3/V4 组件：kernel/ramdisk 从 fs 分段读。
    /// <para>Streaming V3/V4 component extraction by offset from fs.</para>
    /// </summary>
    private void ExtractComponentsV3Stream(FileStream fs)
    {
        var pageSize = Header.PageSize;
        var kernelOffset = AlignToPage(Header.HeaderSize, pageSize);
        var ramdiskOffset = AlignToPage(kernelOffset + Header.KernelSize, pageSize);

        Kernel = ReadSegment(fs, kernelOffset, Header.KernelSize);
        Ramdisk = Header.RamdiskSize > 0 ? ReadSegment(fs, ramdiskOffset, Header.RamdiskSize) : [];
    }

    /// <summary>
    /// 从 FileStream 指定偏移读取指定长度区段（Seek + 单次 Read，不留全量驻留）。
    /// <para>Reads a length-bounded segment from fs at the given offset without retaining the full image.</para>
    /// </summary>
    private static byte[] ReadSegment(FileStream fs, uint offset, uint size)
    {
        if (size == 0 || offset + size > fs.Length)
        {
            return [];
        }

        fs.Seek(offset, SeekOrigin.Begin);
        var buf = new byte[size];
        var read = fs.Read(buf, 0, (int)size);
        return read == size ? buf : buf[..read];
    }

    /// <summary>
    /// 获取 SHA1 校验和。
    /// <para>Calculates the SHA1 checksum.</para>
    /// </summary>
    /// <param name="data">待计算数据。<para>The data to calculate checksum for.</para></param>
    /// <returns>SHA1 十六进制字符串。<para>The SHA1 hex string.</para></returns>
    public static string CalculateSha1(byte[] data)
    {
        var hash = SHA1.HashData(data);
        return Convert.ToHexStringLower(hash);
    }

    private static uint ReadUInt32LE(byte[] data, int offset) => BitConverter.ToUInt32(data, offset);

    private static ulong ReadUInt64LE(byte[] data, int offset) => BitConverter.ToUInt64(data, offset);

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
    /// 释放资源。
    /// <para>Releases all resources used by the parser.</para>
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 释放资源的核心方法。
    /// <para>Core method for releasing resources.</para>
    /// </summary>
    /// <param name="disposing">是否释放托管资源。<para>Whether to release managed resources.</para></param>
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
            RecoveryDtbo = [];
            Signature = [];
        }
        _disposed = true;
    }
}