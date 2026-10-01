using System.Buffers.Binary;

namespace UotanStudio.Tests.Firmware.Fat;

internal sealed partial class FatImageBuilder
{
    private readonly Node _root = new(string.Empty, true) { Parent = null };
    private readonly int _variant;
    private readonly int _bytesPerSector;
    private readonly int _sectorsPerCluster;
    private readonly int _clusterCount;
    private readonly int _reservedSectors;
    private readonly int _rootEntries;
    private readonly int _sectorsPerFat;
    private readonly int _rootDirSectors;
    private readonly long _fatStart;
    private readonly long _dataStart;
    private readonly long _clusterBytes;
    private readonly long _totalSectors;
    private readonly List<(Node Owner, int Index, int Next)> _links = [];
    private int _nextCluster = 2;
    private Node? _lastFile;

    internal FatImageBuilder(int variant, int bytesPerSector = 512, int sectorsPerCluster = 1, int? clusterCount = null)
    {
        _variant = variant;
        _bytesPerSector = bytesPerSector;
        _sectorsPerCluster = sectorsPerCluster;
        _clusterBytes = (long)bytesPerSector * sectorsPerCluster;
        _clusterCount = clusterCount ?? variant switch
        {
            12 => 200,
            16 => 4_100,
            _ => 65_525,
        };
        _reservedSectors = variant == 32 ? 32 : 1;
        _rootEntries = variant == 32 ? 0 : 512;
        var fatBytes = variant switch
        {
            12 => (_clusterCount + 2) * 3 / 2 + 2,
            16 => (_clusterCount + 2) * 2,
            _ => (_clusterCount + 2) * 4,
        };
        _sectorsPerFat = (fatBytes + bytesPerSector - 1) / bytesPerSector;
        if (variant == 32)
        {
            var align = bytesPerSector / 4;
            _sectorsPerFat = (_sectorsPerFat + align - 1) / align * align;
        }

        _rootDirSectors = variant == 32 ? 0 : _rootEntries * 32 / bytesPerSector;
        _fatStart = (long)_reservedSectors * bytesPerSector;
        _dataStart = _fatStart + 2L * _sectorsPerFat * bytesPerSector + (long)_rootDirSectors * bytesPerSector;
        _totalSectors = _reservedSectors + 2L * _sectorsPerFat + _rootDirSectors
            + (long)_clusterCount * sectorsPerCluster;
    }

    internal long DeclaredVolumeBytes => _totalSectors * _bytesPerSector;

    internal long CapacityBytes => _clusterBytes * _clusterCount;

    internal FatImageBuilder AddFile(string path, byte[] content)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var parent = _root;
        for (var index = 0; index < segments.Length - 1; index++)
        {
            parent = parent.Child(segments[index], true);
        }

        var node = parent.Child(segments[^1], false);
        node.Content = content;
        _lastFile = node;
        return this;
    }

    internal FatImageBuilder AddDirectory(string path)
    {
        var parent = _root;
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            parent = parent.Child(segment, true);
        }

        return this;
    }

    internal string WriteTo(string path, bool truncateFinalFile = false)
    {
        var nodes = Flattened().ToList();
        if (_variant == 32)
        {
            _root.Clusters = [2];
            _nextCluster = 3;
        }

        foreach (var node in nodes.Where(candidate => candidate != _root))
        {
            node.Clusters = Reserve(node.IsDirectory ? _clusterBytes : node.Content.Length);
        }

        foreach (var node in nodes.Where(candidate => candidate.Clusters.Count > 0))
        {
            for (var index = 0; index < node.Clusters.Count - 1; index++)
            {
                _links.Add((node, index, node.Clusters[index + 1]));
            }

            _links.Add((node, node.Clusters.Count - 1, EndMarker));
        }

        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, _bytesPerSector);
        stream.SetLength(_totalSectors * _bytesPerSector);
        WriteBootSector(stream);
        if (_variant == 32)
        {
            WriteFsInfo(stream);
        }

        var fat = BuildFat();
        for (var copy = 0; copy < 2; copy++)
        {
            stream.Position = _fatStart + (long)copy * _sectorsPerFat * _bytesPerSector;
            stream.Write(fat, 0, _sectorsPerFat * _bytesPerSector);
        }

        var rootImage = Render(_root);
        if (_variant == 32)
        {
            WriteClusters(stream, _root.Clusters, rootImage);
        }
        else
        {
            var fixedRoot = new byte[_rootEntries * 32];
            rootImage.CopyTo(fixedRoot, 0);
            stream.Position = _fatStart + 2L * _sectorsPerFat * _bytesPerSector;
            stream.Write(fixedRoot);
        }

        foreach (var node in nodes.Where(candidate => candidate.IsDirectory && candidate != _root))
        {
            WriteClusters(stream, node.Clusters, Render(node));
        }

        foreach (var node in nodes.Where(candidate => !candidate.IsDirectory && candidate.Content.Length > 0))
        {
            WriteClusters(stream, node.Clusters, node.Content);
        }

        if (truncateFinalFile && _lastFile is { Clusters.Count: > 0 } tail && tail.Content.Length > 1)
        {
            var finalCluster = tail.Clusters[^1];
            var used = (int)(tail.Content.Length % _clusterBytes);
            stream.SetLength(ClusterOffset(finalCluster) + Math.Max(1, used) / 2);
        }

        return path;
    }

    private IEnumerable<Node> Flattened()
    {
        yield return _root;
        foreach (var node in Descendants(_root))
        {
            yield return node;
        }
    }

    private static IEnumerable<Node> Descendants(Node node)
    {
        foreach (var child in node.Children)
        {
            yield return child;
            foreach (var nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }

    private List<int> Reserve(long bytes)
    {
        var needed = (int)((bytes + _clusterBytes - 1) / _clusterBytes);
        if (needed == 0)
        {
            return [];
        }

        if (_nextCluster + needed - 1 > _clusterCount + 1)
        {
            throw new InvalidOperationException("The planned FAT image needs more clusters than it declares.");
        }

        var clusters = new List<int>(needed);
        for (var index = 0; index < needed; index++)
        {
            clusters.Add(_nextCluster++);
        }

        return clusters;
    }

    private void WriteClusters(Stream stream, List<int> clusters, byte[] payload)
    {
        var written = 0;
        foreach (var cluster in clusters)
        {
            if (written >= payload.Length)
            {
                return;
            }

            stream.Position = ClusterOffset(cluster);
            var take = (int)Math.Min(_clusterBytes, payload.Length - written);
            stream.Write(payload, written, take);
            written += take;
        }
    }

    private byte[] BuildFat()
    {
        var fat = new byte[_sectorsPerFat * _bytesPerSector * 2];
        SetEntry(fat, 0, _variant == 32 ? 0x0FFFFFF8 : 0xF8);
        SetEntry(fat, 1, EndMarker);
        foreach (var (owner, index, next) in _links)
        {
            SetEntry(fat, owner.Clusters[index], next);
        }

        return fat;
    }

    private int EndMarker => _variant switch { 12 => 0x0FF8, 16 => 0xFFFF, _ => 0x0FFFFFFF };

    private void SetEntry(byte[] fat, int cluster, int value)
    {
        switch (_variant)
        {
            case 12:
                var position = cluster + cluster / 2;
                if ((cluster & 1) == 0)
                {
                    fat[position] = (byte)(value & 0xFF);
                    fat[position + 1] = (byte)((fat[position + 1] & 0xF0) | ((value >> 8) & 0x0F));
                }
                else
                {
                    fat[position] = (byte)((fat[position] & 0x0F) | ((value & 0x0F) << 4));
                    fat[position + 1] = (byte)((value >> 4) & 0xFF);
                }

                break;
            case 16:
                BinaryPrimitives.WriteUInt16LittleEndian(fat.AsSpan(cluster * 2, 2), (ushort)value);
                break;
            default:
                BinaryPrimitives.WriteUInt32LittleEndian(fat.AsSpan(cluster * 4, 4), (uint)value);
                break;
        }
    }

    private long ClusterOffset(int cluster) => _dataStart + (long)(cluster - 2) * _clusterBytes;

    private sealed class Node(string name, bool isDirectory)
    {
        internal string Name => name;
        internal bool IsDirectory => isDirectory;
        internal Node? Parent { get; set; }
        internal List<Node> Children { get; } = [];
        internal byte[] Content { get; set; } = [];
        internal List<int> Clusters { get; set; } = [];

        internal Node Child(string childName, bool childIsDirectory)
        {
            var existing = Children.FirstOrDefault(candidate => string.Equals(candidate.Name, childName, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                return existing;
            }

            var child = new Node(childName, childIsDirectory) { Parent = this };
            Children.Add(child);
            return child;
        }
    }
}