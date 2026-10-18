using System.Text;
using UotanStudio.Firmware.BootSecurity.BootImage.Components;
using UotanStudio.Firmware.BootSecurity.BootImage.Models;

using UotanStudio.Firmware.Core;

namespace UotanStudio.Firmware.BootSecurity.BootImage.Formats;

/// <summary>
/// Reads Samsung DHTB boot-image containers, whose 512-byte little-endian header precedes the
/// page-aligned kernel, ramdisk and second-stage payloads.
/// <para>读取三星 DHTB 启动镜像容器：512 字节的小端序头部之后是按页对齐的内核、ramdisk 与 second 阶段载荷。</para>
/// </summary>
public static class DhtbTool
{
    private const uint DhtbMagic = 0x42484444;
    private const int DhtbHeaderSize = 512;

    /// <summary>
    /// Fixed-layout DHTB header fields as decoded from the first bytes of the container.
    /// <para>从容器起始字节解析得到的定长 DHTB 头部字段。</para>
    /// </summary>
    public class DhtbHeader
    {
        /// <summary>Container magic word. <para>容器魔数字。</para></summary>
        /// <value>The little-endian word 0x42484444 read from offset 0; 0 when the input is shorter than four bytes.
        /// <para>取自偏移 0 的小端序字 0x42484444；输入不足四个字节时为 0。</para></value>
        public uint Magic { get; set; }

        /// <summary>Header layout version. <para>头部布局版本。</para></summary>
        /// <value>The unvalidated little-endian word at offset 4; 0 when the input is shorter.
        /// <para>偏移 4 处未经校验的小端序字；输入更短时为 0。</para></value>
        public uint HeaderVersion { get; set; }

        /// <summary>Declared header length. <para>声明的头部长度。</para></summary>
        /// <value>Byte count used as the base of every payload offset; extraction substitutes the default
        /// 512 when it is 0.
        /// <para>作为所有载荷偏移基准的字节数；为 0 时提取阶段改用默认的 512。</para></value>
        public uint HeaderSize { get; set; }

        /// <summary>Declared kernel payload size. <para>声明的内核载荷大小。</para></summary>
        /// <value>Byte count copied for the kernel; 0 means no kernel region is read.
        /// <para>为内核复制的字节数；为 0 表示不读取内核区域。</para></value>
        public uint KernelSize { get; set; }

        /// <summary>Declared kernel position. <para>声明的内核位置。</para></summary>
        /// <value>Kernel offset relative to the end of the header, before page alignment; 0 means the
        /// region is treated as absent.
        /// <para>内核相对于头部末尾的偏移（页对齐之前）；为 0 表示视为不存在。</para></value>
        public uint KernelOffset { get; set; }

        /// <summary>Kernel load address. <para>内核加载地址。</para></summary>
        /// <value>Little-endian word at offset 20, kept as stored and not validated.
        /// <para>偏移 20 处的小端序字，按存储保留且不做校验。</para></value>
        public uint KernelAddr { get; set; }

        /// <summary>Declared ramdisk payload size. <para>声明的 ramdisk 载荷大小。</para></summary>
        /// <value>Byte count copied for the ramdisk; 0 means no ramdisk region is read.
        /// <para>为 ramdisk 复制的字节数；为 0 表示不读取 ramdisk 区域。</para></value>
        public uint RamdiskSize { get; set; }

        /// <summary>Declared ramdisk position. <para>声明的 ramdisk 位置。</para></summary>
        /// <value>Ramdisk offset relative to the end of the header, before page alignment; 0 means the
        /// region is treated as absent.
        /// <para>ramdisk 相对于头部末尾的偏移（页对齐之前）；为 0 表示视为不存在。</para></value>
        public uint RamdiskOffset { get; set; }

        /// <summary>Ramdisk load address. <para>ramdisk 加载地址。</para></summary>
        /// <value>Little-endian word at offset 32, kept as stored and not validated.
        /// <para>偏移 32 处的小端序字，按存储保留且不做校验。</para></value>
        public uint RamdiskAddr { get; set; }

        /// <summary>Declared second-stage payload size. <para>声明的 second 阶段载荷大小。</para></summary>
        /// <value>Byte count copied for the second stage; 0 means no such region is read.
        /// <para>为 second 阶段复制的字节数；为 0 表示不读取该区域。</para></value>
        public uint SecondSize { get; set; }

        /// <summary>Declared second-stage position. <para>声明的 second 阶段位置。</para></summary>
        /// <value>Second-stage offset relative to the end of the header, before page alignment; 0 means the
        /// region is treated as absent.
        /// <para>second 阶段相对于头部末尾的偏移（页对齐之前）；为 0 表示视为不存在。</para></value>
        public uint SecondOffset { get; set; }

        /// <summary>Second-stage load address. <para>second 阶段加载地址。</para></summary>
        /// <value>Little-endian word at offset 44, kept as stored and not validated.
        /// <para>偏移 44 处的小端序字，按存储保留且不做校验。</para></value>
        public uint SecondAddr { get; set; }

        /// <summary>Tagged-hash address. <para>tagged-hash 地址。</para></summary>
        /// <value>Little-endian word at offset 48 naming the target address for tagged-hash boot
        /// parameters; not validated.
        /// <para>偏移 48 处的小端序字，给出 tagged-hash 启动参数的目标地址；不做校验。</para></value>
        public uint TagsAddr { get; set; }

        /// <summary>Declared page size. <para>声明的页大小。</para></summary>
        /// <value>Alignment granularity for payload offsets; extraction substitutes 2048 when it is 0.
        /// <para>载荷偏移的对齐粒度；为 0 时提取阶段改用 2048。</para></value>
        public uint PageSize { get; set; }

        /// <summary>Product name string. <para>机型名字符串。</para></summary>
        /// <value>ASCII text from offset 56 (32 bytes) with trailing NUL characters removed; empty when the
        /// image is shorter than offset 56.
        /// <para>取自偏移 56 的 ASCII 文本（32 字节）并去掉结尾 NUL；镜像短于偏移 56 时为空字符串。</para></value>
        public string ProductName { get; set; } = string.Empty;

        /// <summary>Kernel command line string. <para>内核命令行字符串。</para></summary>
        /// <value>ASCII text from offset 88, truncated to the available bytes and at most 512, with trailing
        /// NUL characters removed; empty when the image is shorter than offset 88.
        /// <para>取自偏移 88 的 ASCII 文本，按可用字节截断且最多 512，并去掉结尾 NUL；镜像短于偏移 88 时为空字符串。</para></value>
        public string CommandLine { get; set; } = string.Empty;

        /// <summary>Kernel compression kind. <para>内核压缩类型。</para></summary>
        /// <value>Result of signature detection over the extracted kernel, or <see cref="CompressionType.None"/>
        /// when no kernel bytes were extracted.
        /// <para>对提取出的内核做签名检测的结果；没有内核字节时为 <see cref="CompressionType.None"/>。</para></value>
        public CompressionType KernelCompression { get; set; }

        /// <summary>Identifier words. <para>标识字数组。</para></summary>
        /// <value>Always eight little-endian words from offset 600; every entry stays 0 when the image is
        /// shorter than 632 bytes.
        /// <para>始终为取自偏移 600 的 8 个小端序字；镜像短于 632 字节时每项保持 0。</para></value>
        public uint[] Ids { get; set; } = new uint[8];
    }

    /// <summary>
    /// Outcome of a DHTB read: the header, whichever payloads fit inside the input, and the reason a
    /// parse was rejected. Parsing reports failures through this type rather than throwing.
    /// <para>DHTB 读取结果：头部、能够完整落在输入范围内的载荷，以及解析被拒绝的原因。解析通过本类型报告失败，而不是抛出异常。</para>
    /// </summary>
    public class DhtbResult
    {
        /// <summary>Parse verdict. <para>解析结论。</para></summary>
        /// <value><see langword="true"/> only when the magic matched and header decoding completed; payload
        /// extraction may still leave individual components empty.
        /// <para>仅当魔数匹配且头部解码完成时为 <see langword="true"/>；载荷提取仍可能让个别组件保持为空。</para></value>
        public bool IsValid { get; set; }

        /// <summary>Decoded header. <para>解码后的头部。</para></summary>
        /// <value>Never <see langword="null"/>; fields stay at their defaults when the parse was rejected.
        /// <para>永不为 <see langword="null"/>；解析被拒绝时字段保持默认值。</para></value>
        public DhtbHeader Header { get; set; } = new DhtbHeader();

        /// <summary>Stored kernel bytes. <para>存储的内核字节。</para></summary>
        /// <value>Payload as stored, before any decompression; empty when the declared region is
        /// absent, zero-sized or falls outside the input.
        /// <para>按存储原样保留的载荷（尚未解压）；声明的区域不存在、长度为 0 或落在输入之外时为空。</para></value>
        public byte[] Kernel { get; set; } = [];

        /// <summary>Stored ramdisk bytes. <para>存储的 ramdisk 字节。</para></summary>
        /// <value>Payload exactly as stored; empty when the declared region is absent, zero-sized or falls
        /// outside the input.
        /// <para>按存储原样保留的载荷；声明的区域不存在、长度为 0 或落在输入之外时为空。</para></value>
        public byte[] Ramdisk { get; set; } = [];

        /// <summary>Stored second-stage bytes. <para>存储的 second 阶段字节。</para></summary>
        /// <value>Payload exactly as stored; empty when the declared region is absent, zero-sized or falls
        /// outside the input.
        /// <para>按存储原样保留的载荷；声明的区域不存在、长度为 0 或落在输入之外时为空。</para></value>
        public byte[] Second { get; set; } = [];

        /// <summary>Failure reason. <para>失败原因。</para></summary>
        /// <value>Message for a rejected parse, covering buffer size, magic mismatch and decoding errors, or
        /// <see langword="null"/> after a successful parse.
        /// <para>解析被拒绝时的消息，涵盖缓冲区长度、魔数不匹配与解码错误；解析成功时为 <see langword="null"/>。</para></value>
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// Checks if the data is in DHTB format. A buffer shorter than eight bytes, or <see langword="null"/>,
    /// is reported as not DHTB rather than as an error.
    /// <para>检查数据是否为 DHTB 格式。长度不足 8 字节的缓冲区或 <see langword="null"/> 会被判为非 DHTB，而不是错误。</para>
    /// </summary>
    /// <param name="data">Candidate image bytes; the first four bytes are compared against the magic.
    /// <para>候选镜像字节；前四个字节与魔数比较。</para></param>
    /// <returns><see langword="true"/> when the leading little-endian word equals the DHTB magic.
    /// <para>起始的小端序字等于 DHTB 魔数时为 <see langword="true"/>。</para></returns>
    public static bool IsDhtbFormat(byte[] data) => data != null && data.Length >= 8 && ReadUInt32LE(data, 0) == DhtbMagic;

    /// <summary>
    /// Parses DHTB data.
    /// <para>解析 DHTB 数据。</para>
    /// </summary>
    /// <param name="data">Whole DHTB container, including header and payloads. Input is untrusted: fields
    /// beyond the buffer length decode as 0 and payload regions outside the buffer stay empty instead of
    /// raising an error. The whole container must already be in memory.
    /// <para>完整的 DHTB 容器（含头部与载荷）。输入视为不可信：超出缓冲区长度的字段解码为 0，落在缓冲区之外的载荷区域保持为空而不报错。整个容器必须已驻留内存。</para></param>
    /// <returns>A result with <see cref="DhtbResult.IsValid"/> set when the magic matched and the header
    /// decoded; on failure <see cref="DhtbResult.ErrorMessage"/> carries the reason and no exception escapes,
    /// including for exceptions raised while extracting payloads.
    /// <para>魔数匹配且头部解码成功时 <see cref="DhtbResult.IsValid"/> 为真的结果；失败时由 <see cref="DhtbResult.ErrorMessage"/> 给出原因，且不会抛出异常，提取载荷过程中出现的异常同样被归入该结果。</para></returns>
    /// <example>
    /// <code>
    /// var result = DhtbTool.Parse(bytes);
    /// if (result.IsValid)
    /// {
    ///     int kernelLength = result.Kernel.Length;
    /// }
    /// </code>
    /// </example>
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

    /// <summary>Parses DHTB from a file.<para>从文件解析 DHTB。</para></summary>
    /// <param name="filePath">Path to the DHTB container; the file is read fully into memory before parsing.
    /// <para>DHTB 容器路径；解析前会把整个文件读入内存。</para></param>
    /// <returns>The parse result; an unreadable or non-DHTB file yields <see cref="DhtbResult.IsValid"/> <see langword="false"/> only after the bytes were loaded successfully.
    /// <para>解析结果；只有在字节成功读取后，非 DHTB 文件才会给出 <see cref="DhtbResult.IsValid"/> 为 <see langword="false"/>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="filePath"/> is <see langword="null"/>.
    /// <para><paramref name="filePath"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="FileNotFoundException">The file does not exist.<para>文件不存在。</para></exception>
    /// <exception cref="DirectoryNotFoundException">The path specifies an unknown directory.
    /// <para>路径指向不存在的目录。</para></exception>
    /// <exception cref="UnauthorizedAccessException">The caller lacks read permission for the file.
    /// <para>调用方没有该文件的读取权限。</para></exception>
    /// <exception cref="IOException">An I/O error occurred while reading the file.
    /// <para>读取文件时发生 I/O 错误。</para></exception>
    public static DhtbResult ParseFile(string filePath)
    {
        var data = File.ReadAllBytes(filePath);
        return Parse(data);
    }

    /// <summary>Decompresses kernel data from DHTB.<para>解压缩 DHTB 中的内核数据。</para></summary>
    /// <param name="data">Whole DHTB container; it is parsed on every call.
    /// <para>完整的 DHTB 容器；每次调用都会重新解析。</para></param>
    /// <returns>An empty array when no kernel region was extracted; the stored bytes unchanged when <see cref="BootCompressor.Detect(byte[])"/> finds no known container, and also when the detected decoder fails.
    /// <para>未提取到内核区域时返回空数组；<see cref="BootCompressor.Detect(byte[])"/> 未识别到已知容器时原样返回存储字节，检测到类型但解码失败时同样原样返回。</para></returns>
    /// <exception cref="CompressionException">The container could not be parsed, as reported by <see cref="DhtbResult.IsValid"/>.
    /// <para>容器无法解析，即 <see cref="DhtbResult.IsValid"/> 为假。</para></exception>
    public static byte[] DecompressKernel(byte[] data)
    {
        var result = Parse(data);
        if (!result.IsValid)
        {
            throw new CompressionException("Invalid DHTB format");
        }

        return Decompress(result.Kernel);
    }

    /// <summary>
    /// Decompresses ramdisk data from DHTB.
    /// <para>解压缩 DHTB 中的 ramdisk 数据。</para>
    /// </summary>
    /// <param name="data">Whole DHTB container; it is parsed on every call.
    /// <para>完整的 DHTB 容器；每次调用都会重新解析。</para></param>
    /// <returns>An empty array when no ramdisk region was extracted; the stored bytes unchanged when the
    /// detected container is <see cref="CompressionType.None"/> or when its decoder fails.
    /// <para>未提取到 ramdisk 区域时返回空数组；检测结果为 <see cref="CompressionType.None"/> 或对应解码器失败时原样返回存储字节。</para></returns>
    /// <exception cref="CompressionException">The container could not be parsed, as reported by
    /// <see cref="DhtbResult.IsValid"/>.
    /// <para>容器无法解析，即 <see cref="DhtbResult.IsValid"/> 为假。</para></exception>
    public static byte[] DecompressRamdisk(byte[] data)
    {
        var result = Parse(data);
        if (!result.IsValid)
        {
            throw new CompressionException("Invalid DHTB format");
        }

        return Decompress(result.Ramdisk);
    }

    /// <summary>
    /// Extracts kernel data.
    /// <para>提取内核数据。</para>
    /// </summary>
    /// <param name="data">Whole DHTB container; it is parsed once per call and the extracted components are reused.
    /// <para>完整的 DHTB 容器；每次调用只解析一次，并复用提取出的组件。</para></param>
    /// <param name="decompress">When <see langword="true"/> the extracted bytes are run through container
    /// detection and decompression; <see langword="false"/> returns them exactly as stored.
    /// <para>为 <see langword="true"/> 时对提取字节执行容器检测与解压；为 <see langword="false"/> 时按存储原样返回。</para></param>
    /// <returns>An empty array when the container fails to parse, which is also checked before any
    /// decompression attempt; otherwise the stored kernel, empty when its declared region is unavailable.
    /// <para>容器解析失败时返回空数组（该检查在解压之前完成）；否则返回存储的内核，若其声明的区域不可用则为空。</para></returns>
    public static byte[] ExtractKernel(byte[] data, bool decompress = true)
    {
        var result = Parse(data);
        return !result.IsValid ? [] : !decompress ? result.Kernel : Decompress(result.Kernel);
    }

    /// <summary>
    /// Extracts ramdisk data.
    /// <para>提取 ramdisk 数据。</para>
    /// </summary>
    /// <param name="data">Whole DHTB container; it is parsed once per call and the extracted components are reused.
    /// <para>完整的 DHTB 容器；每次调用只解析一次，并复用提取出的组件。</para></param>
    /// <param name="decompress">When <see langword="true"/> the extracted bytes are run through container
    /// detection and decompression; <see langword="false"/> returns them exactly as stored.
    /// <para>为 <see langword="true"/> 时对提取字节执行容器检测与解压；为 <see langword="false"/> 时按存储原样返回。</para></param>
    /// <returns>An empty array when the container fails to parse, which is checked before any
    /// decompression attempt; otherwise the stored ramdisk, empty when its declared region is unavailable.
    /// <para>容器解析失败时返回空数组（该检查在解压之前完成）；否则返回存储的 ramdisk，若其声明的区域不可用则为空。</para></returns>
    public static byte[] ExtractRamdisk(byte[] data, bool decompress = true)
    {
        var result = Parse(data);
        return !result.IsValid ? [] : !decompress ? result.Ramdisk : Decompress(result.Ramdisk);
    }

    private static byte[] Decompress(byte[] stored)
    {
        if (stored.Length == 0)
        {
            return [];
        }

        var compType = BootCompressor.Detect(stored);
        return compType == CompressionType.None ? stored : BootCompressor.TryDecompress(stored, compType) ?? stored;
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

    private static uint ReadUInt32LE(byte[] data, int offset) => offset + 4 > data.Length ? 0 : data.ReadUInt32LE(offset);

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

    private static uint AlignToPage(uint offset, uint pageSize) => Alignment.Up(offset, pageSize);
}