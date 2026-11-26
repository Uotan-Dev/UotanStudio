using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Tests.Firmware;

internal static class MtkTestImageBuilder
{
    public static byte[] BuildLkImage(params (string Name, byte[] Data, uint Address, byte Group, bool Last)[] entries)
    {
        var total = 0;
        foreach (var entry in entries)
        {
            total += 512 + entry.Data.Length;
            var pad = (16 - (total % 16)) % 16;
            total += pad;
        }

        var image = new byte[total];
        var offset = 0;
        foreach (var entry in entries)
        {
            var header = image.AsSpan(offset, 512);
            BinaryPrimitives.WriteUInt32LittleEndian(header, 0x58881688);
            BinaryPrimitives.WriteUInt32LittleEndian(header[0x04..], (uint)entry.Data.Length);
            Encoding.ASCII.GetBytes(entry.Name).CopyTo(header[0x08..]);
            BinaryPrimitives.WriteUInt32LittleEndian(header[0x28..], entry.Address);
            BinaryPrimitives.WriteUInt32LittleEndian(header[0x30..], 0x58891689);
            BinaryPrimitives.WriteUInt32LittleEndian(header[0x34..], 512);
            BinaryPrimitives.WriteUInt32LittleEndian(header[0x38..], 1);
            header[0x3C] = 0;
            header[0x3D] = 0;
            header[0x3E] = 0;
            header[0x3F] = entry.Group;
            BinaryPrimitives.WriteUInt32LittleEndian(header[0x40..], entry.Last ? 1u : 0u);
            BinaryPrimitives.WriteUInt32LittleEndian(header[0x44..], 0x10);
            entry.Data.CopyTo(image.AsSpan(offset + 512));
            offset += 512 + entry.Data.Length;
            offset += (16 - (offset % 16)) % 16;
        }

        return image;
    }

    public static byte[] BuildPreloader(byte[] code, byte lockState, long codeOffset = 0x800, int size = 4 * 1024 * 1024)
    {
        var region = new byte[size];
        "EMMC_BOOT"u8.CopyTo(region);
        region[0x20D] = (byte)(codeOffset / 256);
        code.CopyTo(region.AsSpan((int)codeOffset));
        var flag = Encoding.ASCII.GetBytes("AND_ROMINFO_v");
        const int flagBlockOffset = 0x1000;
        flag.CopyTo(region.AsSpan(flagBlockOffset));
        region[flagBlockOffset + 0x4C] = lockState;
        return region;
    }
}