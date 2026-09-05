using System.Text;

namespace UotanStudio.Firmware.BootImage;

/// <summary>
/// DHTB (Debut) Boot 镜像解析器。
/// <para>
/// 负责解析使用 DHTB 格式的 boot 镜像。</para>
/// <para>
/// Parses boot images using the DHTB (Debut) format.</para>
/// <para>
/// DHTB 是某些设备使用的专有格式，通常基于标准 boot 头加上额外的头部标记。</para>
/// <para>
/// DHTB is a proprietary format used by some devices, usually based on standard boot header with additional header markers.</para>
/// </summary>
public static class DhtbTool
{
    private const uint DhtbMagic = 0x42484444;
    private const int DhtbHeaderSize = 512;

    /// <summary>
    /// DHTB 头部信息。
    /// <para>DHTB header information.</para>
    /// </summary>
    public class DhtbHeader
    {
        /// <summary>
        /// Magic 值。
        /// <para>The magic value.</para>
        /// </summary>
        public uint Magic { get; set; }

        /// <summary>
        /// 头部版本。
        /// <para>The header version.</para>
        /// </summary>
        public uint HeaderVersion { get; set; }

        /// <summary>
        /// 头部大小。
        /// <para>The header size.</para>
        /// </summary>
        public uint HeaderSize { get; set; }

        /// <summary>
        /// 内核大小。
        /// <para>The kernel size.</para>
        /// </summary>
        public uint KernelSize { get; set; }

        /// <summary>
        /// 内核偏移量。
        /// <para>The kernel offset.</para>
        /// </summary>
        public uint KernelOffset { get; set; }

        /// <summary>
        /// 内核加载地址。
        /// <para>The kernel load address.</para>
        /// </summary>
        public uint KernelAddr { get; set; }

        /// <summary>
        /// Ramdisk 大小。
        /// <para>The ramdisk size.</para>
        /// </summary>
        public uint RamdiskSize { get; set; }

        /// <summary>
        /// Ramdisk 偏移量。
        /// <para>The ramdisk offset.</para>
        /// </summary>
        public uint RamdiskOffset { get; set; }

        /// <summary>
        /// Ramdisk 加载地址。
        /// <para>The ramdisk load address.</para>
        /// </summary>
        public uint RamdiskAddr { get; set; }

        /// <summary>
        /// 第二阶段大小。
        /// <para>The second stage size.</para>
        /// </summary>
        public uint SecondSize { get; set; }

        /// <summary>
        /// 第二阶段偏移量。
        /// <para>The second stage offset.</para>
        /// </summary>
        public uint SecondOffset { get; set; }

        /// <summary>
        /// 第二阶段加载地址。
        /// <para>The second stage load address.</para>
        /// </summary>
        public uint SecondAddr { get; set; }

        /// <summary>
        /// 标签地址。
        /// <para>The tags address.</para>
        /// </summary>
        public uint TagsAddr { get; set; }

        /// <summary>
        /// 页大小。
        /// <para>The page size.</para>
        /// </summary>
        public uint PageSize { get; set; }

        /// <summary>
        /// 产品名称。
        /// <para>The product name.</para>
        /// </summary>
        public string ProductName { get; set; } = string.Empty;

        /// <summary>
        /// 命令行。
        /// <para>The command line.</para>
        /// </summary>
        public string CommandLine { get; set; } = string.Empty;

        /// <summary>
        /// 压缩类型。
        /// <para>The compression type.</para>
        /// </summary>
        public CompressionType KernelCompression { get; set; }

        /// <summary>
        /// ID 数组。
        /// <para>The ID array.</para>
        /// </summary>
        public uint[] Ids { get; set; } = new uint[8];
    }

    /// <summary>
    /// DHTB 解析结果。
    /// <para>DHTB parsing result.</para>
    /// </summary>
    public class DhtbResult
    {
        /// <summary>
        /// 是否成功。
        /// <para>Whether successful.</para>
        /// </summary>
        public bool IsValid { get; set; }

        /// <summary>
        /// 头部信息。
        /// <para>The header information.</para>
        /// </summary>
        public DhtbHeader Header { get; set; } = new DhtbHeader();

        /// <summary>
        /// 内核数据。
        /// <para>The kernel data.</para>
        /// </summary>
        public byte[] Kernel { get; set; } = [];

        /// <summary>
        /// Ramdisk 数据。
        /// <para>The ramdisk data.</para>
        /// </summary>
        public byte[] Ramdisk { get; set; } = [];

        /// <summary>
        /// 第二阶段数据。
        /// <para>The second stage data.</para>
        /// </summary>
        public byte[] Second { get; set; } = [];

        /// <summary>
        /// 错误消息。
        /// <para>Error message.</para>
        /// </summary>
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// 检查数据是否为 DHTB 格式。
    /// <para>Checks if the data is in DHTB format.</para>
    /// </summary>
    /// <param name="data">待检测的数据。<para>The data to check.</para></param>
    /// <returns>如果是 DHTB 格式则为 true。<para>True if DHTB format.</para></returns>
    public static bool IsDhtbFormat(byte[] data) => data != null && data.Length >= 8 && ReadUInt32LE(data, 0) == DhtbMagic;

    /// <summary>
    /// 解析 DHTB 数据。
    /// <para>Parses DHTB data.</para>
    /// </summary>
    /// <param name="data">DHTB 数据。<para>The DHTB data.</para></param>
    /// <returns>解析结果。<para>The parsing result.</para></returns>
    public static DhtbResult Parse(byte[] data)
    {
        var result = new DhtbResult { IsValid = false };

        if (data == null || data.Length < DhtbHeaderSize)
        {
            result.ErrorMessage = "Data too small for DHTB header";
            return result;
        }

        try
        {
            result.Header.Magic = ReadUInt32LE(data, 0);
            if (result.Header.Magic != DhtbMagic)
            {
                result.ErrorMessage = "Invalid DHTB magic";
                return result;
            }

            result.Header.HeaderVersion = ReadUInt32LE(data, 4);
            result.Header.HeaderSize = ReadUInt32LE(data, 8);
            result.Header.KernelSize = ReadUInt32LE(data, 12);
            result.Header.KernelOffset = ReadUInt32LE(data, 16);
            result.Header.KernelAddr = ReadUInt32LE(data, 20);
            result.Header.RamdiskSize = ReadUInt32LE(data, 24);
            result.Header.RamdiskOffset = ReadUInt32LE(data, 28);
            result.Header.RamdiskAddr = ReadUInt32LE(data, 32);
            result.Header.SecondSize = ReadUInt32LE(data, 36);
            result.Header.SecondOffset = ReadUInt32LE(data, 40);
            result.Header.SecondAddr = ReadUInt32LE(data, 44);
            result.Header.TagsAddr = ReadUInt32LE(data, 48);
            result.Header.PageSize = ReadUInt32LE(data, 52);

            if (data.Length > 56)
            {
                result.Header.ProductName = ReadString(data, 56, 32).TrimEnd('\0');
            }

            if (data.Length > 88)
            {
                result.Header.CommandLine = ReadString(data, 88, 512).TrimEnd('\0');
            }

            var idsOffset = 600;
            if (data.Length >= idsOffset + 32)
            {
                for (var i = 0; i < 8; i++)
                {
                    result.Header.Ids[i] = ReadUInt32LE(data, idsOffset + (i * 4));
                }
            }

            result.IsValid = true;

            ExtractComponents(data, result);
            if (result.Kernel.Length > 0)
            {
                result.Header.KernelCompression = BootCompressor.Detect(result.Kernel);
            }
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            result.IsValid = false;
        }

        return result;
    }

    /// <summary>
    /// 从文件解析 DHTB。
    /// <para>Parses DHTB from a file.</para>
    /// </summary>
    /// <param name="filePath">文件路径。<para>The file path.</para></param>
    /// <returns>解析结果。<para>The parsing result.</para></returns>
    public static DhtbResult ParseFile(string filePath)
    {
        var data = File.ReadAllBytes(filePath);
        return Parse(data);
    }

    /// <summary>
    /// 解压缩 DHTB 中的内核数据。
    /// <para>Decompresses kernel data from DHTB.</para>
    /// </summary>
    /// <param name="data">DHTB 数据。<para>The DHTB data.</para></param>
    /// <returns>解压后的内核数据。<para>The decompressed kernel data.</para></returns>
    public static byte[] DecompressKernel(byte[] data)
    {
        var result = Parse(data);
        if (!result.IsValid)
        {
            throw new CompressionException("Invalid DHTB format");
        }

        if (result.Kernel.Length == 0)
        {
            return [];
        }

        var compType = BootCompressor.Detect(result.Kernel);
        return compType == CompressionType.None ? result.Kernel : BootCompressor.TryDecompress(result.Kernel, compType) ?? result.Kernel;
    }

    /// <summary>
    /// 解压缩 DHTB 中的 ramdisk 数据。
    /// <para>Decompresses ramdisk data from DHTB.</para>
    /// </summary>
    /// <param name="data">DHTB 数据。<para>The DHTB data.</para></param>
    /// <returns>ramdisk 数据。<para>The ramdisk data.</para></returns>
    public static byte[] DecompressRamdisk(byte[] data)
    {
        var result = Parse(data);
        if (!result.IsValid)
        {
            throw new CompressionException("Invalid DHTB format");
        }

        if (result.Ramdisk.Length == 0)
        {
            return [];
        }

        var compType = BootCompressor.Detect(result.Ramdisk);
        return compType == CompressionType.None ? result.Ramdisk : BootCompressor.TryDecompress(result.Ramdisk, compType) ?? result.Ramdisk;
    }

    /// <summary>
    /// 提取内核数据。
    /// <para>Extracts kernel data.</para>
    /// </summary>
    /// <param name="data">DHTB 数据。<para>The DHTB data.</para></param>
    /// <param name="decompress">是否自动解压缩。<para>Whether to auto decompress.</para></param>
    /// <returns>内核数据。<para>The kernel data.</para></returns>
    public static byte[] ExtractKernel(byte[] data, bool decompress = true)
    {
        var result = Parse(data);
        return !result.IsValid ? [] : !decompress ? result.Kernel : DecompressKernel(data);
    }

    /// <summary>
    /// 提取 ramdisk 数据。
    /// <para>Extracts ramdisk data.</para>
    /// </summary>
    /// <param name="data">DHTB 数据。<para>The DHTB data.</para></param>
    /// <param name="decompress">是否自动解压缩。<para>Whether to auto decompress.</para></param>
    /// <returns>ramdisk 数据。<para>The ramdisk data.</para></returns>
    public static byte[] ExtractRamdisk(byte[] data, bool decompress = true)
    {
        var result = Parse(data);
        return !result.IsValid ? [] : !decompress ? result.Ramdisk : DecompressRamdisk(data);
    }

    private static void ExtractComponents(byte[] data, DhtbResult result)
    {
        var headerSize = result.Header.HeaderSize > 0 ? result.Header.HeaderSize : DhtbHeaderSize;
        var pageSize = result.Header.PageSize > 0 ? result.Header.PageSize : 2048;

        if (result.Header.KernelOffset > 0 && result.Header.KernelSize > 0)
        {
            var kernelOffset = AlignToPage(headerSize + result.Header.KernelOffset, pageSize);
            if (kernelOffset + result.Header.KernelSize <= data.Length)
            {
                result.Kernel = new byte[result.Header.KernelSize];
                Array.Copy(data, kernelOffset, result.Kernel, 0, result.Header.KernelSize);
            }
        }

        if (result.Header.RamdiskOffset > 0 && result.Header.RamdiskSize > 0)
        {
            var ramdiskOffset = AlignToPage(headerSize + result.Header.RamdiskOffset, pageSize);
            if (ramdiskOffset + result.Header.RamdiskSize <= data.Length)
            {
                result.Ramdisk = new byte[result.Header.RamdiskSize];
                Array.Copy(data, ramdiskOffset, result.Ramdisk, 0, result.Header.RamdiskSize);
            }
        }

        if (result.Header.SecondOffset > 0 && result.Header.SecondSize > 0)
        {
            var secondOffset = AlignToPage(headerSize + result.Header.SecondOffset, pageSize);
            if (secondOffset + result.Header.SecondSize <= data.Length)
            {
                result.Second = new byte[result.Header.SecondSize];
                Array.Copy(data, secondOffset, result.Second, 0, result.Header.SecondSize);
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

    private static uint AlignToPage(uint offset, uint pageSize) => (offset + pageSize - 1) & ~(pageSize - 1);
}