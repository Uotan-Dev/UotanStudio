using System.Buffers.Binary;
using System.Text;
using UotanStudio.Hash.Crc32Ieee;

namespace UotanStudio.Tests.TestInfrastructure;

/// <summary>
/// Builds a minimal but structurally faithful Allwinner SD-card image for fixtures: a protective MBR, a GPT pair
/// whose first usable block sits past the boot region, an eGON first-stage loader at LBA 16, an optional
/// <c>sunxi-package</c> boot package and one payload per requested partition.
/// <para>为夹具构建最小但结构真实的全志 SD 卡镜像：保护性 MBR、首个可用块位于引导区之后的 GPT 主备对、
/// LBA 16 处的 eGON 一级引导程序、可选的 <c>sunxi-package</c> 启动包，以及每个请求分区各一份负载。</para>
/// </summary>
internal static class SunxiCardImageBuilder
{
    /// <summary>Logical block size of the built image. <para>构建镜像的逻辑块大小。</para></summary>
    public const int BlockSize = 512;

    /// <summary>Byte offset the boot ROM reads the first-stage loader from. <para>引导 ROM 读取一级引导程序的字节偏移。</para></summary>
    public const int EgonOffset = 0x2000;

    /// <summary>Byte length of the first-stage loader slot. <para>一级引导程序槽位的字节长度。</para></summary>
    public const int EgonLength = 0x10000;

    /// <summary>First usable logical block of the built GPT. <para>所构建 GPT 的首个可用逻辑块。</para></summary>
    public const int FirstUsableLba = 34816;

    /// <summary>Block the boot package is written at. <para>启动包写入的块。</para></summary>
    public const int BootPackageBlock = 32800;

    /// <summary>One payload the image must carry. <para>镜像必须携带的一份负载。</para></summary>
    /// <param name="Name">GPT partition name. <para>GPT 分区名。</para></param>
    /// <param name="Sectors">Partition length in sectors. <para>分区长度（扇区）。</para></param>
    /// <param name="Content">Bytes written at the partition start. <para>写入分区起始处的字节。</para></param>
    public readonly record struct Partition(string Name, int Sectors, byte[] Content);

    /// <summary>Builds the image and returns its bytes. <para>构建镜像并返回其字节。</para></summary>
    /// <param name="partitions">Partitions in table order; each starts where the previous one ends.
    /// <para>按表顺序排列的分区；每个分区从上一个结束处开始。</para></param>
    /// <returns>The image bytes. <para>镜像字节。</para></returns>
    public static byte[] Build(params Partition[] partitions)
    {
        var totalSectors = FirstUsableLba + partitions.Sum(partition => partition.Sectors) + 64;
        var image = new byte[totalSectors * BlockSize];

        image[510] = 0x55;
        image[511] = 0xAA;
        image[446 + 4] = 0xEE;
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(446 + 8), 1);

        WriteEgon(image.AsSpan(EgonOffset, EgonLength));
        WriteBootPackage(image.AsSpan(BootPackageBlock * BlockSize));

        var cursor = FirstUsableLba;
        var layout = new List<(string Name, int First, int Sectors)>();
        foreach (var partition in partitions)
        {
            layout.Add((partition.Name, cursor, partition.Sectors));
            partition.Content.CopyTo(image, cursor * BlockSize);
            cursor += partition.Sectors;
        }

        WriteGpt(image, totalSectors, layout);
        return image;
    }

    private static void WriteEgon(Span<byte> slot)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(slot, 0x3C80006F);
        "eGON.BT0"u8.CopyTo(slot[4..]);
        BinaryPrimitives.WriteUInt32LittleEndian(slot[12..], 0x191DABB2);
        BinaryPrimitives.WriteUInt32LittleEndian(slot[16..], 0x10000);
        BinaryPrimitives.WriteUInt32LittleEndian(slot[20..], 0x00000030);
        BinaryPrimitives.WriteUInt32LittleEndian(slot[32..], 0x20000);
        Encoding.ASCII.GetBytes("4.0\0").CopyTo(slot[48..]);
    }

    private static void WriteBootPackage(Span<byte> package)
    {
        "sunxi-package"u8.CopyTo(package);
        BinaryPrimitives.WriteUInt32LittleEndian(package[16..], 0x89119800);
        BinaryPrimitives.WriteUInt32LittleEndian(package[20..], 0xA78ACD0B);
        BinaryPrimitives.WriteUInt32LittleEndian(package[32..], 3);
        BinaryPrimitives.WriteUInt32LittleEndian(package[36..], 0xFC000);

        WritePackageItem(package, 0, "opensbi", 0x800, 0x400, 0x0, "OpenSBI v0.9"u8);
        WritePackageItem(package, 1, "u-boot", 0xC00, 0x800, 0x42000000, "U-Boot 2018.05"u8);
        WritePackageItem(package, 2, "dtb", 0x1400, 0x100, 0x0, (ReadOnlySpan<byte>)[0xD0, 0x0D, 0xFE, 0xED]);
    }

    private static void WritePackageItem(Span<byte> package, int index, string name, uint offset, uint length,
        uint runningAddress, ReadOnlySpan<byte> payload)
    {
        var record = package.Slice(0x3C + (index * 0x170), 0x170);
        "MIE;"u8.CopyTo(record);
        Encoding.ASCII.GetBytes(name).CopyTo(record[4..]);
        BinaryPrimitives.WriteUInt32LittleEndian(record[0x44..], offset);
        BinaryPrimitives.WriteUInt32LittleEndian(record[0x48..], length);
        BinaryPrimitives.WriteUInt32LittleEndian(record[0x50..], 3);
        BinaryPrimitives.WriteUInt32LittleEndian(record[0x54..], runningAddress);
        payload.CopyTo(package[(int)offset..]);
    }

    private static void WriteGpt(Span<byte> image, int totalSectors,
        IReadOnlyList<(string Name, int First, int Sectors)> layout)
    {
        const int entryCapacity = 128;
        const int entrySize = 128;
        var entries = image.Slice(2048 * BlockSize, entryCapacity * entrySize);
        for (var index = 0; index < layout.Count; index++)
        {
            var (name, first, sectors) = layout[index];
            var entry = entries.Slice(index * entrySize, entrySize);
            new Guid("0fc63daf-8483-4772-8e79-3d69d8477de4").TryWriteBytes(entry, bigEndian: false, out _);
            new Guid(0x1000 + index, 0x2000, 0x3000, 0x40, 0x50, 0x60, 0x70, 0x80, 0x90, 0xA0, 0xB0)
                .TryWriteBytes(entry[16..], bigEndian: false, out _);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[32..], (ulong)first);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[40..], (ulong)(first + sectors - 1));
            Encoding.Unicode.GetBytes(name).CopyTo(entry[56..]);
        }

        ulong lastUsable = (ulong)totalSectors - 34;
        var entriesCrc = Crc32Ieee.Compute(entries);
        WriteHeader(image.Slice(BlockSize, BlockSize), 1, (ulong)totalSectors - 1, lastUsable, 2048, entriesCrc);
        WriteHeader(image.Slice((totalSectors - 1) * BlockSize, BlockSize), (ulong)totalSectors - 1, 1, lastUsable,
            (ulong)totalSectors - 33, entriesCrc);
        entries.CopyTo(image.Slice((totalSectors - 33) * BlockSize, entryCapacity * entrySize));
    }

    private static void WriteHeader(Span<byte> header, ulong myLba, ulong alternateLba, ulong lastUsable,
        ulong entriesLba, uint entriesCrc)
    {
        const int entryCapacity = 128;
        const int entrySize = 128;
        "EFI PART"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], 0x00010000);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], 92);
        BinaryPrimitives.WriteUInt64LittleEndian(header[24..], myLba);
        BinaryPrimitives.WriteUInt64LittleEndian(header[32..], alternateLba);
        BinaryPrimitives.WriteUInt64LittleEndian(header[40..], FirstUsableLba);
        BinaryPrimitives.WriteUInt64LittleEndian(header[48..], lastUsable);
        new Guid("15536006-38b1-315b-87dd-ba2df3034625").TryWriteBytes(header[56..], bigEndian: false, out _);
        BinaryPrimitives.WriteUInt64LittleEndian(header[72..], entriesLba);
        BinaryPrimitives.WriteUInt32LittleEndian(header[80..], entryCapacity);
        BinaryPrimitives.WriteUInt32LittleEndian(header[84..], entrySize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[88..], entriesCrc);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], Crc32Ieee.Compute(header[..92]));
    }
}