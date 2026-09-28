using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Romfs;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

/// <summary>
/// RomFS 只读解析与解包测试：合成镜像往返、硬链接/符号链接、损坏拒绝。
/// <para>RomFS read-only parsing and unpacking tests: synthetic volume round-trips, hard and symbolic
/// links, and malformed-image rejection.</para>
/// </summary>
public class RomfsTests
{
    private enum NextMode
    {
        Normal,
        Self,
        BeyondVolume,
    }

    private sealed class Node
    {
        internal required string Name { get; init; }
        internal required int Type { get; init; }
        internal byte[]? Data { get; init; }
        internal Node? Target { get; init; }
        internal List<Node> Children { get; init; } = [];
        internal NextMode Next { get; init; }
    }

    [Fact]
    public void ParseAndExtract_SampleVolume_RoundTripsFilesLinksAndHardLinks()
    {
        var hello = new Node { Name = "hello.txt", Type = 2, Data = "hello romfs\n"u8.ToArray() };
        var link = new Node { Name = "link.txt", Type = 0, Target = hello };
        var nested = new Node { Name = "nested.bin", Type = 2, Data = [0x00, 0x01, 0xFE, 0xFF, 0x80] };
        var sym = new Node { Name = "sym.lnk", Type = 3, Data = "hello.txt"u8.ToArray() };
        var sub = new Node { Name = "sub", Type = 1, Children = [nested, sym] };
        var root = new Node { Name = "", Type = 1, Children = [hello, link, sub] };

        var volume = Build(root, "UotanVol");
        using var ms = new MemoryStream(volume);
        var parser = new RomfsParser(ms);

        Assert.Equal("UotanVol", parser.Name);
        var paths = parser.EnumerateFiles()
            .Select(entry => entry.Path)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["hello.txt", "link.txt", "sub/nested.bin", "sub/sym.lnk"], paths);

        using var temp = new TempDirectory();
        var extracted = parser.ExtractToDirectory(temp.Path, overwrite: true, maximumOutputBytes: 1L << 30, byteProgress: null, CancellationToken.None);
        Assert.Equal("hello romfs\n", File.ReadAllText(Path.Combine(temp.Path, "hello.txt"), Encoding.UTF8));
        Assert.Equal("hello romfs\n", File.ReadAllText(Path.Combine(temp.Path, "link.txt"), Encoding.UTF8));
        Assert.Equal([0x00, 0x01, 0xFE, 0xFF, 0x80], File.ReadAllBytes(Path.Combine(temp.Path, "sub/nested.bin")));
        Assert.Equal("hello.txt", File.ReadAllText(Path.Combine(temp.Path, "sub/sym.lnk"), Encoding.UTF8));
        Assert.Equal(4, extracted.Count);
    }

    [Fact]
    public void Constructor_BadMagic_ThrowsInvalidData()
    {
        var root = new Node { Name = "", Type = 1, Children = [] };
        var volume = Build(root, "Vol");
        volume[3] = (byte)'X';
        Assert.Throws<InvalidDataException>(() => new RomfsParser(new MemoryStream(volume)));
    }

    [Fact]
    public void Constructor_BadSuperblockChecksum_ThrowsInvalidData()
    {
        var hello = new Node { Name = "hello.txt", Type = 2, Data = "padding to move past five hundred bytes so the entry checksums stay valid while only the superblock sum breaks"u8.ToArray() };
        var root = new Node { Name = "", Type = 1, Children = [hello] };
        var volume = Build(root, "Vol");
        volume[400] ^= 0xFF;
        Assert.Throws<InvalidDataException>(() => new RomfsParser(new MemoryStream(volume)));
    }

    [Fact]
    public void Constructor_NonAdvancingChain_ThrowsInvalidData()
    {
        var hello = new Node { Name = "hello.txt", Type = 2, Data = "x"u8.ToArray(), Next = NextMode.Self };
        var root = new Node { Name = "", Type = 1, Children = [hello] };
        var volume = Build(root, "Vol");
        var ex = Assert.Throws<InvalidDataException>(() => new RomfsParser(new MemoryStream(volume)));
        Assert.Contains("does not advance", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_ChainBeyondVolume_ThrowsInvalidData()
    {
        var hello = new Node { Name = "hello.txt", Type = 2, Data = "x"u8.ToArray() };
        var tail = new Node { Name = "z", Type = 2, Data = "y"u8.ToArray(), Next = NextMode.BeyondVolume };
        var root = new Node { Name = "", Type = 1, Children = [hello, tail] };
        var volume = Build(root, "Vol");
        Assert.Throws<InvalidDataException>(() => new RomfsParser(new MemoryStream(volume)));
    }

    [Fact]
    public void Constructor_ChainedHardLinkTarget_ThrowsInvalidData()
    {
        var hello = new Node { Name = "hello.txt", Type = 2, Data = "x"u8.ToArray() };
        var first = new Node { Name = "a.lnk", Type = 0, Target = hello };
        var second = new Node { Name = "b.lnk", Type = 0, Target = first };
        var root = new Node { Name = "", Type = 1, Children = [hello, first, second] };
        var volume = Build(root, "Vol");
        var ex = Assert.Throws<InvalidDataException>(() => new RomfsParser(new MemoryStream(volume)));
        Assert.Contains("invalid target", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_VolumeSizeBeyondStream_ThrowsInvalidData()
    {
        var root = new Node { Name = "", Type = 1, Children = [] };
        var volume = Build(root, "Vol");
        BinaryPrimitives.WriteUInt32BigEndian(volume.AsSpan(8), (uint)(volume.Length + 4096));
        Assert.Throws<InvalidDataException>(() => new RomfsParser(new MemoryStream(volume)));
    }

    [Fact]
    public async Task HandlerLifecycle_MagicSample_ReadsMetadataAndUnpacks()
    {
        var hello = new Node { Name = "hello.txt", Type = 2, Data = "handler path"u8.ToArray() };
        var root = new Node { Name = "", Type = 1, Children = [hello] };
        using var temp = new TempDirectory();
        var image = Path.Combine(temp.Path, "root.rom");
        File.WriteAllBytes(image, Build(root, "HandlerVol"));

        var handler = new RomfsFormatHandler();
        var token = TestContext.Current.CancellationToken;
        Assert.True(handler.CanHandle(image));
        Assert.False(handler.CanHandle(Path.Combine(temp.Path, "missing.bin")));

        var metadata = await handler.ReadMetadataAsync(image, token);
        Assert.True(metadata.Success);
        Assert.Equal("HandlerVol", metadata.Metadata["VolumeName"]);

        var outDir = Path.Combine(temp.Path, "out");
        var unpacked = await handler.UnpackAsync(image, outDir, new UnpackOptions { Overwrite = false }, token);
        Assert.True(unpacked.Success);
        Assert.Equal("handler path", File.ReadAllText(Path.Combine(outDir, "hello.txt"), Encoding.UTF8));
    }

    [Fact]
    public async Task UnpackAsync_OutputBudgetExceeded_ReportsFailure()
    {
        var hello = new Node { Name = "hello.txt", Type = 2, Data = "way too long for one byte"u8.ToArray() };
        var root = new Node { Name = "", Type = 1, Children = [hello] };
        using var temp = new TempDirectory();
        var image = Path.Combine(temp.Path, "tight.rom");
        File.WriteAllBytes(image, Build(root, "TightVol"));

        var handler = new RomfsFormatHandler();
        var token = TestContext.Current.CancellationToken;
        var result = await handler.UnpackAsync(
            image,
            Path.Combine(temp.Path, "out"),
            new UnpackOptions { MaximumOutputBytes = 4 },
            token);
        Assert.False(result.Success);
        Assert.Contains("budget", result.Summary, StringComparison.Ordinal);
    }

    private static byte[] Build(Node root, string volumeName)
    {
        foreach (var directory in EnumerateDirectories(root).ToList())
        {
            var parent = FindParent(root, directory) ?? directory;
            directory.Children.Insert(0, new Node { Name = "..", Type = 0, Target = parent });
            directory.Children.Insert(0, new Node { Name = ".", Type = 0, Target = directory });
        }

        var headerOffsets = new Dictionary<Node, long>();
        var firstChild = new Dictionary<Node, long>();
        var nextOf = new Dictionary<Node, long>();
        var contentEnd = Assign(root, SuperblockStart(volumeName), headerOffsets, firstChild, nextOf);
        var volumeSize = Round(contentEnd, 1024);
        var buffer = new byte[volumeSize];
        "-rom1fs-"u8.CopyTo(buffer);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(8), (uint)volumeSize);
        Encoding.ASCII.GetBytes(volumeName).CopyTo(buffer, 16);

        foreach (var node in EnumerateAll(root))
        {
            Emit(buffer, node, headerOffsets, firstChild, nextOf, volumeSize);
        }

        var sum = 0U;
        var region = (int)Math.Min(volumeSize, 512);
        for (var offset = 0; offset < region; offset += 4)
        {
            if (offset != 12)
            {
                sum += BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset));
            }
        }

        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(12), unchecked((uint)(0 - sum)));
        return buffer;
    }

    private static void Emit(byte[] buffer, Node node, Dictionary<Node, long> headerOffsets, Dictionary<Node, long> firstChild, Dictionary<Node, long> nextOf, long volumeSize)
    {
        var offset = headerOffsets[node];
        var nameBytes = Encoding.ASCII.GetBytes(node.Name);
        var nameRegion = Round(nameBytes.Length + 1L, 16);
        var next = node.Next switch
        {
            NextMode.Self => offset,
            NextMode.BeyondVolume => Round(volumeSize, 16),
            _ => nextOf.TryGetValue(node, out var sibling) ? sibling : 0L,
        };
        var spec = node.Type switch
        {
            0 => node.Target is null ? 0L : headerOffsets[node.Target],
            1 => firstChild[node],
            _ => 0L,
        };
        var size = node.Type is 2 or 3 ? node.Data?.Length ?? 0 : 0;

        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan((int)offset), (uint)(next | (long)node.Type));
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan((int)offset + 4), (uint)spec);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan((int)offset + 8), (uint)size);
        nameBytes.CopyTo(buffer, (int)offset + 16);
        if (node.Data is { Length: > 0 })
        {
            node.Data.CopyTo(buffer, (int)(offset + 16 + nameRegion));
        }

        var entrySum = 0U;
        for (var i = 0; i < 16 + nameRegion; i += 4)
        {
            if (i != 12)
            {
                entrySum += BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan((int)offset + i));
            }
        }

        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan((int)offset + 12), unchecked((uint)(0 - entrySum)));
    }

    private static long Assign(Node node, long offset, Dictionary<Node, long> headerOffsets, Dictionary<Node, long> firstChild, Dictionary<Node, long> nextOf)
    {
        headerOffsets[node] = offset;
        var nameRegion = Round(Encoding.ASCII.GetByteCount(node.Name) + 1L, 16);
        var position = offset + 16 + nameRegion;
        if (node.Type == 1)
        {
            firstChild[node] = position;
            var childOffsets = new List<long>();
            foreach (var child in node.Children)
            {
                childOffsets.Add(position);
                position = Assign(child, position, headerOffsets, firstChild, nextOf);
            }

            for (var i = 0; i < node.Children.Count; i++)
            {
                nextOf[node.Children[i]] = i + 1 < childOffsets.Count ? childOffsets[i + 1] : 0L;
            }

            return position;
        }

        if (node.Type is 2 or 3)
        {
            position = Round(position + node.Data?.Length ?? 0, 16);
        }

        return position;
    }

    private static IEnumerable<Node> EnumerateAll(Node node)
    {
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var descendant in EnumerateAll(child))
            {
                yield return descendant;
            }
        }
    }

    private static IEnumerable<Node> EnumerateDirectories(Node node)
    {
        if (node.Type != 1)
        {
            yield break;
        }

        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var descendant in EnumerateDirectories(child))
            {
                yield return descendant;
            }
        }
    }

    private static Node? FindParent(Node current, Node target)
    {
        foreach (var child in current.Children)
        {
            if (ReferenceEquals(child, target))
            {
                return current;
            }
        }

        foreach (var child in current.Children.Where(entry => entry.Type == 1))
        {
            var found = FindParent(child, target);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static long SuperblockStart(string volumeName) => Round(16L + Encoding.ASCII.GetByteCount(volumeName) + 1, 16);

    private static long Round(long value, long alignment) => (value + alignment - 1) / alignment * alignment;
}