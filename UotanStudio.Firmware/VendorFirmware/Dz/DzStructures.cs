using System.Runtime.InteropServices;
using System.Text;
using UotanStudio.Firmware.Core.Text;

namespace UotanStudio.Firmware.VendorFirmware.Dz;

/// <summary>
/// Magic byte sequences that identify LG DZ and KDZ firmware containers.
/// <para>用于识别 LG DZ 与 KDZ 固件容器的魔数字节序列。</para>
/// </summary>
internal static class DzConstants
{
    /// <summary>
    /// Four-byte signature <c>32 96 18 74</c> expected at the start of a DZ file header.
    /// <para>位于 DZ 文件头起始处、期望匹配的四字节签名 <c>32 96 18 74</c>。</para>
    /// </summary>
    public static readonly byte[] DzFileHeader = [0x32, 0x96, 0x18, 0x74];

    /// <summary>
    /// Four-byte signature <c>30 12 95 78</c> expected at the start of every DZ chunk header.
    /// <para>位于每个 DZ 块头起始处、期望匹配的四字节签名 <c>30 12 95 78</c>。</para>
    /// </summary>
    public static readonly byte[] DzChunkHeader = [0x30, 0x12, 0x95, 0x78];

    /// <summary>
    /// Eight-byte KDZ signature recognised as header type <c>0</c>.
    /// <para>被识别为头部类型 <c>0</c> 的八字节 KDZ 签名。</para>
    /// </summary>
    public static readonly byte[] KdzHeaderV0 = [0x28, 0x05, 0x00, 0x00, 0x34, 0x31, 0x25, 0x80];

    /// <summary>
    /// Eight-byte KDZ signature recognised as header type <c>1</c>.
    /// <para>被识别为头部类型 <c>1</c> 的八字节 KDZ 签名。</para>
    /// </summary>
    public static readonly byte[] KdzHeaderV1 = [0x18, 0x05, 0x00, 0x00, 0x32, 0x79, 0x44, 0x50];

    /// <summary>
    /// Eight-byte KDZ signature recognised as header type <c>2</c>.
    /// <para>被识别为头部类型 <c>2</c> 的八字节 KDZ 签名。</para>
    /// </summary>
    public static readonly byte[] KdzHeaderV2 = [0x28, 0x05, 0x00, 0x00, 0x24, 0x38, 0x22, 0x25];
}

/// <summary>
/// Fixed 512-byte header at the start of an LG DZ package: device identification, the declared chunk
/// count and the package digest fields. Multi-byte integers are decoded through <c>BitConverter</c>,
/// so they follow the host byte order. Byte fields are stored verbatim; only the first
/// <see cref="Size"/> bytes of the input are consumed.
/// <para>DZ 包起始处固定的 512 字节头：设备标识、声明的块数量以及包摘要字段。多字节整数通过
/// <c>BitConverter</c> 解码，因此遵循宿主机字节序。字节字段按原样保存，输入中仅使用前
/// <see cref="Size"/> 字节。</para>
/// </summary>
internal sealed class DzFileHeader
{
    /// <summary>
    /// Raw four bytes copied from header offset <c>0</c>, compared against
    /// <see cref="DzConstants.DzFileHeader"/> to confirm the container type.
    /// <para>从头部偏移 <c>0</c> 复制的原始四字节，与 <see cref="DzConstants.DzFileHeader"/>
    /// 比较以确认容器类型。</para>
    /// </summary>
    public byte[] Header = new byte[4];

    /// <summary>
    /// Format major version read from header offset <c>4</c>.
    /// <para>从头部偏移 <c>4</c> 读取的格式主版本号。</para>
    /// </summary>
    public uint FormatMajor;

    /// <summary>
    /// Format minor version read from header offset <c>8</c>.
    /// <para>从头部偏移 <c>8</c> 读取的格式次版本号。</para>
    /// </summary>
    public uint FormatMinor;

    /// <summary>
    /// Reserved uint32 at header offset <c>12</c>, kept exactly as read.
    /// <para>头部偏移 <c>12</c> 处保留的 uint32，按读取到的原值保存。</para>
    /// </summary>
    public uint Reserved0;

    /// <summary>
    /// Device model name as raw bytes at header offset <c>16</c>, zero-padded to 32 bytes; see
    /// <see cref="GetDevice"/>.
    /// <para>头部偏移 <c>16</c> 处的设备型号原始字节，以零填充至 32 字节；参见
    /// <see cref="GetDevice"/>。</para>
    /// </summary>
    public byte[] Device = new byte[32];

    /// <summary>
    /// Firmware build version as raw bytes at header offset <c>48</c>, occupying 144 bytes; see
    /// <see cref="GetVersion"/>.
    /// <para>头部偏移 <c>48</c> 处的固件构建版本原始字节，共 144 字节；参见
    /// <see cref="GetVersion"/>。</para>
    /// </summary>
    public byte[] Version = new byte[144];

    /// <summary>
    /// Declared number of chunk headers that follow the file header, read from header offset
    /// <c>192</c>. The parser that consumes it rejects a count larger than the remaining file.
    /// <para>从头部偏移 <c>192</c> 读取的后续块头声明数量。使用该值的解析器会拒绝超出文件剩余
    /// 长度的数量。</para>
    /// </summary>
    public uint ChunkCount;

    /// <summary>
    /// Sixteen raw digest bytes at header offset <c>196</c>. This type copies them without verifying
    /// them.
    /// <para>头部偏移 <c>196</c> 处的十六个原始摘要字节。本类型仅复制而不校验。</para>
    /// </summary>
    public byte[] Md5 = new byte[16];

    /// <summary>
    /// Unmodelled uint32 at header offset <c>212</c>, preserved as read.
    /// <para>头部偏移 <c>212</c> 处未建模的 uint32，按读取到的原值保存。</para>
    /// </summary>
    public uint Unknown0;

    /// <summary>
    /// Reserved uint32 at header offset <c>216</c>, preserved as read.
    /// <para>头部偏移 <c>216</c> 处保留的 uint32，按读取到的原值保存。</para>
    /// </summary>
    public uint Reserved1;

    /// <summary>
    /// Reserved uint16 at header offset <c>220</c>, preserved as read.
    /// <para>头部偏移 <c>220</c> 处保留的 uint16，按读取到的原值保存。</para>
    /// </summary>
    public ushort Reserved4;

    /// <summary>
    /// Unmodelled 16 bytes at header offset <c>222</c>, preserved as read.
    /// <para>头部偏移 <c>222</c> 处未建模的 16 字节，按读取到的原值保存。</para>
    /// </summary>
    public byte[] Unknown1 = new byte[16];

    /// <summary>
    /// Unmodelled 50 bytes at header offset <c>238</c>, preserved as read.
    /// <para>头部偏移 <c>238</c> 处未建模的 50 字节，按读取到的原值保存。</para>
    /// </summary>
    public byte[] Unknown2 = new byte[50];

    /// <summary>
    /// Build type label as raw bytes at header offset <c>288</c>, 20 bytes long.
    /// <para>头部偏移 <c>288</c> 处的构建类型标签原始字节，长 20 字节。</para>
    /// </summary>
    public byte[] BuildType = new byte[20];

    /// <summary>
    /// Unmodelled 4 bytes at header offset <c>308</c>, preserved as read.
    /// <para>头部偏移 <c>308</c> 处未建模的 4 字节，按读取到的原值保存。</para>
    /// </summary>
    public byte[] Unknown3 = new byte[4];

    /// <summary>
    /// Android version label as raw bytes at header offset <c>312</c>, 10 bytes long; see
    /// <see cref="GetAndroidVersion"/>.
    /// <para>头部偏移 <c>312</c> 处的 Android 版本标签原始字节，长 10 字节；参见
    /// <see cref="GetAndroidVersion"/>。</para>
    /// </summary>
    public byte[] AndroidVer = new byte[10];

    /// <summary>
    /// Date code from the source build as raw bytes at header offset <c>322</c>, 10 bytes long.
    /// <para>源构建的日期码原始字节位于头部偏移 <c>322</c>，长 10 字节。</para>
    /// </summary>
    public byte[] OldDateCode = new byte[10];

    /// <summary>
    /// Reserved uint32 at header offset <c>332</c>, preserved as read.
    /// <para>头部偏移 <c>332</c> 处保留的 uint32，按读取到的原值保存。</para>
    /// </summary>
    public uint Reserved5;

    /// <summary>
    /// Unmodelled uint32 at header offset <c>336</c>, preserved as read.
    /// <para>头部偏移 <c>336</c> 处未建模的 uint32，按读取到的原值保存。</para>
    /// </summary>
    public uint Unknown4;

    /// <summary>
    /// Unmodelled uint64 at header offset <c>340</c>, preserved as read.
    /// <para>头部偏移 <c>340</c> 处未建模的 uint64，按读取到的原值保存。</para>
    /// </summary>
    public ulong Unknown5;

    /// <summary>
    /// Trailing 164 bytes at header offset <c>348</c> that complete the fixed header size, preserved
    /// as read.
    /// <para>头部偏移 <c>348</c> 处补齐固定头部大小的末尾 164 字节，按读取到的原值保存。</para>
    /// </summary>
    public byte[] Pad = new byte[164];

    /// <summary>
    /// Fixed header length in bytes, <c>512</c>; also the minimum input length for
    /// <see cref="FromBytes"/>.
    /// <para>固定头部长度（字节），值为 <c>512</c>；同时也是 <see cref="FromBytes"/> 要求的最小
    /// 输入长度。</para>
    /// </summary>
    public const int Size = 512;

    /// <summary>
    /// Parses the DZ file header from a byte array.
    /// <para>从字节数组解析 DZ 文件头。</para>
    /// </summary>
    /// <param name="buffer">
    /// Buffer holding at least <see cref="Size"/> header bytes; only the first <see cref="Size"/>
    /// bytes are read and the magic field is not validated here.
    /// <para>至少包含 <see cref="Size"/> 个头部字节的缓冲区；仅读取前 <see cref="Size"/> 字节，
    /// 且此处不校验魔数字段。</para>
    /// </param>
    /// <returns>
    /// A header instance whose array fields are fresh copies, so later edits to
    /// <paramref name="buffer"/> do not change the result.
    /// <para>解析得到的头部实例，其数组字段均为新副本，因此后续修改
    /// <paramref name="buffer"/> 不会影响结果。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="buffer"/> is shorter than <see cref="Size"/> bytes.
    /// <para><paramref name="buffer"/> 的长度小于 <see cref="Size"/> 字节。</para>
    /// </exception>
    public static DzFileHeader FromBytes(byte[] buffer)
    {
        if (buffer.Length < Size)
        {
            throw new ArgumentException($"Buffer must be at least {Size} bytes.", nameof(buffer));
        }

        var header = new DzFileHeader();
        var offset = 0;
        header.Header = buffer.AsSpan(offset, 4).ToArray(); offset += 4;
        header.FormatMajor = BitConverter.ToUInt32(buffer, offset); offset += 4;
        header.FormatMinor = BitConverter.ToUInt32(buffer, offset); offset += 4;
        header.Reserved0 = BitConverter.ToUInt32(buffer, offset); offset += 4;
        header.Device = buffer.AsSpan(offset, 32).ToArray(); offset += 32;
        header.Version = buffer.AsSpan(offset, 144).ToArray(); offset += 144;
        header.ChunkCount = BitConverter.ToUInt32(buffer, offset); offset += 4;
        header.Md5 = buffer.AsSpan(offset, 16).ToArray(); offset += 16;
        header.Unknown0 = BitConverter.ToUInt32(buffer, offset); offset += 4;
        header.Reserved1 = BitConverter.ToUInt32(buffer, offset); offset += 4;
        header.Reserved4 = BitConverter.ToUInt16(buffer, offset); offset += 2;
        header.Unknown1 = buffer.AsSpan(offset, 16).ToArray(); offset += 16;
        header.Unknown2 = buffer.AsSpan(offset, 50).ToArray(); offset += 50;
        header.BuildType = buffer.AsSpan(offset, 20).ToArray(); offset += 20;
        header.Unknown3 = buffer.AsSpan(offset, 4).ToArray(); offset += 4;
        header.AndroidVer = buffer.AsSpan(offset, 10).ToArray(); offset += 10;
        header.OldDateCode = buffer.AsSpan(offset, 10).ToArray(); offset += 10;
        header.Reserved5 = BitConverter.ToUInt32(buffer, offset); offset += 4;
        header.Unknown4 = BitConverter.ToUInt32(buffer, offset); offset += 4;
        header.Unknown5 = BitConverter.ToUInt64(buffer, offset); offset += 8;
        header.Pad = buffer.AsSpan(offset, 164).ToArray();
        return header;
    }

    /// <summary>
    /// Gets the device model string.
    /// <para>获取设备型号字符串。</para>
    /// </summary>
    /// <returns>
    /// The UTF-8 decoding of <see cref="Device"/> with trailing NUL characters removed; an empty
    /// string when the field holds only NUL bytes. Bytes that are not valid UTF-8 become replacement
    /// characters rather than an error.
    /// <para>对 <see cref="Device"/> 作 UTF-8 解码并去除结尾 NUL 字符；字段全为 NUL 时返回空
    /// 字符串。非合法 UTF-8 字节会转为替换字符而不会抛出错误。</para>
    /// </returns>
    public string GetDevice() => Encoding.UTF8.GetStringTrimNul(Device);

    /// <summary>
    /// Gets the version string.
    /// <para>获取版本字符串。</para>
    /// </summary>
    /// <returns>
    /// The UTF-8 decoding of <see cref="Version"/> with trailing NUL characters removed; an empty
    /// string when the field holds only NUL bytes.
    /// <para>对 <see cref="Version"/> 作 UTF-8 解码并去除结尾 NUL 字符；字段全为 NUL 时返回空
    /// 字符串。</para>
    /// </returns>
    public string GetVersion() => Encoding.UTF8.GetStringTrimNul(Version);

    /// <summary>
    /// Gets the Android version string.
    /// <para>获取 Android 版本字符串。</para>
    /// </summary>
    /// <returns>
    /// The UTF-8 decoding of <see cref="AndroidVer"/> with trailing NUL characters removed; an empty
    /// string when the field holds only NUL bytes.
    /// <para>对 <see cref="AndroidVer"/> 作 UTF-8 解码并去除结尾 NUL 字符；字段全为 NUL 时返回空
    /// 字符串。</para>
    /// </returns>
    public string GetAndroidVersion() => Encoding.UTF8.GetStringTrimNul(AndroidVer);
}

/// <summary>
/// Fixed 512-byte header of one DZ chunk: the slice and chunk names, the compressed payload length,
/// the expected decompressed size, the target start sector and the verification fields. Multi-byte
/// integers are decoded through <c>BitConverter</c>, so they follow the host byte order.
/// <para>DZ 单个块固定的 512 字节头：切片名与块名、压缩负载长度、期望的解压大小、目标起始扇区
/// 以及校验字段。多字节整数通过 <c>BitConverter</c> 解码，因此遵循宿主机字节序。</para>
/// </summary>
public sealed class DzChunkHeader
{
    /// <summary>
    /// Raw four bytes copied from chunk-header offset <c>0</c>, compared against
    /// <see cref="DzConstants.DzChunkHeader"/> to accept the chunk.
    /// <para>从块头偏移 <c>0</c> 复制的原始四字节，与 <see cref="DzConstants.DzChunkHeader"/>
    /// 比较后才会接受该块。</para>
    /// </summary>
    public byte[] Header = new byte[4];

    /// <summary>
    /// Slice (partition) name as raw bytes at chunk-header offset <c>4</c>, zero-padded to 32 bytes;
    /// see <see cref="GetSliceName"/>.
    /// <para>块头偏移 <c>4</c> 处的切片（分区）名原始字节，以零填充至 32 字节；参见
    /// <see cref="GetSliceName"/>。</para>
    /// </summary>
    public byte[] SliceName = new byte[32];

    /// <summary>
    /// Chunk name used as the extracted file name, raw bytes at chunk-header offset <c>36</c>,
    /// zero-padded to 64 bytes; see <see cref="GetChunkName"/>.
    /// <para>作为提取文件名的块名原始字节位于块头偏移 <c>36</c>，以零填充至 64 字节；参见
    /// <see cref="GetChunkName"/>。</para>
    /// </summary>
    public byte[] ChunkName = new byte[64];

    /// <summary>
    /// Expected decompressed size of the chunk payload in bytes, read from chunk-header offset
    /// <c>100</c>. Decompression must produce exactly this many bytes.
    /// <para>块负载期望的解压后大小（字节），读取自块头偏移 <c>100</c>。解压必须正好产生这么多
    /// 字节。</para>
    /// </summary>
    public uint TargetSize;

    /// <summary>
    /// Length in bytes of the stored, still-compressed payload that follows the chunk table, read
    /// from chunk-header offset <c>104</c>. It bounds the region the reader may consume.
    /// <para>块表之后存放的压缩负载长度（字节），读取自块头偏移 <c>104</c>，用于限定读取器可消费
    /// 的区域。</para>
    /// </summary>
    public uint DataSize;

    /// <summary>
    /// Sixteen MD5 bytes at chunk-header offset <c>108</c> covering the decompressed payload; a chunk
    /// whose value is all zero skips digest verification.
    /// <para>块头偏移 <c>108</c> 处的十六个 MD5 字节，对应解压后的负载；该值全为零时会跳过摘要
    /// 校验。</para>
    /// </summary>
    public byte[] Md5 = new byte[16];

    /// <summary>
    /// Target start sector of the chunk on the storage device, read from chunk-header offset
    /// <c>124</c>. Partition reconstruction turns it into a byte offset using the partition layout's
    /// start LBA and logical block size.
    /// <para>块在存储设备上的目标起始扇区，读取自块头偏移 <c>124</c>。分区重建会结合分区布局的
    /// 起始 LBA 与逻辑块大小将其换算为字节偏移。</para>
    /// </summary>
    public uint TargetAddr;

    /// <summary>
    /// Trim (sector) count read from chunk-header offset <c>128</c>; multiplied by the logical block
    /// size it contributes to the span the chunk is treated as covering.
    /// <para>读取自块头偏移 <c>128</c> 的 trim（扇区）数；与逻辑块大小相乘后参与确定该块被视为
    /// 覆盖的范围。</para>
    /// </summary>
    public uint TrimCount;

    /// <summary>
    /// Storage device identifier read from chunk-header offset <c>132</c>; chunks are selected for a
    /// partition by matching this value.
    /// <para>读取自块头偏移 <c>132</c> 的存储设备标识；按该值匹配来为分区筛选块。</para>
    /// </summary>
    public uint Dev;

    /// <summary>
    /// Checksum value read from chunk-header offset <c>136</c> and preserved as read.
    /// <para>读取自块头偏移 <c>136</c> 的校验和值，按原值保存。</para>
    /// </summary>
    public uint Crc32;

    /// <summary>
    /// Trailing 372 bytes at chunk-header offset <c>140</c> that complete the fixed header size,
    /// preserved as read.
    /// <para>块头偏移 <c>140</c> 处补齐固定头部大小的末尾 372 字节，按原值保存。</para>
    /// </summary>
    public byte[] Pad = new byte[372];

    /// <summary>
    /// Fixed chunk-header length in bytes, <c>512</c>; also the minimum input length for
    /// <see cref="FromBytes"/>.
    /// <para>固定块头长度（字节），值为 <c>512</c>；同时也是 <see cref="FromBytes"/> 要求的最小
    /// 输入长度。</para>
    /// </summary>
    public const int Size = 512;

    /// <summary>
    /// Parses a DZ chunk header from a byte array.
    /// <para>从字节数组解析 DZ 块头。</para>
    /// </summary>
    /// <param name="buffer">
    /// Buffer holding at least <see cref="Size"/> chunk-header bytes; only the first
    /// <see cref="Size"/> bytes are read and the magic field is not validated here.
    /// <para>至少包含 <see cref="Size"/> 个块头字节的缓冲区；仅读取前 <see cref="Size"/> 字节，
    /// 且此处不校验魔数字段。</para>
    /// </param>
    /// <returns>
    /// A chunk-header instance whose array fields are fresh copies, so later edits to
    /// <paramref name="buffer"/> do not change the result.
    /// <para>解析得到的块头实例，其数组字段均为新副本，因此后续修改
    /// <paramref name="buffer"/> 不会影响结果。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="buffer"/> is shorter than <see cref="Size"/> bytes.
    /// <para><paramref name="buffer"/> 的长度小于 <see cref="Size"/> 字节。</para>
    /// </exception>
    public static DzChunkHeader FromBytes(byte[] buffer)
    {
        if (buffer.Length < Size)
        {
            throw new ArgumentException($"Buffer must be at least {Size} bytes.", nameof(buffer));
        }

        var chunk = new DzChunkHeader();
        var offset = 0;
        chunk.Header = buffer.AsSpan(offset, 4).ToArray(); offset += 4;
        chunk.SliceName = buffer.AsSpan(offset, 32).ToArray(); offset += 32;
        chunk.ChunkName = buffer.AsSpan(offset, 64).ToArray(); offset += 64;
        chunk.TargetSize = BitConverter.ToUInt32(buffer, offset); offset += 4;
        chunk.DataSize = BitConverter.ToUInt32(buffer, offset); offset += 4;
        chunk.Md5 = buffer.AsSpan(offset, 16).ToArray(); offset += 16;
        chunk.TargetAddr = BitConverter.ToUInt32(buffer, offset); offset += 4;
        chunk.TrimCount = BitConverter.ToUInt32(buffer, offset); offset += 4;
        chunk.Dev = BitConverter.ToUInt32(buffer, offset); offset += 4;
        chunk.Crc32 = BitConverter.ToUInt32(buffer, offset); offset += 4;
        chunk.Pad = buffer.AsSpan(offset, 372).ToArray();
        return chunk;
    }

    /// <summary>
    /// Gets the block name string.
    /// <para>获取块名称字符串。</para>
    /// </summary>
    /// <returns>
    /// The UTF-8 decoding of <see cref="ChunkName"/> with trailing NUL characters removed; an empty
    /// string when the field holds only NUL bytes. Bytes that are not valid UTF-8 become replacement
    /// characters rather than an error.
    /// <para>对 <see cref="ChunkName"/> 作 UTF-8 解码并去除结尾 NUL 字符；字段全为 NUL 时返回空
    /// 字符串。非合法 UTF-8 字节会转为替换字符而不会抛出错误。</para>
    /// </returns>
    public string GetChunkName() => Encoding.UTF8.GetStringTrimNul(ChunkName);

    /// <summary>
    /// Gets the slice name string.
    /// <para>获取切片名称字符串。</para>
    /// </summary>
    /// <returns>
    /// The UTF-8 decoding of <see cref="SliceName"/> with trailing NUL characters removed; an empty
    /// string when the field holds only NUL bytes. Partition selection compares this value with the
    /// requested layout name.
    /// <para>对 <see cref="SliceName"/> 作 UTF-8 解码并去除结尾 NUL 字符；字段全为 NUL 时返回空
    /// 字符串。分区筛选会把该值与所请求的布局名比较。</para>
    /// </returns>
    public string GetSliceName() => Encoding.UTF8.GetStringTrimNul(SliceName);
}

/// <summary>
/// One 272-byte entry of an LG KDZ partition table, laid out sequentially and packed with no padding:
/// a 256-byte name followed by two 64-bit values decoded through <c>BitConverter</c>, so they follow
/// the host byte order.
/// <para>LG KDZ 分区表中 272 字节的一条记录，按顺序布局且无填充：前 256 字节为名称，随后是两个
/// 通过 <c>BitConverter</c> 解码的 64 位值，因此遵循宿主机字节序。</para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct KdzHeader
{
    /// <summary>
    /// Partition name as raw bytes occupying the first 256 bytes of the entry; see
    /// <see cref="GetName"/>.
    /// <para>分区名原始字节，占记录的前 256 字节；参见 <see cref="GetName"/>。</para>
    /// </summary>
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
    public byte[] Name;

    /// <summary>
    /// Length in bytes of the partition data, read from entry offset <c>256</c>. Together with
    /// <see cref="Offset"/> it must stay inside the container, otherwise the entry is rejected.
    /// <para>分区数据长度（字节），读取自记录偏移 <c>256</c>。它与 <see cref="Offset"/> 必须落在
    /// 容器之内，否则该记录会被拒绝。</para>
    /// </summary>
    public ulong Length;

    /// <summary>
    /// Absolute byte offset of the partition data inside the KDZ file, read from entry offset
    /// <c>264</c>.
    /// <para>分区数据在 KDZ 文件内的绝对字节偏移，读取自记录偏移 <c>264</c>。</para>
    /// </summary>
    public ulong Offset;

    /// <summary>
    /// Fixed entry length in bytes, <c>272</c>; also the minimum input length for
    /// <see cref="FromBytes"/>.
    /// <para>固定记录长度（字节），值为 <c>272</c>；同时也是 <see cref="FromBytes"/> 要求的最小
    /// 输入长度。</para>
    /// </summary>
    public const int Size = 272;

    /// <summary>
    /// Parses a KDZ partition header from a byte array.
    /// <para>从字节数组解析 KDZ 分区头。</para>
    /// </summary>
    /// <param name="buffer">
    /// Buffer holding at least <see cref="Size"/> entry bytes; only the first <see cref="Size"/>
    /// bytes are read and the name is not decoded here.
    /// <para>至少包含 <see cref="Size"/> 个记录字节的缓冲区；仅读取前 <see cref="Size"/> 字节，
    /// 且此处不解码名称。</para>
    /// </param>
    /// <returns>
    /// A value holding a fresh copy of the name bytes plus the decoded length and offset, so later
    /// edits to <paramref name="buffer"/> do not change the result. Bounds against the real file
    /// length are checked by the caller, not here.
    /// <para>包含名称字节新副本以及解码得到的长度与偏移的值，因此后续修改
    /// <paramref name="buffer"/> 不会影响结果；针对真实文件长度的边界检查由调用方完成，而非此
    /// 处。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="buffer"/> is shorter than <see cref="Size"/> bytes.
    /// <para><paramref name="buffer"/> 的长度小于 <see cref="Size"/> 字节。</para>
    /// </exception>
    public static KdzHeader FromBytes(byte[] buffer)
    {
        return buffer.Length < Size
            ? throw new ArgumentException($"Buffer must be at least {Size} bytes.", nameof(buffer))
            : new KdzHeader
            {
                Name = buffer.AsSpan(0, 256).ToArray(),
                Length = BitConverter.ToUInt64(buffer, 256),
                Offset = BitConverter.ToUInt64(buffer, 264)
            };
    }

    /// <summary>
    /// Gets the partition name string.
    /// <para>获取分区名称字符串。</para>
    /// </summary>
    /// <returns>
    /// The UTF-8 decoding of all 256 <see cref="Name"/> bytes with trailing NUL characters removed;
    /// an empty string when the field holds only NUL bytes. Bytes that are not valid UTF-8 become
    /// replacement characters rather than an error.
    /// <para>对全部 256 个 <see cref="Name"/> 字节作 UTF-8 解码并去除结尾 NUL 字符；字段全为 NUL
    /// 时返回空字符串。非合法 UTF-8 字节会转为替换字符而不会抛出错误。</para>
    /// </returns>
    public readonly string GetName() => Encoding.UTF8.GetStringTrimNul(Name);
}