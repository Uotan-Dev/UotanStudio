using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.Core;

namespace UotanStudio.Firmware.FileSystem.Fat;

public static partial class FatBuilder
{
    private const int CopyChunkBytes = 81_920;
    private static ReadOnlySpan<byte> SelfEntryName => ".          "u8;
    private static ReadOnlySpan<byte> ParentEntryName => "..         "u8;

    /// <summary>
    /// Writes the planned volume: boot record, FSInfo and backup copies, both allocation tables, every directory
    /// and then every file, mapping all writes through the planned cluster chains.
    /// <para>写出已规划的卷：引导记录、FSInfo 与备份副本、两份分配表、每个目录，最后是每个文件；所有写入都
    /// 经已规划的簇链映射。</para>
    /// </summary>
    private static void Write(FileStream stream, FatPlan plan,
        IProgress<(long Processed, long Total)>? progress, CancellationToken cancellationToken)
    {
        var layout = plan.Layout;
        cancellationToken.ThrowIfCancellationRequested();
        stream.SetLength(layout.VolumeBytes);
        var bootSector = BuildBootSector(layout);
        stream.Position = 0;
        stream.Write(bootSector);
        if (layout.Variant == FatVariant.Fat32)
        {
            var fsInfo = BuildFsInfo(layout, plan);
            stream.Position = layout.BytesPerSector;
            stream.Write(fsInfo);
            stream.Position = 6L * layout.BytesPerSector;
            stream.Write(bootSector);
            stream.Position = 7L * layout.BytesPerSector;
            stream.Write(fsInfo);
        }

        WriteAllocationTables(stream, plan);

        var throttle = new ProgressThrottle(plan.DataBytes, progress, cancellationToken);
        throttle.Report((0, plan.DataBytes));
        var processed = 0L;
        foreach (var directory in plan.Directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var image = RenderDirectory(directory, layout);
            if (directory.Parent is null && layout.Variant != FatVariant.Fat32)
            {
                stream.Position = layout.RootStart;
                stream.Write(image, 0, image.Length);
            }
            else
            {
                WriteClusters(stream, layout, directory.Clusters, image);
            }
        }

        foreach (var file in plan.Files)
        {
            CopyFile(stream, layout, file, plan.DataBytes, cancellationToken, ref processed, throttle);
        }

        throttle.Complete();
    }

    private static byte[] BuildBootSector(Layout layout)
    {
        var boot = new byte[Math.Max(layout.BytesPerSector, 512)];
        if (layout.Variant == FatVariant.Fat32)
        {
            boot[0] = 0xEB;
            boot[1] = 0x58;
            boot[2] = 0x90;
            WriteOemName(boot, layout.OemName);
            BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(11, 2), (ushort)layout.BytesPerSector);
            boot[13] = (byte)layout.SectorsPerCluster;
            BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(14, 2), (ushort)layout.ReservedSectors);
            boot[16] = FatCount;
            BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(17, 2), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(19, 2), 0);
            boot[21] = layout.MediaDescriptor;
            BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(22, 2), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(24, 2), 63);
            BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(26, 2), 255);
            BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(28, 4), (uint)layout.HiddenSectors);
            BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(32, 4), (uint)layout.TotalSectors);
            BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(36, 4), (uint)layout.SectorsPerFat);
            BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(40, 2), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(42, 2), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(44, 4), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(48, 2), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(50, 2), 6);
            boot[64] = 0x80;
            boot[66] = 0x29;
            BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(67, 4), 0x554F5441);
            WriteVolumeLabel(boot, 71, layout.VolumeLabel);
            Encoding.ASCII.GetBytes("FAT32   ").CopyTo(boot, 82);
        }
        else
        {
            boot[0] = 0xEB;
            boot[1] = 0x3C;
            boot[2] = 0x90;
            WriteOemName(boot, layout.OemName);
            BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(11, 2), (ushort)layout.BytesPerSector);
            boot[13] = (byte)layout.SectorsPerCluster;
            BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(14, 2), (ushort)layout.ReservedSectors);
            boot[16] = FatCount;
            BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(17, 2), (ushort)layout.RootDirectoryEntries);
            BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(19, 2),
                layout.TotalSectors < 65_536 ? (ushort)layout.TotalSectors : (ushort)0);
            boot[21] = layout.MediaDescriptor;
            BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(22, 2), (ushort)layout.SectorsPerFat);
            BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(24, 2), 63);
            BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(26, 2), 255);
            BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(28, 4), (uint)layout.HiddenSectors);
            BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(32, 4),
                layout.TotalSectors >= 65_536 ? (uint)layout.TotalSectors : 0);
            boot[36] = 0x80;
            boot[38] = 0x29;
            BinaryPrimitives.WriteUInt32LittleEndian(boot.AsSpan(39, 4), 0x554F5441);
            WriteVolumeLabel(boot, 43, layout.VolumeLabel);
            Encoding.ASCII.GetBytes(layout.VariantName.PadRight(8)).CopyTo(boot, 54);
        }

        boot[510] = 0x55;
        boot[511] = 0xAA;
        return boot;
    }

    private static void WriteOemName(byte[] boot, string oemName)
    {
        var bytes = Encoding.ASCII.GetBytes(oemName.PadRight(8, ' '));
        bytes.AsSpan(0, 8).CopyTo(boot.AsSpan(3, 8));
    }

    private static void WriteVolumeLabel(byte[] boot, int offset, string label)
    {
        var bytes = ArrayPool<byte>.Shared.Rent(11);
        try
        {
            Array.Fill(bytes, (byte)' ', 0, 11);
            Encoding.ASCII.GetBytes(label).CopyTo(bytes, 0);
            bytes.AsSpan(0, 11).CopyTo(boot.AsSpan(offset, 11));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    private static byte[] BuildFsInfo(Layout layout, FatPlan plan)
    {
        var info = new byte[layout.BytesPerSector];
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(0, 4), 0x41615252);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(484, 4), 0x61417272);
        var allocated = CountAllocated(plan);
        var free = Math.Max(0L, layout.ClusterCount - allocated);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(488, 4), (uint)free);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(492, 4),
            free == 0 ? 0xFFFFFFFFu : (uint)(2 + allocated));
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(508, 4), 0xAA550000);
        return info;
    }

    private static long CountAllocated(FatPlan plan)
    {
        long allocated = 0;
        foreach (var directory in plan.Directories)
        {
            allocated += directory.Clusters.Count;
        }

        foreach (var file in plan.Files)
        {
            allocated += file.Clusters.Count;
        }

        return allocated;
    }

    private static void WriteAllocationTables(FileStream stream, FatPlan plan)
    {
        var layout = plan.Layout;
        var fat = new byte[checked(layout.SectorsPerFat * layout.BytesPerSector)];
        switch (layout.Variant)
        {
            case FatVariant.Fat12:
                SetEntry(fat, FatVariant.Fat12, 0, 0xF00 | layout.MediaDescriptor);
                SetEntry(fat, FatVariant.Fat12, 1, 0xFFF);
                break;
            case FatVariant.Fat16:
                SetEntry(fat, FatVariant.Fat16, 0, 0xFF00 | layout.MediaDescriptor);
                SetEntry(fat, FatVariant.Fat16, 1, 0xFFFF);
                break;
            default:
                SetEntry(fat, FatVariant.Fat32, 0, 0x0FFFFFF8);
                SetEntry(fat, FatVariant.Fat32, 1, 0x0FFFFFFF);
                break;
        }

        foreach (var directory in plan.Directories)
        {
            Chain(fat, layout.Variant, directory.Clusters);
        }

        foreach (var file in plan.Files)
        {
            Chain(fat, layout.Variant, file.Clusters);
        }

        for (var copy = 0; copy < FatCount; copy++)
        {
            stream.Position = layout.FatStart + (long)copy * layout.SectorsPerFat * layout.BytesPerSector;
            stream.Write(fat, 0, fat.Length);
        }
    }

    private static void Chain(byte[] fat, FatVariant variant, List<int> clusters)
    {
        var end = variant switch
        {
            FatVariant.Fat12 => 0xFFF,
            FatVariant.Fat16 => 0xFFFF,
            _ => 0x0FFFFFFF,
        };
        for (var index = 0; index < clusters.Count; index++)
        {
            SetEntry(fat, variant, clusters[index], index == clusters.Count - 1 ? end : clusters[index + 1]);
        }
    }

    private static void SetEntry(byte[] fat, FatVariant variant, int cluster, int value)
    {
        switch (variant)
        {
            case FatVariant.Fat12:
                var position = cluster + cluster / 2;
                if (position + 1 >= fat.Length)
                {
                    throw new InvalidDataException("The FAT12 allocation table cannot address the planned cluster.");
                }

                if ((cluster & 1) == 0)
                {
                    fat[position] = (byte)value;
                    fat[position + 1] = (byte)(fat[position + 1] & 0xF0 | (value >> 8) & 0x0F);
                }
                else
                {
                    fat[position] = (byte)(fat[position] & 0x0F | (value & 0x0F) << 4);
                    fat[position + 1] = (byte)(value >> 4 & 0xFF);
                }

                break;
            case FatVariant.Fat16:
                if ((long)cluster * 2 + 2 > fat.Length)
                {
                    throw new InvalidDataException("The FAT16 allocation table cannot address the planned cluster.");
                }

                BinaryPrimitives.WriteUInt16LittleEndian(fat.AsSpan(cluster * 2, 2), (ushort)value);
                break;
            default:
                if ((long)cluster * 4 + 4 > fat.Length)
                {
                    throw new InvalidDataException("The FAT32 allocation table cannot address the planned cluster.");
                }

                BinaryPrimitives.WriteUInt32LittleEndian(fat.AsSpan(cluster * 4, 4), (uint)value);
                break;
        }
    }

    private static byte[] RenderDirectory(FatNode directory, Layout layout)
    {
        var image = new byte[directory.DirectoryBytes];
        var offset = 0;
        if (directory.Parent is null)
        {
            if (layout.VolumeLabel.Length > 0)
            {
                WriteVolumeLabelEntry(image.AsSpan(offset, DirectoryEntryBytes), layout.VolumeLabel);
                offset += DirectoryEntryBytes;
            }
        }
        else
        {
            var self = directory.Clusters.Count > 0 ? directory.Clusters[0] : 0;
            var parent = directory.Parent.Clusters.Count > 0 ? directory.Parent.Clusters[0] : 0;
            if (layout.Variant != FatVariant.Fat32 && directory.Parent.Parent is null)
            {
                parent = 0;
            }

            var timestamp = DosTimestamp(directory.LastWriteTimeUtc);
            WriteShortEntry(image.AsSpan(offset, DirectoryEntryBytes), SelfEntryName, 0x10, timestamp, self, 0);
            offset += DirectoryEntryBytes;
            WriteShortEntry(image.AsSpan(offset, DirectoryEntryBytes), ParentEntryName, 0x10, timestamp, parent, 0);
            offset += DirectoryEntryBytes;
        }

        foreach (var child in directory.Children)
        {
            foreach (var longName in child.LongNameEntries)
            {
                longName.CopyTo(image, offset);
                offset += DirectoryEntryBytes;
            }

            var cluster = child.Clusters.Count > 0 ? child.Clusters[0] : 0;
            WriteShortEntry(image.AsSpan(offset, DirectoryEntryBytes), child.ShortName,
                AttributeByte(child.Attributes, child.IsDirectory), DosTimestamp(child.LastWriteTimeUtc), cluster,
                child.Length);
            offset += DirectoryEntryBytes;
        }

        return image;
    }

    private static void WriteVolumeLabelEntry(Span<byte> entry, string label)
    {
        entry[..11].Fill((byte)' ');
        Encoding.ASCII.GetBytes(label).CopyTo(entry[..11]);
        entry[11] = 0x08;
    }

    private static void WriteShortEntry(Span<byte> entry, ReadOnlySpan<byte> name, byte attributes,
        (ushort Date, ushort Time) timestamp, int cluster, long length)
    {
        entry[..11].Fill((byte)' ');
        name[..Math.Min(name.Length, 11)].CopyTo(entry[..11]);
        entry[11] = attributes;
        entry[12] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(entry[14..16], timestamp.Time);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[16..18], timestamp.Date);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[18..20], timestamp.Date);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[20..22], (ushort)(cluster >> 16));
        BinaryPrimitives.WriteUInt16LittleEndian(entry[22..24], timestamp.Time);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[24..26], timestamp.Date);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[26..28], (ushort)(cluster & 0xFFFF));
        BinaryPrimitives.WriteUInt32LittleEndian(entry[28..32], (uint)length);
    }

    private static void WriteClusters(FileStream stream, Layout layout, List<int> clusters, ReadOnlySpan<byte> payload)
    {
        var written = 0;
        foreach (var cluster in clusters)
        {
            if (written >= payload.Length)
            {
                return;
            }

            stream.Position = layout.DataStart + (long)(cluster - 2) * layout.ClusterBytes;
            var take = (int)Math.Min(layout.ClusterBytes, payload.Length - written);
            stream.Write(payload.Slice(written, take));
            written += take;
        }
    }

    private static void CopyFile(FileStream stream, Layout layout, FatNode file, long total,
        CancellationToken cancellationToken, ref long processed, ProgressThrottle throttle)
    {
        if (file.Clusters.Count == 0)
        {
            return;
        }

        using var input = new FileStream(file.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            CopyChunkBytes, FileOptions.SequentialScan);
        if (input.Length < file.Length)
        {
            throw new IOException(
                $"The file '{file.SourcePath}' shrank to {input.Length} bytes while the FAT image was being built.");
        }

        var buffer = ArrayPool<byte>.Shared.Rent(CopyChunkBytes);
        try
        {
            var remaining = file.Length;
            foreach (var cluster in file.Clusters)
            {
                if (remaining == 0)
                {
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                stream.Position = layout.DataStart + (long)(cluster - 2) * layout.ClusterBytes;
                var space = Math.Min(layout.ClusterBytes, remaining);
                while (space > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var take = (int)Math.Min(buffer.Length, space);
                    var read = input.Read(buffer, 0, take);
                    if (read <= 0)
                    {
                        throw new IOException(
                            $"The file '{file.SourcePath}' ended after {file.Length - remaining} of {file.Length} bytes.");
                    }

                    stream.Write(buffer, 0, read);
                    space -= read;
                    remaining -= read;
                    processed = checked(processed + read);
                    throttle.Report((processed, total));
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}