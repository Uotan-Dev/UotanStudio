using UotanStudio.Firmware.BoundedContexts.DeviceTree.Dt.Dtb;
using UotanStudio.Firmware.BoundedContexts.DeviceTree.Dt.Dtbo;
using UotanStudio.Firmware.BoundedContexts.DeviceTree.Dt.Models;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class DtboByteFidelityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-dtbo-fidelity-" + Guid.NewGuid().ToString("N"));

    public DtboByteFidelityTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private static byte[] BuildDtb(string marker)
    {
        var tree = new DeviceTree();
        tree.Root.AddPropertyString("compatible", marker);
        tree.Root.AddChild("soc").AddPropertyCells("reg", 0x1000_0000, 0x1000);
        return DtbFormat.Write(tree);
    }

    private static void WriteU32BE(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }

    private static byte[] BuildExtendedImage(byte[][] blobs, uint pageSize, uint totalSize, uint fileLength)
    {
        const uint entriesOffset = 64;
        var image = new byte[fileLength];
        WriteU32BE(image, 0, FdtConstants.DtboMagic);
        WriteU32BE(image, 4, totalSize);
        WriteU32BE(image, 8, 32);
        WriteU32BE(image, 12, 32);
        WriteU32BE(image, 16, (uint)blobs.Length);
        WriteU32BE(image, 20, entriesOffset);
        WriteU32BE(image, 24, pageSize);
        WriteU32BE(image, 28, 0);
        for (var offset = 32; offset < entriesOffset; offset++)
        {
            image[offset] = 0xAA;
        }

        var blobOffset = entriesOffset + (uint)blobs.Length * 32;
        var cursor = blobOffset;
        uint previousOffset = 0;
        byte[]? previous = null;
        for (var index = 0; index < blobs.Length; index++)
        {
            var entry = (int)(entriesOffset + index * 32);
            if (previous is not null && previous.AsSpan().SequenceEqual(blobs[index]))
            {
                WriteU32BE(image, entry, (uint)blobs[index].Length);
                WriteU32BE(image, entry + 4, previousOffset);
                WriteU32BE(image, entry + 8, (uint)(index + 1));
                WriteU32BE(image, entry + 12, 0x10);
                WriteU32BE(image, entry + 16, 0xFEED);
                continue;
            }

            WriteU32BE(image, entry, (uint)blobs[index].Length);
            WriteU32BE(image, entry + 4, cursor);
            WriteU32BE(image, entry + 8, (uint)(index + 1));
            WriteU32BE(image, entry + 12, 0x10);
            WriteU32BE(image, entry + 16, 0xFEED);
            blobs[index].CopyTo(image, (int)cursor);
            previousOffset = cursor;
            previous = blobs[index];
            cursor += (uint)blobs[index].Length;
        }

        return image;
    }

    [Fact]
    public void Pack_MkdtboimgConventions_KeepsBlobsContiguousAndRecordsPageSizeWithoutAlignment()
    {
        var first = BuildDtb("board-a");
        var second = BuildDtb("board-b");

        var image = DtboFormat.Pack([first, second], pageSize: 4096);

        var (header, entries) = DtboFormat.ReadTable(new MemoryStream(image));
        Assert.Equal(4096u, header.PageSize);
        Assert.Equal(32u, header.EntriesOffset);
        Assert.Equal((uint)image.Length, header.TotalSize);
        Assert.Equal(96u + (uint)first.Length, entries[1].DtOffset);
        Assert.Equal(second, image[(96 + first.Length)..]);
    }

    [Fact]
    public void Repack_ExtendedEntriesOffsetWithGapAndPadding_IsByteIdentical()
    {
        var blobs = new[] { BuildDtb("overlay-x"), BuildDtb("overlay-y") };
        var original = BuildExtendedImage(blobs, pageSize: 2048,
            totalSize: (uint)(64 + 2 * 32 + blobs[0].Length + blobs[1].Length),
            fileLength: 4096);
        var input = Path.Combine(_root, "extended.dtbo.img");
        var output = Path.Combine(_root, "repacked.dtbo.img");
        File.WriteAllBytes(input, original);

        DtboFormat.Repack(input, output, TestContext.Current.CancellationToken);

        Assert.Equal(original, File.ReadAllBytes(output));
    }

    [Fact]
    public void UnpackThenPackDirectory_UnchangedOverlays_IsByteIdenticalWithExtendedHeader()
    {
        var shared = BuildDtb("shared-overlay");
        var blobs = new[] { shared, shared };
        var original = BuildExtendedImage(blobs, pageSize: 4096,
            totalSize: (uint)(64 + 2 * 32 + shared.Length),
            fileLength: (uint)(64 + 2 * 32 + 2 * shared.Length));
        var input = Path.Combine(_root, "dedup.dtbo.img");
        File.WriteAllBytes(input, original);
        var directory = Path.Combine(_root, "dedup-dir");

        DtboFormat.Unpack(input, directory, cancellationToken: TestContext.Current.CancellationToken);
        DtboFormat.PackDirectory(directory, Path.Combine(_root, "dedup-out.img"), TestContext.Current.CancellationToken);

        Assert.Equal(original, File.ReadAllBytes(Path.Combine(_root, "dedup-out.img")));
    }

    [Fact]
    public void UnpackThenPackDirectory_RoundTripOfPlainImage_IsByteIdentical()
    {
        var image = DtboFormat.Pack([BuildDtb("board-c"), BuildDtb("board-d")], pageSize: 2048);
        var input = Path.Combine(_root, "plain.dtbo.img");
        File.WriteAllBytes(input, image);
        var directory = Path.Combine(_root, "plain-dir");

        var written = DtboFormat.Unpack(input, directory, cancellationToken: TestContext.Current.CancellationToken);
        DtboFormat.PackDirectory(directory, Path.Combine(_root, "plain-out.img"), TestContext.Current.CancellationToken);

        Assert.Contains(written, path => path.EndsWith("original.dtbo.img", StringComparison.Ordinal));
        Assert.Equal(image, File.ReadAllBytes(Path.Combine(_root, "plain-out.img")));
    }

    [Fact]
    public void PackDirectory_ModifiedOverlay_RebuildsWithoutExtendedHeaderLoss()
    {
        var blobs = new[] { BuildDtb("keep-a"), BuildDtb("keep-b") };
        var original = BuildExtendedImage(blobs, pageSize: 2048,
            totalSize: (uint)(64 + 2 * 32 + blobs[0].Length + blobs[1].Length),
            fileLength: (uint)(64 + 2 * 32 + blobs[0].Length + blobs[1].Length));
        var input = Path.Combine(_root, "modify.dtbo.img");
        File.WriteAllBytes(input, original);
        var directory = Path.Combine(_root, "modify-dir");
        DtboFormat.Unpack(input, directory, cancellationToken: TestContext.Current.CancellationToken);
        File.WriteAllBytes(Path.Combine(directory, "overlay_00.dtb"), BuildDtb("keep-x"));

        DtboFormat.PackDirectory(directory, Path.Combine(_root, "modify-out.img"), TestContext.Current.CancellationToken);

        var rebuilt = File.ReadAllBytes(Path.Combine(_root, "modify-out.img"));
        Assert.Equal(original[..64], rebuilt[..64]);
        var (header, entries) = DtboFormat.ReadTable(new MemoryStream(rebuilt));
        Assert.Equal(64u, header.EntriesOffset);
        Assert.Equal((uint)blobs[1].Length, entries[1].DtSize);
    }
}