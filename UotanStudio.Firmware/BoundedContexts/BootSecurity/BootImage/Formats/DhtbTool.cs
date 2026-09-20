using System.Text;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Components;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Models;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Formats;

public static class DhtbTool
{
    private const uint DhtbMagic = 0x42484444;
    private const int DhtbHeaderSize = 512;

    public class DhtbHeader
    {
        public uint Magic { get; set; }

        public uint HeaderVersion { get; set; }

        public uint HeaderSize { get; set; }

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

        public string ProductName { get; set; } = string.Empty;

        public string CommandLine { get; set; } = string.Empty;

        public CompressionType KernelCompression { get; set; }

        public uint[] Ids { get; set; } = new uint[8];
    }

    public class DhtbResult
    {
        public bool IsValid { get; set; }

        public DhtbHeader Header { get; set; } = new DhtbHeader();

        public byte[] Kernel { get; set; } = [];

        public byte[] Ramdisk { get; set; } = [];

        public byte[] Second { get; set; } = [];

        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// Checks if the data is in DHTB format.
    /// <para>检查数据是否为 DHTB 格式。</para>
    /// </summary>
    public static bool IsDhtbFormat(byte[] data) => data != null && data.Length >= 8 && ReadUInt32LE(data, 0) == DhtbMagic;

    /// <summary>
    /// Parses DHTB data.
    /// <para>解析 DHTB 数据。</para>
    /// </summary>
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
    /// Parses DHTB from a file.
    /// <para>从文件解析 DHTB。</para>
    /// </summary>
    public static DhtbResult ParseFile(string filePath)
    {
        var data = File.ReadAllBytes(filePath);
        return Parse(data);
    }

    /// <summary>
    /// Decompresses kernel data from DHTB.
    /// <para>解压缩 DHTB 中的内核数据。</para>
    /// </summary>
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
    /// Decompresses ramdisk data from DHTB.
    /// <para>解压缩 DHTB 中的 ramdisk 数据。</para>
    /// </summary>
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
    /// Extracts kernel data.
    /// <para>提取内核数据。</para>
    /// </summary>
    public static byte[] ExtractKernel(byte[] data, bool decompress = true)
    {
        var result = Parse(data);
        return !result.IsValid ? [] : !decompress ? result.Kernel : DecompressKernel(data);
    }

    /// <summary>
    /// Extracts ramdisk data.
    /// <para>提取 ramdisk 数据。</para>
    /// </summary>
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