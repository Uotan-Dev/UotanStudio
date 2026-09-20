using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Services.FileManagement;

/// <summary>Bounded reader for FAT12/FAT16/FAT32 disk images.</summary>
internal sealed class Fat12ImageParser : IDisposable
{
    private readonly FileStream _stream;
    private readonly int _bytesPerSector;
    private readonly int _sectorsPerCluster;
    private readonly int _reservedSectors;
    private readonly int _fatCount;
    private readonly int _sectorsPerFat;
    private readonly int _rootEntries;
    private readonly int _fatBits;
    private readonly int _rootCluster;
    private readonly long _rootOffset;
    private readonly long _dataOffset;
    private readonly int _clusterSize;
    private readonly byte[] _fat;

    private Fat12ImageParser(string path, FileStream stream, int bytesPerSector, int sectorsPerCluster,
        int reservedSectors, int fatCount, int sectorsPerFat, int rootEntries, int fatBits, int rootCluster,
        long rootOffset, long dataOffset, byte[] fat)
    {
        _stream = stream;
        _bytesPerSector = bytesPerSector;
        _sectorsPerCluster = sectorsPerCluster;
        _reservedSectors = reservedSectors;
        _fatCount = fatCount;
        _sectorsPerFat = sectorsPerFat;
        _rootEntries = rootEntries;
        _fatBits = fatBits;
        _rootCluster = rootCluster;
        _rootOffset = rootOffset;
        _dataOffset = dataOffset;
        _clusterSize = checked(bytesPerSector * sectorsPerCluster);
        _fat = fat;
        Entries = ReadEntries();
    }

    internal IReadOnlyList<ImageBrowserEntry> Entries { get; }
    internal string FormatName => $"FAT{_fatBits}";

    internal static bool IsFat12(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> boot = stackalloc byte[512];
            if (stream.Read(boot) != boot.Length || boot[510] != 0x55 || boot[511] != 0xAA) return false;
            var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot[11..]);
            var sectorsPerCluster = boot[13];
            var rootEntries = BinaryPrimitives.ReadUInt16LittleEndian(boot[17..]);
            var sectorsPerFat = BinaryPrimitives.ReadUInt16LittleEndian(boot[22..]);
            var fat32 = rootEntries == 0 && sectorsPerFat == 0
                && Encoding.ASCII.GetString(boot[82..90]).Trim() == "FAT32";
            return boot[16] > 0 && bytesPerSector is >= 512 and <= 4096
                && sectorsPerCluster is > 0 and <= 128 && sectorsPerFat > 0
                && (Encoding.ASCII.GetString(boot[54..62]).Trim() is "FAT12" or "FAT16" || fat32);
        }
        catch { return false; }
    }

    internal static Fat12ImageParser Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        try
        {
            Span<byte> boot = stackalloc byte[512];
            stream.ReadExactly(boot);
            if (boot[510] != 0x55 || boot[511] != 0xAA) throw new InvalidDataException("Invalid FAT boot signature.");
            var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot[11..]);
            var sectorsPerCluster = boot[13];
            var reserved = BinaryPrimitives.ReadUInt16LittleEndian(boot[14..]);
            var fatCount = boot[16];
            var rootEntries = BinaryPrimitives.ReadUInt16LittleEndian(boot[17..]);
            var fat16Sectors = BinaryPrimitives.ReadUInt16LittleEndian(boot[22..]);
            var fat32Sectors = BinaryPrimitives.ReadUInt32LittleEndian(boot[36..]);
            var sectorsPerFat = fat16Sectors != 0 ? fat16Sectors : checked((int)fat32Sectors);
            var fatBits = rootEntries == 0 && fat16Sectors == 0 ? 32 :
                Encoding.ASCII.GetString(boot[54..62]).Trim() == "FAT16" ? 16 : 12;
            var rootCluster = fatBits == 32 ? checked((int)BinaryPrimitives.ReadUInt32LittleEndian(boot[44..])) : 0;
            var rootOffset = checked((long)reserved * bytesPerSector + (long)fatCount * sectorsPerFat * bytesPerSector);
            var rootBytes = fatBits == 32 ? 0 : checked(rootEntries * 32L);
            var dataOffset = checked(rootOffset + rootBytes);
            var fatBytes = checked(sectorsPerFat * bytesPerSector);
            var fat = new byte[fatBytes];
            stream.Position = (long)reserved * bytesPerSector;
            stream.ReadExactly(fat);
            return new Fat12ImageParser(path, stream, bytesPerSector, sectorsPerCluster, reserved, fatCount,
                sectorsPerFat, rootEntries, fatBits, rootCluster, rootOffset, dataOffset, fat);
        }
        catch { stream.Dispose(); throw; }
    }

    internal void ReadFile(string path, Stream output)
    {
        var entry = Entries.FirstOrDefault(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (entry is null || entry.Kind != ImageBrowserEntryKind.File) throw new FileNotFoundException(path);
        var cluster = FindCluster(path);
        var remaining = entry.Size;
        var buffer = new byte[_clusterSize];
        while (cluster >= 2 && remaining > 0)
        {
            _stream.Position = checked(_dataOffset + (long)(cluster - 2) * _clusterSize);
            var count = checked((int)Math.Min(remaining, buffer.Length));
            _stream.ReadExactly(buffer.AsSpan(0, count));
            output.Write(buffer, 0, count);
            remaining -= count;
            cluster = NextCluster(cluster);
        }
    }

    private IReadOnlyList<ImageBrowserEntry> ReadEntries()
    {
        var result = new List<ImageBrowserEntry>();
        ReadDirectory(0, string.Empty, result, 0);
        return result;
    }

    private void ReadDirectory(int cluster, string prefix, List<ImageBrowserEntry> result, int depth)
    {
        if (depth > 32) throw new InvalidDataException("FAT directory nesting exceeds the safety limit.");
        if (cluster == 0 && _fatBits == 32) cluster = _rootCluster;
        var bytes = cluster == 0 ? checked(_rootEntries * 32) : _clusterSize;
        var offset = cluster == 0 ? _rootOffset : checked(_dataOffset + (long)(cluster - 2) * _clusterSize);
        var data = new byte[bytes];
        _stream.Position = offset;
        _stream.ReadExactly(data);
        for (var i = 0; i + 32 <= data.Length; i += 32)
        {
            if (data[i] == 0) break;
            if (data[i] == 0xE5 || data[i + 11] == 0x0F || (data[i + 11] & 0x08) != 0) continue;
            var name = DecodeName(data.AsSpan(i, 11));
            if (name is "." or "..") continue;
            var child = string.IsNullOrEmpty(prefix) ? name : $"{prefix}/{name}";
            var attributes = data[i + 11];
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i + 28, 4));
            var firstCluster = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i + 26, 2));
            var directory = (attributes & 0x10) != 0;
            result.Add(new ImageBrowserEntry(child, directory ? 0 : size,
                directory ? ImageBrowserEntryKind.Directory : ImageBrowserEntryKind.File));
            if (directory && firstCluster >= 2) ReadDirectory(firstCluster, child, result, depth + 1);
        }
    }

    private int FindCluster(string path)
    {
        var name = path.Split('/').Last();
        var cluster = 0;
        var current = string.Empty;
        foreach (var part in path.Split('/'))
        {
            current = string.IsNullOrEmpty(current) ? part : $"{current}/{part}";
            cluster = FindDirectoryEntryCluster(current);
        }
        return cluster;
    }

    private int FindDirectoryEntryCluster(string path)
    {
        var entry = Entries.First(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        var parent = path.Contains('/') ? path[..path.LastIndexOf('/')] : string.Empty;
        var target = path[(path.LastIndexOf('/') + 1)..];
        var bytes = string.IsNullOrEmpty(parent) ? checked(_rootEntries * 32) : _clusterSize;
        var cluster = string.IsNullOrEmpty(parent) ? 0 : FindCluster(parent);
        var offset = cluster == 0 ? _rootOffset : checked(_dataOffset + (long)(cluster - 2) * _clusterSize);
        var data = new byte[bytes];
        _stream.Position = offset;
        _stream.ReadExactly(data);
        for (var i = 0; i + 32 <= data.Length; i += 32)
        {
            if (DecodeName(data.AsSpan(i, 11)).Equals(target, StringComparison.OrdinalIgnoreCase))
                return BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i + 26, 2));
        }
        return 0;
    }

    private int NextCluster(int cluster)
    {
        var offset = _fatBits switch { 12 => cluster + cluster / 2, 16 => cluster * 2, _ => cluster * 4 };
        var value = BinaryPrimitives.ReadUInt32LittleEndian(_fat.AsSpan(offset, 4));
        return _fatBits switch
        {
            12 => (int)((cluster & 1) == 0 ? value & 0x0FFF : value >> 4),
            16 => (int)(value & 0xFFFF),
            _ => (int)(value & 0x0FFFFFFF)
        };
    }

    private static string DecodeName(ReadOnlySpan<byte> entry)
    {
        var baseName = Encoding.ASCII.GetString(entry[..8]).TrimEnd(' ');
        var extension = Encoding.ASCII.GetString(entry.Slice(8, 3)).TrimEnd(' ');
        return extension.Length == 0 ? baseName : $"{baseName}.{extension}";
    }

    public void Dispose() => _stream.Dispose();
}