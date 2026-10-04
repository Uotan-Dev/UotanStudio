using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// Minimal writer used by tests to synthesize valid OLE2/CFB version 3 containers (512-byte
/// sectors) with nested storages, regular streams, a mini stream and, for large files, a DIFAT
/// chain. Stream sector allocation round-robins across regular streams so FAT chains are
/// fragmented. Not a production type.
/// </summary>
internal sealed class CompoundFileBuilder
{
    public const uint EndOfChain = 0xFFFFFFFE;
    public const uint FreeSect = 0xFFFFFFFD;
    public const uint FatSect = 0xFFFFFFFC;
    public const uint NoStream = 0xFFFFFFFF;

    private const int SectorSize = 512;
    private const int MiniSize = 64;

    private sealed class Node
    {
        public Node(string name, byte[]? data) => (Name, Data) = (name, data);

        public string Name { get; }
        public byte[]? Data { get; }
        public List<Node> Children { get; } = [];
        public int FlatIndex = -1;
    }

    private sealed class FlatEntry
    {
        public string Name = string.Empty;
        public int Type;
        public byte[] Data = [];
        public uint Start = NoStream;
        public uint Size;
        public uint Left = NoStream;
        public uint Right = NoStream;
        public uint Child = NoStream;
    }

    private readonly Node _root = new("Root Entry", null);

    public void AddStream(string path, byte[] data)
    {
        var parts = path.Split('/');
        var node = _root;
        foreach (var part in parts[..^1])
        {
            var existing = node.Children.Find(child => child.Name == part && child.Data is null);
            if (existing is null)
            {
                existing = new Node(part, null);
                node.Children.Add(existing);
            }

            node = existing;
        }

        node.Children.Add(new Node(parts[^1], data));
    }

    public byte[] Build()
    {
        var entries = new List<FlatEntry>();
        Flatten(_root, entries);
        entries[0].Type = 5;

        var regularAllocations = new List<(int EntryIndex, int Chunks)>();
        var miniData = new List<byte>();
        var miniStreams = new List<(int EntryIndex, int MiniBlocks)>();
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry.Type != 2 || entry.Size == 0)
            {
                continue;
            }

            if (entry.Size >= 4096)
            {
                regularAllocations.Add((i, (int)((entry.Size + SectorSize - 1) / SectorSize)));
            }
            else
            {
                miniStreams.Add((i, (int)((entry.Size + MiniSize - 1) / MiniSize)));
                miniData.AddRange(entry.Data);
                while (miniData.Count % MiniSize != 0)
                {
                    miniData.Add(0);
                }
            }
        }

        var miniFatEntries = miniData.Count / MiniSize;
        var directoryChunks = (entries.Count * 128 + SectorSize - 1) / SectorSize;
        var miniDataChunks = (miniData.Count + SectorSize - 1) / SectorSize;
        var miniFatChunks = miniFatEntries > 0 ? (miniFatEntries * 4 + SectorSize - 1) / SectorSize : 0;
        var streamChunkTotal = regularAllocations.Sum(allocation => allocation.Chunks);
        var otherChunks = directoryChunks + miniDataChunks + miniFatChunks + streamChunkTotal;
        var fatChunks = 1;
        for (var i = 0; i < 16; i++)
        {
            var total = fatChunks + otherChunks;
            var required = (total * 4 + SectorSize - 1) / SectorSize;
            if (required == fatChunks)
            {
                break;
            }

            fatChunks = required;
        }

        var difatSectors = fatChunks > 109 ? (fatChunks - 109 + 126) / 127 : 0;
        var totalSectors = fatChunks + difatSectors + otherChunks;

        var fat = new uint[totalSectors];
        Array.Fill(fat, FreeSect);
        for (var i = 0; i < fatChunks; i++)
        {
            fat[i] = FatSect;
        }

        var nextSector = fatChunks;
        var difatSectorIds = new uint[difatSectors];
        for (var i = 0; i < difatSectors; i++)
        {
            difatSectorIds[i] = (uint)(nextSector + i);
        }

        nextSector += difatSectors;

        var miniFatSectorIds = new uint[miniFatChunks];
        for (var i = 0; i < miniFatChunks; i++)
        {
            miniFatSectorIds[i] = (uint)(nextSector + i);
        }

        nextSector += miniFatChunks;

        var directorySectorIds = new uint[directoryChunks];
        for (var i = 0; i < directoryChunks; i++)
        {
            directorySectorIds[i] = (uint)(nextSector + i);
        }

        nextSector += directoryChunks;

        var miniDataSectorIds = new uint[miniDataChunks];
        for (var i = 0; i < miniDataChunks; i++)
        {
            miniDataSectorIds[i] = (uint)(nextSector + i);
        }

        nextSector += miniDataChunks;

        var streamSectorIds = new uint[regularAllocations.Count][];
        var cursors = new int[regularAllocations.Count];
        for (var i = 0; i < regularAllocations.Count; i++)
        {
            streamSectorIds[i] = new uint[regularAllocations[i].Chunks];
        }

        var remaining = streamChunkTotal;
        while (remaining > 0)
        {
            for (var i = 0; i < regularAllocations.Count; i++)
            {
                if (cursors[i] < streamSectorIds[i].Length)
                {
                    streamSectorIds[i][cursors[i]++] = (uint)(nextSector++);
                    remaining--;
                }
            }
        }

        foreach (var chain in new[] { miniFatSectorIds, directorySectorIds, miniDataSectorIds })
        {
            LinkChain(fat, chain);
        }

        for (var i = 0; i < regularAllocations.Count; i++)
        {
            LinkChain(fat, streamSectorIds[i]);
            entries[regularAllocations[i].EntryIndex].Start = streamSectorIds[i][0];
        }

        var miniFat = new uint[Math.Max(miniFatEntries, 0)];
        Array.Fill(miniFat, FreeSect);
        var miniCursor = 0u;
        foreach (var (entryIndex, miniBlocks) in miniStreams)
        {
            for (var i = 0; i < miniBlocks; i++)
            {
                miniFat[miniCursor + i] = i + 1 < miniBlocks ? miniCursor + (uint)i + 1 : EndOfChain;
            }

            entries[entryIndex].Start = miniCursor;
            miniCursor += (uint)miniBlocks;
        }

        entries[0].Start = miniDataChunks > 0 ? miniDataSectorIds[0] : NoStream;
        entries[0].Size = (uint)miniData.Count;
        LinkSiblings(_root, entries);

        var file = new byte[(totalSectors + 1) * SectorSize];
        WriteHeader(file, fatChunks, difatSectorIds, miniFatSectorIds, directorySectorIds[0]);
        for (var i = 0; i < fatChunks; i++)
        {
            WriteFatSector(file, 1 + i, fat.AsSpan(i * 128, Math.Min(128, fat.Length - (i * 128))));
        }

        for (var i = 0; i < difatSectors; i++)
        {
            WriteDifatSector(file, 1 + fatChunks + i, fatChunks, 109 + (i * 127), i < difatSectors - 1 ? difatSectorIds[i + 1] : EndOfChain);
        }

        for (var i = 0; i < miniFatChunks; i++)
        {
            WriteFatSector(file, 1 + fatChunks + difatSectors + i, miniFat.AsSpan(i * 128, Math.Min(128, miniFat.Length - (i * 128))));
        }

        var directoryBytes = new byte[entries.Count * 128];
        for (var i = 0; i < entries.Count; i++)
        {
            WriteDirectoryEntry(directoryBytes.AsSpan(i * 128, 128), entries[i]);
        }

        for (var i = 0; i < directoryChunks; i++)
        {
            directoryBytes.AsSpan(i * SectorSize, Math.Min(SectorSize, directoryBytes.Length - (i * SectorSize)))
                .CopyTo(file.AsSpan((1 + (int)directorySectorIds[i]) * SectorSize, SectorSize));
        }

        if (miniDataChunks > 0)
        {
            miniData.CopyTo(file, (1 + (int)miniDataSectorIds[0]) * SectorSize);
        }

        for (var i = 0; i < regularAllocations.Count; i++)
        {
            var data = entries[regularAllocations[i].EntryIndex].Data;
            for (var c = 0; c < streamSectorIds[i].Length; c++)
            {
                var take = Math.Min(SectorSize, data.Length - (c * SectorSize));
                if (take > 0)
                {
                    data.AsSpan(c * SectorSize, take).CopyTo(file.AsSpan((1 + (int)streamSectorIds[i][c]) * SectorSize, take));
                }
            }
        }

        return file;
    }

    private static void LinkChain(uint[] fat, uint[] chain)
    {
        for (var i = 0; i < chain.Length; i++)
        {
            fat[chain[i]] = i + 1 < chain.Length ? chain[i + 1] : EndOfChain;
        }
    }

    private static void LinkSiblings(Node node, List<FlatEntry> entries)
    {
        var children = node.Children;
        for (var i = 0; i < children.Count; i++)
        {
            entries[children[i].FlatIndex].Right = i + 1 < children.Count ? (uint)children[i + 1].FlatIndex : NoStream;
        }

        if (children.Count > 0)
        {
            entries[node.FlatIndex].Child = (uint)children[0].FlatIndex;
        }

        foreach (var child in children)
        {
            if (child.Data is null)
            {
                LinkSiblings(child, entries);
            }
        }
    }

    private void Flatten(Node node, List<FlatEntry> entries)
    {
        node.FlatIndex = entries.Count;
        entries.Add(new FlatEntry
        {
            Name = node.Name,
            Type = node.Data is null ? 1 : 2,
            Data = node.Data ?? [],
            Size = (uint)(node.Data?.Length ?? 0)
        });
        foreach (var child in node.Children)
        {
            Flatten(child, entries);
        }
    }

    private static void WriteHeader(byte[] file, int fatChunks, uint[] difatSectorIds, uint[] miniFatSectorIds, uint firstDirectorySector)
    {
        Span<byte> header = file.AsSpan(0, SectorSize);
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header[24..], 0x3E);
        BinaryPrimitives.WriteUInt16LittleEndian(header[26..], 3);
        BinaryPrimitives.WriteUInt16LittleEndian(header[28..], 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(header[30..], 9);
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..], 6);
        BinaryPrimitives.WriteUInt32LittleEndian(header[44..], (uint)fatChunks);
        BinaryPrimitives.WriteUInt32LittleEndian(header[48..], firstDirectorySector);
        BinaryPrimitives.WriteUInt32LittleEndian(header[56..], 4096);
        BinaryPrimitives.WriteUInt32LittleEndian(header[60..], miniFatSectorIds.Length > 0 ? miniFatSectorIds[0] : EndOfChain);
        BinaryPrimitives.WriteUInt32LittleEndian(header[64..], (uint)miniFatSectorIds.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[68..], difatSectorIds.Length > 0 ? difatSectorIds[0] : EndOfChain);
        BinaryPrimitives.WriteUInt32LittleEndian(header[72..], (uint)difatSectorIds.Length);
        for (var i = 0; i < 109; i++)
        {
            var value = i < fatChunks ? (uint)i : FreeSect;
            BinaryPrimitives.WriteUInt32LittleEndian(header[(76 + (4 * i))..], value);
        }
    }

    private static void WriteFatSector(byte[] file, int fileSector, ReadOnlySpan<uint> entries)
    {
        Span<byte> sector = file.AsSpan(fileSector * SectorSize, SectorSize);
        for (var i = 0; i < entries.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(sector[(4 * i)..], entries[i]);
        }
    }

    private static void WriteDifatSector(byte[] file, int fileSector, int fatChunks, int firstFatIndex, uint next)
    {
        Span<byte> sector = file.AsSpan(fileSector * SectorSize, SectorSize);
        for (var i = 0; i < 127; i++)
        {
            var fatIndex = firstFatIndex + i;
            BinaryPrimitives.WriteUInt32LittleEndian(sector[(4 * i)..], fatIndex < fatChunks ? (uint)fatIndex : FreeSect);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(sector[508..], next);
    }

    private static void WriteDirectoryEntry(Span<byte> entry, FlatEntry flat)
    {
        entry.Clear();
        var nameBytes = Encoding.Unicode.GetBytes(flat.Name + "\0");
        nameBytes.CopyTo(entry);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[64..], (ushort)nameBytes.Length);
        entry[66] = (byte)flat.Type;
        BinaryPrimitives.WriteUInt32LittleEndian(entry[68..], flat.Left);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[72..], flat.Right);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[76..], flat.Child);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[116..], flat.Start);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[120..], flat.Size);
    }
}