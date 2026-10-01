using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Tests.Firmware.Fat;

internal sealed partial class FatImageBuilder
{
    private void WriteBootSector(Stream stream)
    {
        var boot = new byte[Math.Max(_bytesPerSector, 512)];
        boot[0] = 0xEB;
        boot[1] = 0x3C;
        boot[2] = 0x90;
        BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(11), (ushort)_bytesPerSector);
        boot[13] = (byte)_sectorsPerCluster;
        BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(14), (ushort)_reservedSectors);
        boot[16] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(17), (ushort)_rootEntries);
        BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(19), _totalSectors < 65_536 ? (ushort)_totalSectors : (ushort)0);
        boot[21] = 0xF8;
        BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(22), (ushort)(_variant == 32 ? 0 : _sectorsPerFat));
        BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(24), _variant == 32 ? (ushort)0 : (ushort)63);
        BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(26), _variant == 32 ? (ushort)0 : (ushort)255);
        BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(32), _totalSectors < 65_536 ? 0 : (uint)_totalSectors);
        if (_variant == 32)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(36), (uint)_sectorsPerFat);
            BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(42), 0x0001);
            BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(44), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(48), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(50), 6);
            boot[66] = 0x29;
            Encoding.ASCII.GetBytes("FAT32   ").CopyTo(boot, 82);
        }
        else
        {
            boot[38] = 0x29;
            Encoding.ASCII.GetBytes(_variant == 12 ? "FAT12   " : "FAT16   ").CopyTo(boot, 54);
        }

        boot[510] = 0x55;
        boot[511] = 0xAA;
        stream.Position = 0;
        stream.Write(boot);
    }

    private void WriteFsInfo(Stream stream)
    {
        var info = new byte[_bytesPerSector];
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(0), 0x41615252);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(484), 0x61417272);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(488), (uint)Math.Max(0, _clusterCount - _nextCluster + 2));
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(492), (uint)_nextCluster);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(508), 0xAA550000);
        stream.Position = _fatStart;
        stream.Write(info);
    }
}