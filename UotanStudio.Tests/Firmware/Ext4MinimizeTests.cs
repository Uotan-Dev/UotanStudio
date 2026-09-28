using System.Buffers.Binary;
using System.Diagnostics;
using UotanStudio.Firmware.BlockImage.Sparse;
using UotanStudio.Firmware.BlockImage.Sparse.IO;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.FileSystem.Ext4;
using UotanStudio.Firmware.FileSystem.Ext4.Builder;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class Ext4MinimizeTests
{
    [Fact]
    public void Build_Minimize_TrimsLogicalCapacityAndPreservesContents()
    {
        using var temp = CreateSource();
        var source = Path.Combine(temp.Path, "root");
        var regular = Path.Combine(temp.Path, "regular.img");
        var minimized = Path.Combine(temp.Path, "minimized.img");

        Ext4Builder.Build(source, regular, new Ext4Options
        {
            CancellationToken = TestContext.Current.CancellationToken,
        });
        Ext4Builder.Build(source, minimized, MinimizeOptions());

        Assert.True(new FileInfo(minimized).Length < new FileInfo(regular).Length);
        using var parser = new Ext4Parser(minimized);
        Assert.Equal(new FileInfo(minimized).Length, parser.PartitionSize);
        Assert.Equal(File.ReadAllBytes(Path.Combine(source, "payload.bin")), ReadFile(parser, "payload.bin"));
        Assert.Equal("ro.product.name=uotan\n", System.Text.Encoding.UTF8.GetString(ReadFile(parser, "system/build.prop")));
        Assert.Equal(0L, ReadFreeBlocks(minimized));
    }

    [Fact]
    public void Build_MinimizeAndExplicitCapacity_PreservesExistingDestination()
    {
        using var temp = CreateSource();
        var existing = "existing-output"u8.ToArray();
        var output = temp.WriteBytes("system.img", existing);

        Assert.Throws<InvalidOperationException>(() => Ext4Builder.Build(Path.Combine(temp.Path, "root"), output,
            new Ext4Options
            {
                CapacityMode = Ext4CapacityMode.Minimize,
                TargetSizeBytes = 16 * 1024 * 1024,
                CancellationToken = TestContext.Current.CancellationToken,
            }));

        Assert.Equal(existing, File.ReadAllBytes(output));
    }

    [Fact]
    public void Build_ReadOnlySharedBlocksAndMinimize_ProducesCompactSharedFilesystem()
    {
        using var temp = new TempDirectory();
        var content = CreateData(2 * 1024 * 1024);
        temp.WriteBytes("root/A.bin", content);
        temp.WriteBytes("root/B.bin", content);
        var output = Path.Combine(temp.Path, "shared-minimized.img");

        Ext4Builder.Build(Path.Combine(temp.Path, "root"), output, new Ext4Options
        {
            CapacityMode = Ext4CapacityMode.Minimize,
            BlockSharingMode = Ext4BlockSharingMode.ShareDuplicateData,
            ReadOnly = true,
            IncludeJournal = false,
            CancellationToken = TestContext.Current.CancellationToken,
        });

        using var parser = new Ext4Parser(output);
        Assert.Equal(content, ReadFile(parser, "A.bin"));
        Assert.Equal(content, ReadFile(parser, "B.bin"));
        var features = ReadRoCompatFeatures(output);
        Assert.NotEqual(0U, features & 0x1000U);
        Assert.NotEqual(0U, features & 0x4000U);
        Assert.Equal(0L, ReadFreeBlocks(output));
    }

    [Fact]
    public void Build_MinimizeToSeekableStream_ShrinksExistingLength()
    {
        using var temp = CreateSource();
        using var output = new MemoryStream(new byte[32 * 1024 * 1024], writable: true);

        Ext4Builder.Build(Path.Combine(temp.Path, "root"), output, MinimizeOptions());

        Assert.True(output.Length < 32 * 1024 * 1024);
        output.Position = 0;
        using var parser = new Ext4Parser(output);
        Assert.Equal(output.Length, parser.PartitionSize);
    }

    [Fact]
    public void Build_MinimizedRawAndSparse_HaveIdenticalLogicalCapacityAndBytes()
    {
        using var temp = CreateSource();
        var raw = Path.Combine(temp.Path, "minimized.img");
        var sparse = Path.Combine(temp.Path, "minimized.simg");
        var expanded = Path.Combine(temp.Path, "expanded.img");
        Ext4Builder.Build(Path.Combine(temp.Path, "root"), raw, MinimizeOptions());

        SparseImageConverter.ConvertRawToSparse(raw, sparse);
        using (var parsed = SparseReader.FromImageFile(sparse))
        {
            Assert.Equal(new FileInfo(raw).Length,
                checked((long)parsed.Header.TotalBlocks * parsed.Header.BlockSize));
        }
        SparseImageConverter.ConvertSparseToRaw([sparse], expanded);

        Assert.Equal(File.ReadAllBytes(raw), File.ReadAllBytes(expanded));
    }

    [Fact]
    public async Task Pack_PreservedMetadataWithMinimize_RetainsRebuildPathAndContent()
    {
        using var temp = CreateSource();
        var source = Path.Combine(temp.Path, "root");
        var original = Path.Combine(temp.Path, "original.img");
        var extracted = Path.Combine(temp.Path, "extracted");
        var minimized = Path.Combine(temp.Path, "minimized.img");
        var handler = new Ext4FormatHandler();
        Assert.True((await handler.PackAsync(source, original, TestContext.Current.CancellationToken)).Success);
        Assert.True((await handler.UnpackAsync(original, extracted,
            cancellationToken: TestContext.Current.CancellationToken)).Success);

        using (RepackMetadata.UsePackingOverrides(new Dictionary<string, string>
        {
            ["CapacityPolicy"] = "minimize",
            ["ReadOnly"] = "1",
            ["BlockSharingMode"] = nameof(Ext4BlockSharingMode.Independent),
        }))
        {
            var result = await handler.PackAsync(extracted, minimized, TestContext.Current.CancellationToken);
            Assert.True(result.Success, result.Summary);
        }

        Assert.True(new FileInfo(minimized).Length < new FileInfo(original).Length);
        using var parser = new Ext4Parser(minimized);
        Assert.Equal(File.ReadAllBytes(Path.Combine(source, "payload.bin")), ReadFile(parser, "payload.bin"));
        Assert.NotEqual(0U, ReadRoCompatFeatures(minimized) & 0x1000U);
    }

    [Fact]
    public async Task Build_Minimize_PassesE2fsckAndResize2fsCannotShrinkFurtherWhenAvailable()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("UOTAN_NATIVE_TOOLS") != "1")
        {
            Assert.Skip("Set UOTAN_NATIVE_TOOLS=1 on Windows with Alpine WSL and e2fsprogs 1.47.4 to run this interoperability check.");
        }

        var version = await RunWslAsync("resize2fs");
        if (!version.Output.Contains("resize2fs 1.47.4", StringComparison.Ordinal))
        {
            Assert.Skip("Alpine WSL with fixed e2fsprogs 1.47.4 is unavailable.");
        }

        using var temp = CreateSource();
        var image = Path.Combine(temp.Path, "minimized.img");
        var resizeCopy = Path.Combine(temp.Path, "resize-copy.img");
        Ext4Builder.Build(Path.Combine(temp.Path, "root"), image, MinimizeOptions());
        File.Copy(image, resizeCopy);
        var before = new FileInfo(resizeCopy).Length;
        var linuxImage = ToWslPath(image);
        var linuxCopy = ToWslPath(resizeCopy);

        var check = await RunWslAsync("e2fsck", "-fn", linuxImage);
        Assert.True(check.ExitCode == 0, check.Output);
        var resize = await RunWslAsync("resize2fs", "-M", linuxCopy);
        Assert.True(resize.ExitCode == 0, resize.Output);
        Assert.Equal(before, new FileInfo(resizeCopy).Length);
        var resizedCheck = await RunWslAsync("e2fsck", "-fn", linuxCopy);
        Assert.True(resizedCheck.ExitCode == 0, resizedCheck.Output);
    }

    [Fact]
    public async Task Build_Minimize_MultiGroupJournalAndDepthOneExtent_PassesNativeChecksWhenAvailable()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("UOTAN_NATIVE_TOOLS") != "1")
        {
            Assert.Skip("Set UOTAN_NATIVE_TOOLS=1 on Windows with Alpine WSL and e2fsprogs 1.47.4 to run this interoperability check.");
        }

        var version = await RunWslAsync("resize2fs");
        if (!version.Output.Contains("resize2fs 1.47.4", StringComparison.Ordinal))
        {
            Assert.Skip("Alpine WSL with fixed e2fsprogs 1.47.4 is unavailable.");
        }

        using var temp = new TempDirectory();
        var root = Path.Combine(temp.Path, "root");
        Directory.CreateDirectory(root);
        var largePath = Path.Combine(root, "large.bin");
        var megabyte = CreateData(1024 * 1024);
        using (var large = new FileStream(largePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            for (var i = 0; i < 129; i++)
            {
                large.Write(megabyte);
            }
        }

        var fragmented = new byte[4096 * 40];
        var block = CreateData(4096);
        for (var i = 0; i < 40; i += 2)
        {
            block.CopyTo(fragmented, i * 4096);
        }
        temp.WriteBytes("root/fragmented.bin", fragmented);
        var image = Path.Combine(temp.Path, "multi-group.img");
        var resizeCopy = Path.Combine(temp.Path, "multi-group-copy.img");
        Ext4Builder.Build(root, image, new Ext4Options
        {
            CapacityMode = Ext4CapacityMode.Minimize,
            EnableSparse = true,
            IncludeJournal = true,
            CancellationToken = TestContext.Current.CancellationToken,
        });

        using (var parser = new Ext4Parser(image))
        {
            var fragmentedInode = parser.ListFiles().Single(static entry => entry.Path == "fragmented.bin").Inode;
            Assert.True(ReadExtentDepth(image, fragmentedInode) > 0);
        }
        Assert.True(ReadGroupCount(image) > 1);
        File.Copy(image, resizeCopy);
        var before = new FileInfo(resizeCopy).Length;
        var check = await RunWslAsync("e2fsck", "-fn", ToWslPath(image));
        Assert.True(check.ExitCode == 0, check.Output);
        var resize = await RunWslAsync("resize2fs", "-M", ToWslPath(resizeCopy));
        Assert.True(resize.ExitCode == 0, resize.Output);
        Assert.Equal(before, new FileInfo(resizeCopy).Length);
    }

    private static Ext4Options MinimizeOptions() => new()
    {
        CapacityMode = Ext4CapacityMode.Minimize,
        CancellationToken = TestContext.Current.CancellationToken,
    };

    private static TempDirectory CreateSource()
    {
        var temp = new TempDirectory();
        temp.WriteBytes("root/payload.bin", CreateData(1024 * 1024 + 317));
        temp.WriteBytes("root/system/build.prop", "ro.product.name=uotan\n"u8.ToArray());
        return temp;
    }

    private static byte[] CreateData(int length)
    {
        var data = new byte[length];
        new Random(0x4D494E34).NextBytes(data);
        return data;
    }

    private static byte[] ReadFile(Ext4Parser parser, string path)
    {
        using var output = new MemoryStream();
        parser.ReadFile(path, output);
        return output.ToArray();
    }

    private static long ReadFreeBlocks(string imagePath)
    {
        using var image = File.OpenRead(imagePath);
        Span<byte> superblock = stackalloc byte[1024];
        image.Position = 1024;
        image.ReadExactly(superblock);
        return BinaryPrimitives.ReadUInt32LittleEndian(superblock[0x0C..])
            | (long)BinaryPrimitives.ReadUInt32LittleEndian(superblock[0x158..]) << 32;
    }

    private static uint ReadRoCompatFeatures(string imagePath)
    {
        using var image = File.OpenRead(imagePath);
        image.Position = 1024 + 0x64;
        Span<byte> value = stackalloc byte[4];
        image.ReadExactly(value);
        return BinaryPrimitives.ReadUInt32LittleEndian(value);
    }

    private static long ReadGroupCount(string imagePath)
    {
        using var image = File.OpenRead(imagePath);
        Span<byte> superblock = stackalloc byte[1024];
        image.Position = 1024;
        image.ReadExactly(superblock);
        var totalBlocks = BinaryPrimitives.ReadUInt32LittleEndian(superblock[0x04..])
            | (long)BinaryPrimitives.ReadUInt32LittleEndian(superblock[0x150..]) << 32;
        var blocksPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(superblock[0x20..]);
        return (totalBlocks + blocksPerGroup - 1) / blocksPerGroup;
    }

    private static ushort ReadExtentDepth(string imagePath, uint inodeNumber)
    {
        using var image = File.OpenRead(imagePath);
        Span<byte> superblock = stackalloc byte[1024];
        image.Position = 1024;
        image.ReadExactly(superblock);
        var blockSize = 1024 << checked((int)BinaryPrimitives.ReadUInt32LittleEndian(superblock[0x18..]));
        var inodesPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(superblock[0x28..]);
        var inodeSize = BinaryPrimitives.ReadUInt16LittleEndian(superblock[0x58..]);
        var group = (inodeNumber - 1) / inodesPerGroup;
        var local = (inodeNumber - 1) % inodesPerGroup;
        Span<byte> descriptor = stackalloc byte[64];
        image.Position = checked(blockSize + group * 64L);
        image.ReadExactly(descriptor);
        var inodeTable = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[8..])
            | (long)BinaryPrimitives.ReadUInt32LittleEndian(descriptor[40..]) << 32;
        image.Position = checked(inodeTable * blockSize + local * inodeSize + 46);
        Span<byte> depth = stackalloc byte[2];
        image.ReadExactly(depth);
        return BinaryPrimitives.ReadUInt16LittleEndian(depth);
    }

    private static async Task<(int ExitCode, string Output)> RunWslAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-d");
        start.ArgumentList.Add("Alpine");
        start.ArgumentList.Add("--");
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start WSL.");
        var outputTask = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, await outputTask + await errorTask);
    }

    private static string ToWslPath(string path)
    {
        var full = Path.GetFullPath(path);
        return $"/mnt/{char.ToLowerInvariant(full[0])}/{full[3..].Replace('\\', '/')}";
    }
}