using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Tests.TestInfrastructure;

/// <summary>
/// Builds Coolpad CPB containers the way the vendor packer lays them out, so tests can exercise the reader
/// against bytes they did not produce with the production code path.
/// <para>按厂商打包器的布局构造酷派 CPB 容器，使测试能以非生产代码路径产生的字节验证读取实现。</para>
/// </summary>
public static class CpbContainerBuilder
{
    /// <summary>Header length in bytes.<para>头部字节长度。</para></summary>
    public const int HeaderSize = 552;

    /// <summary>Image header length in bytes.<para>镜像头字节长度。</para></summary>
    public const int ImageHeaderSize = 76;

    /// <summary>Correction words the vendor packer writes into a new header.
    /// <para>厂商打包器写入新头部的校正字。</para></summary>
    private static readonly ushort[] CorrectionWords = [0x005C, 0x0836, 0x0828, 0x083A, 0x0809, 0x0825];

    private static readonly ushort[] Table = BuildTable();

    private static readonly byte[] UnknownWords =
    [
        0x55, 0x35, 0x25, 0x27, 0x28, 0x59, 0x2D, 0x11, 0x32, 0x2D, 0x24, 0x3D, 0x5B, 0x2B, 0x3F, 0x11,
        0x0C, 0x16, 0x07, 0x01, 0x11, 0x16, 0x10, 0x03, 0x06, 0x13, 0x03, 0x1C, 0x01, 0x06, 0x00, 0x00,
    ];

    /// <summary>Builds a container from the given image payloads.
    /// <para>由给定镜像负载构造容器。</para>
    /// </summary>
    /// <param name="images">Image names and payload bytes in image-header order.
    /// <para>按镜像头顺序排列的镜像名称与负载字节。</para></param>
    /// <param name="options">Header text, version bytes and checksum variant; <see langword="null"/> uses the defaults.
    /// <para>头部文本、版本字节与校验和变体；传 <see langword="null"/> 时使用默认值。</para></param>
    /// <returns>The complete container bytes.<para>完整的容器字节。</para></returns>
    public static byte[] Build(IReadOnlyList<(string Name, byte[] Payload)> images, CpbContainerOptions? options = null)
        => Build(images, options, out _);

    /// <summary>Builds a container and also returns the un-obfuscated image headers the checksum covers.
    /// <para>构造容器，并同时返回校验和所覆盖的未混淆镜像头。</para>
    /// </summary>
    /// <param name="images">Image names and payload bytes in image-header order.
    /// <para>按镜像头顺序排列的镜像名称与负载字节。</para></param>
    /// <param name="options">Header text, version bytes and checksum variant; <see langword="null"/> uses the defaults.
    /// <para>头部文本、版本字节与校验和变体；传 <see langword="null"/> 时使用默认值。</para></param>
    /// <param name="plainHeaders">Receives the concatenated image headers as stored before obfuscation.
    /// <para>输出混淆前存储的连续镜像头。</para></param>
    /// <returns>The complete container bytes.<para>完整的容器字节。</para></returns>
    public static byte[] Build(IReadOnlyList<(string Name, byte[] Payload)> images, CpbContainerOptions? options,
        out byte[] plainHeaders)
    {
        options ??= new CpbContainerOptions();
        var headerEnd = HeaderSize + (images.Count * ImageHeaderSize);
        var offsets = new int[images.Count];
        var cursor = headerEnd;
        for (var index = 0; index < images.Count; index++)
        {
            offsets[index] = cursor;
            cursor += images[index].Payload.Length;
        }

        var container = new byte[cursor];
        var header = container.AsSpan(0, HeaderSize);
        header[0] = (byte)'C';
        header[1] = (byte)'P';
        header[2] = options.VersionMajor;
        header[3] = options.VersionMinor;
        for (var index = 0; index < CorrectionWords.Length; index++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(header[(4 + (index * 2))..], CorrectionWords[index]);
        }

        UnknownWords.CopyTo(header[16..]);
        WriteString(header[48..80], options.Model);
        WriteString(header[80..96], options.HardwareVersion);
        WriteString(header[96..160], options.Version);
        WriteString(header[160..416], options.RomFrom);
        BinaryPrimitives.WriteUInt32LittleEndian(header[416..], (uint)headerEnd);

        plainHeaders = new byte[images.Count * ImageHeaderSize];
        for (var index = 0; index < images.Count; index++)
        {
            var raw = plainHeaders.AsSpan(index * ImageHeaderSize, ImageHeaderSize);
            WriteString(raw[..64], images[index].Name);
            BinaryPrimitives.WriteUInt32LittleEndian(raw[64..], (uint)offsets[index]);
            BinaryPrimitives.WriteUInt32LittleEndian(raw[68..], (uint)images[index].Payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(raw[72..], Accumulate(0, images[index].Payload));
        }

        var checksum = Accumulate(0, header[..(HeaderSize - 4)]);
        checksum = Accumulate(checksum, plainHeaders);
        checksum = unchecked(checksum + options.VersionMinor);
        BinaryPrimitives.WriteUInt32LittleEndian(header[(HeaderSize - 4)..],
            options.StoreMaskedChecksum ? checksum & 0xFFFF : checksum);

        var key = DeriveKey(header);
        for (var index = 0; index < images.Count; index++)
        {
            var source = plainHeaders.AsSpan(index * ImageHeaderSize, ImageHeaderSize);
            var target = container.AsSpan(HeaderSize + (index * ImageHeaderSize), ImageHeaderSize);
            source.CopyTo(target);
            BinaryPrimitives.WriteUInt32LittleEndian(target[64..], Transform(BinaryPrimitives.ReadUInt32LittleEndian(source[64..]), index, key));
            BinaryPrimitives.WriteUInt32LittleEndian(target[68..], Transform(BinaryPrimitives.ReadUInt32LittleEndian(source[68..]), index, key));
            images[index].Payload.CopyTo(container, offsets[index]);
        }

        return container;
    }

    /// <summary>Computes the checksum as a bitwise CRC-16/CCITT-FALSE over the same bytes the container covers.
    /// <para>对容器覆盖的同一批字节按位计算 CRC-16/CCITT-FALSE 校验和。</para>
    /// </summary>
    /// <param name="segments">Byte segments folded in order.<para>按顺序并入的字节段。</para></param>
    /// <returns>The 16-bit checksum before the version-minor addition.
    /// <para>加入次版本字节之前的 16 位校验和。</para></returns>
    public static ushort BitwiseCrc16(params byte[][] segments)
    {
        ushort crc = 0;
        foreach (var segment in segments)
        {
            foreach (var value in segment)
            {
                crc = (ushort)(crc ^ (value << 8));
                for (var bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
                }
            }
        }

        return crc;
    }

    private static uint Accumulate(uint crc, ReadOnlySpan<byte> data)
    {
        unchecked
        {
            foreach (var value in data)
            {
                crc = (crc << 8) ^ Table[(byte)(crc >> 8) ^ value];
            }
        }

        return crc;
    }

    private static ushort[] BuildTable()
    {
        var table = new ushort[256];
        for (var index = 0; index < table.Length; index++)
        {
            ushort crc = (ushort)(index << 8);
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
            }

            table[index] = crc;
        }

        return table;
    }

    private static uint DeriveKey(ReadOnlySpan<byte> header)
    {
        uint sum = 0;
        for (var index = 0; index < CorrectionWords.Length; index++)
        {
            sum += BinaryPrimitives.ReadUInt16LittleEndian(header[(4 + (index * 2))..]);
        }

        unchecked
        {
            var average = (ushort)((0x66666667UL * sum) >> 32 >> 1);
            var low = average - BinaryPrimitives.ReadUInt16LittleEndian(header[10..]);
            var high = average - BinaryPrimitives.ReadUInt16LittleEndian(header[12..]);
            return (uint)low | (uint)(high << 16);
        }
    }

    private static uint Transform(uint value, int index, uint key)
    {
        unchecked
        {
            var k0 = (byte)(index + (byte)key);
            var k1 = (byte)(index + (byte)(key >> 8));
            var k2 = (byte)(index + (byte)(key >> 16));
            var k3 = (byte)(index + (byte)(key >> 24));
            return (uint)(((byte)value) ^ k2)
                | (uint)(((byte)(value >> 8) ^ k3) << 8)
                | (uint)(((byte)(value >> 16) ^ k0) << 16)
                | (uint)(((byte)(value >> 24) ^ k1) << 24);
        }
    }

    private static void WriteString(Span<byte> field, string value)
    {
        field.Clear();
        var bytes = Encoding.UTF8.GetBytes(value);
        bytes.AsSpan(0, Math.Min(bytes.Length, field.Length)).CopyTo(field);
    }
}

/// <summary>Header text, version bytes and checksum variant for <see cref="CpbContainerBuilder"/>.
/// <para><see cref="CpbContainerBuilder"/> 使用的头部文本、版本字节与校验和变体。</para>
/// </summary>
public sealed record CpbContainerOptions
{
    /// <summary>Gets or sets the device model text written at offset 48.
    /// <para>获取或设置写入偏移 48 的机型文本。</para></summary>
    public string Model { get; init; } = "8730L";

    /// <summary>Gets or sets the hardware version text written at offset 80.
    /// <para>获取或设置写入偏移 80 的硬件版本文本。</para></summary>
    public string HardwareVersion { get; init; } = "P3";

    /// <summary>Gets or sets the ROM version text written at offset 96.
    /// <para>获取或设置写入偏移 96 的 ROM 版本文本。</para></summary>
    public string Version { get; init; } = "4.3.066.P3.140417.8730L";

    /// <summary>Gets or sets the ROM source text written at offset 160.
    /// <para>获取或设置写入偏移 160 的 ROM 来源文本。</para></summary>
    public string RomFrom { get; init; } = "D:\\image";

    /// <summary>Gets or sets the major version byte written at offset 2.
    /// <para>获取或设置写入偏移 2 的主版本字节。</para></summary>
    public byte VersionMajor { get; init; } = 0x01;

    /// <summary>Gets or sets the minor version byte written at offset 3.
    /// <para>获取或设置写入偏移 3 的次版本字节。</para></summary>
    public byte VersionMinor { get; init; } = 0x07;

    /// <summary>Gets or sets whether only the low 16 bits of the checksum are stored, as a producer with a
    /// 16-bit CRC register would write them.
    /// <para>获取或设置是否只存储校验和的低 16 位，即 CRC 寄存器为 16 位的产出方所写入的形式。</para></summary>
    public bool StoreMaskedChecksum { get; init; }
}