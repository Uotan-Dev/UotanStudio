using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Firmware.FileSystem.Ext4;
using UotanStudio.Firmware.FileSystem.Ext4.Builder;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class Ext4SharedBlocksTests
{
    private const int BlockSize = 4096;

    [Fact]
    public void Build_SharedDuplicateData_UsesSamePhysicalBlocksForIndependentInodes()
    {
        using var temp = new TempDirectory();
        var root = Path.Combine(temp.Path, "root");
        var a = CreateDeterministicData(2 * 1024 * 1024);
        var c = a.ToArray();
        c[c.Length / 2] ^= 0x5A;
        temp.WriteBytes("root/A.bin", a);
        temp.WriteBytes("root/B.bin", a);
        temp.WriteBytes("root/C.bin", c);
        temp.WriteBytes("root/tail.bin", a[..(BlockSize + 17)]);
        temp.WriteBytes("root/empty.bin", []);

        var independent = Path.Combine(temp.Path, "independent.img");
        var shared = Path.Combine(temp.Path, "shared.img");
        Ext4Builder.Build(root, independent, CreateOptions(Ext4BlockSharingMode.Independent));
        Ext4Builder.Build(root, shared, CreateOptions(Ext4BlockSharingMode.ShareDuplicateData));

        using var parser = new Ext4Parser(shared);
        var entries = parser.ListFiles().ToDictionary(static entry => entry.Path, StringComparer.Ordinal);
        Assert.NotEqual(entries["A.bin"].Inode, entries["B.bin"].Inode);

        var aBlocks = ReadDataBlocks(shared, entries["A.bin"].Inode);
        var bBlocks = ReadDataBlocks(shared, entries["B.bin"].Inode);
        var cBlocks = ReadDataBlocks(shared, entries["C.bin"].Inode);
        Assert.Equal(512, aBlocks.Count);
        Assert.Equal(aBlocks, bBlocks);
        Assert.Equal(aBlocks.Count, cBlocks.Count);
        Assert.Equal(aBlocks.Count - 1, aBlocks.Zip(cBlocks).Count(static pair => pair.First == pair.Second));
        Assert.NotEqual(aBlocks[256], cBlocks[256]);

        Assert.NotEqual(0U, ReadRoCompatFeatures(shared) & 0x4000U);
        Assert.Equal(1024, ReadFreeBlocks(shared) - ReadFreeBlocks(independent));

        Assert.Equal(a, ReadFile(parser, "A.bin"));
        Assert.Equal(a, ReadFile(parser, "B.bin"));
        Assert.Equal(c, ReadFile(parser, "C.bin"));
        Assert.Equal(a[..(BlockSize + 17)], ReadFile(parser, "tail.bin"));
        Assert.Empty(ReadFile(parser, "empty.bin"));
    }

    [Fact]
    public void Build_SharingRequestedWithoutDuplicates_DoesNotSetSharedBlocksFeature()
    {
        using var temp = new TempDirectory();
        temp.WriteBytes("root/unique.bin", CreateDeterministicData(BlockSize));
        var output = Path.Combine(temp.Path, "unique.img");

        Ext4Builder.Build(Path.Combine(temp.Path, "root"), output,
            CreateOptions(Ext4BlockSharingMode.ShareDuplicateData));

        Assert.Equal(0U, ReadRoCompatFeatures(output) & 0x4000U);
    }

    [Fact]
    public void Build_HashCollision_ConfirmsBytesBeforeSharingPhysicalBlock()
    {
        using var temp = new TempDirectory();
        temp.WriteBytes("root/A.bin", Enumerable.Repeat((byte)0x11, BlockSize).ToArray());
        temp.WriteBytes("root/B.bin", Enumerable.Repeat((byte)0x22, BlockSize).ToArray());
        var output = Path.Combine(temp.Path, "collision.img");

        Ext4Builder.Build(Path.Combine(temp.Path, "root"), output, new Ext4Options
        {
            BlockSharingMode = Ext4BlockSharingMode.ShareDuplicateData,
            ReadOnly = true,
            IncludeJournal = false,
            BlockHashProvider = static _ => new byte[32],
            CancellationToken = TestContext.Current.CancellationToken,
        });

        using var parser = new Ext4Parser(output);
        var entries = parser.ListFiles().ToDictionary(static entry => entry.Path, StringComparer.Ordinal);
        Assert.NotEqual(ReadDataBlocks(output, entries["A.bin"].Inode)[0],
            ReadDataBlocks(output, entries["B.bin"].Inode)[0]);
        Assert.Equal(0U, ReadRoCompatFeatures(output) & 0x4000U);
    }

    [Fact]
    public void Build_SharedSparseFiles_PreserveHolesAndShareNonZeroBlocks()
    {
        using var temp = new TempDirectory();
        var content = new byte[BlockSize * 4];
        CreateDeterministicData(BlockSize).CopyTo(content, 0);
        CreateDeterministicData(BlockSize).CopyTo(content, BlockSize * 3);
        temp.WriteBytes("root/A.bin", content);
        temp.WriteBytes("root/B.bin", content);
        var output = Path.Combine(temp.Path, "sparse.img");

        Ext4Builder.Build(Path.Combine(temp.Path, "root"), output, new Ext4Options
        {
            BlockSharingMode = Ext4BlockSharingMode.ShareDuplicateData,
            ReadOnly = true,
            EnableSparse = true,
            IncludeJournal = false,
            CancellationToken = TestContext.Current.CancellationToken,
        });

        using var parser = new Ext4Parser(output);
        var entries = parser.ListFiles().ToDictionary(static entry => entry.Path, StringComparer.Ordinal);
        var first = ReadDataBlocks(output, entries["A.bin"].Inode);
        var second = ReadDataBlocks(output, entries["B.bin"].Inode);
        Assert.Equal(first, second);
        Assert.Equal([first[0], -1, -1, first[3]], first);
        Assert.Equal(content, ReadFile(parser, "A.bin"));
        Assert.Equal(content, ReadFile(parser, "B.bin"));
    }

    [Fact]
    public void Build_DiskBackedIndex_SuccessAndCancellationRemoveTemporaryIndex()
    {
        using var temp = new TempDirectory();
        var indexDirectory = Path.Combine(temp.Path, "index");
        Directory.CreateDirectory(indexDirectory);
        var content = CreateDeterministicData(2 * 1024 * 1024);
        temp.WriteBytes("root/A.bin", content);
        temp.WriteBytes("root/B.bin", content);

        Ext4Builder.Build(Path.Combine(temp.Path, "root"), Path.Combine(temp.Path, "success.img"),
            new Ext4Options
            {
                BlockSharingMode = Ext4BlockSharingMode.ShareDuplicateData,
                ReadOnly = true,
                IncludeJournal = false,
                BlockIndexDirectory = indexDirectory,
                CancellationToken = TestContext.Current.CancellationToken,
            });
        Assert.Empty(Directory.EnumerateFileSystemEntries(indexDirectory));

        using var cancellation = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => Ext4Builder.Build(Path.Combine(temp.Path, "root"),
            Path.Combine(temp.Path, "cancelled.img"), new Ext4Options
            {
                BlockSharingMode = Ext4BlockSharingMode.ShareDuplicateData,
                ReadOnly = true,
                IncludeJournal = false,
                BlockIndexDirectory = indexDirectory,
                BlockHashProvider = memory =>
                {
                    cancellation.Cancel();
                    return SHA256.HashData(memory.Span);
                },
                CancellationToken = cancellation.Token,
            }));
        Assert.Empty(Directory.EnumerateFileSystemEntries(indexDirectory));
    }

    [Fact]
    public void Build_ExplicitCapacityBetweenSharedAndIndependentRequirements_UsesUniqueBlockPlan()
    {
        using var temp = new TempDirectory();
        var content = CreateDeterministicData(8 * 1024 * 1024);
        temp.WriteBytes("root/A.bin", content);
        temp.WriteBytes("root/B.bin", content);
        var minimized = Path.Combine(temp.Path, "minimum.img");
        Ext4Builder.Build(Path.Combine(temp.Path, "root"), minimized, new Ext4Options
        {
            CapacityMode = Ext4CapacityMode.Minimize,
            BlockSharingMode = Ext4BlockSharingMode.ShareDuplicateData,
            ReadOnly = true,
            CancellationToken = TestContext.Current.CancellationToken,
        });
        var targetSize = new FileInfo(minimized).Length;

        var shared = Path.Combine(temp.Path, "shared.img");
        Ext4Builder.Build(Path.Combine(temp.Path, "root"), shared, new Ext4Options
        {
            TargetSizeBytes = targetSize,
            BlockSharingMode = Ext4BlockSharingMode.ShareDuplicateData,
            ReadOnly = true,
            CancellationToken = TestContext.Current.CancellationToken,
        });
        Assert.Equal(targetSize, new FileInfo(shared).Length);

        Assert.Throws<InvalidOperationException>(() => Ext4Builder.Build(Path.Combine(temp.Path, "root"),
            Path.Combine(temp.Path, "independent.img"), new Ext4Options
            {
                TargetSizeBytes = targetSize,
                BlockSharingMode = Ext4BlockSharingMode.Independent,
                ReadOnly = true,
                CancellationToken = TestContext.Current.CancellationToken,
            }));
    }

    [Fact]
    public void Build_SourceChangesAfterSharedPlan_PreservesExistingDestination()
    {
        using var temp = new TempDirectory();
        var content = CreateDeterministicData(2 * 1024 * 1024);
        temp.WriteBytes("root/A.bin", content);
        var second = temp.WriteBytes("root/B.bin", content);
        var existing = "existing-output"u8.ToArray();
        var output = temp.WriteBytes("system.img", existing);

        Assert.Throws<IOException>(() => Ext4Builder.Build(Path.Combine(temp.Path, "root"), output,
            new Ext4Options
            {
                BlockSharingMode = Ext4BlockSharingMode.ShareDuplicateData,
                ReadOnly = true,
                SharedBlockPlanCompleted = () =>
                {
                    using var file = new FileStream(second, FileMode.Open, FileAccess.Write, FileShare.Read);
                    file.WriteByte(0xA5);
                },
                CancellationToken = TestContext.Current.CancellationToken,
            }));

        Assert.Equal(existing, File.ReadAllBytes(output));
    }

    [Fact]
    public void Build_SourceChangesAfterSharedPlan_DoesNotMutateSeekableStreamBeforePublication()
    {
        using var temp = new TempDirectory();
        var content = CreateDeterministicData(2 * 1024 * 1024);
        temp.WriteBytes("root/A.bin", content);
        var second = temp.WriteBytes("root/B.bin", content);
        var existing = "existing-stream"u8.ToArray();
        using var output = new MemoryStream();
        output.Write(existing);
        output.Position = 0;

        Assert.Throws<IOException>(() => Ext4Builder.Build(Path.Combine(temp.Path, "root"), output,
            new Ext4Options
            {
                BlockSharingMode = Ext4BlockSharingMode.ShareDuplicateData,
                ReadOnly = true,
                SharedBlockPlanCompleted = () =>
                {
                    using var file = new FileStream(second, FileMode.Open, FileAccess.Write, FileShare.Read);
                    file.WriteByte(0xA5);
                },
                CancellationToken = TestContext.Current.CancellationToken,
            }));

        Assert.Equal(existing, output.ToArray());
    }

    [Fact]
    public void WriteInodeBlockCount_WritesHighBitsAndRejectsOverflow()
    {
        Span<byte> inode = stackalloc byte[256];
        const ulong maximum = 0x0000_FFFF_FFFF_FFFFUL;

        Ext4Builder.WriteInodeBlockCount(inode, maximum, 11);

        Assert.Equal(uint.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(inode[28..]));
        Assert.Equal(ushort.MaxValue, BinaryPrimitives.ReadUInt16LittleEndian(inode[116..]));
        Assert.Throws<InvalidOperationException>(() =>
            Ext4Builder.WriteInodeBlockCount(new byte[256], maximum + 1, 11));
    }

    [Fact]
    public void Build_SharingRequestedForWritableFilesystem_PreservesExistingDestination()
    {
        using var temp = new TempDirectory();
        temp.WriteBytes("root/A.bin", CreateDeterministicData(BlockSize));
        var existing = "existing-output"u8.ToArray();
        var output = temp.WriteBytes("system.img", existing);

        Assert.Throws<InvalidOperationException>(() => Ext4Builder.Build(Path.Combine(temp.Path, "root"), output,
            new Ext4Options
            {
                BlockSharingMode = Ext4BlockSharingMode.ShareDuplicateData,
                ReadOnly = false,
                CancellationToken = TestContext.Current.CancellationToken,
            }));

        Assert.Equal(existing, File.ReadAllBytes(output));
    }

    [Fact]
    public void Build_BlockIndexCreationFails_PreservesExistingDestination()
    {
        using var temp = new TempDirectory();
        temp.WriteBytes("root/A.bin", CreateDeterministicData(BlockSize));
        var invalidDirectory = temp.Write("not-a-directory", "file");
        var existing = "existing-output"u8.ToArray();
        var output = temp.WriteBytes("system.img", existing);

        Assert.ThrowsAny<IOException>(() => Ext4Builder.Build(Path.Combine(temp.Path, "root"), output,
            new Ext4Options
            {
                BlockSharingMode = Ext4BlockSharingMode.ShareDuplicateData,
                ReadOnly = true,
                IncludeJournal = false,
                BlockIndexDirectory = invalidDirectory,
                CancellationToken = TestContext.Current.CancellationToken,
            }));

        Assert.Equal(existing, File.ReadAllBytes(output));
    }

    [Fact]
    public void Build_SharingCancelled_PreservesExistingDestination()
    {
        using var temp = new TempDirectory();
        var content = CreateDeterministicData(8 * 1024 * 1024);
        temp.WriteBytes("root/A.bin", content);
        temp.WriteBytes("root/B.bin", content);
        var existing = "existing-output"u8.ToArray();
        var output = temp.WriteBytes("system.img", existing);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => Ext4Builder.Build(Path.Combine(temp.Path, "root"), output,
            new Ext4Options
            {
                BlockSharingMode = Ext4BlockSharingMode.ShareDuplicateData,
                ReadOnly = true,
                CancellationToken = cancellation.Token,
            }));

        Assert.Equal(existing, File.ReadAllBytes(output));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Build_SharedDuplicateData_PassesNativeReadOnlyInspectionWhenAvailable(bool fragmented)
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("UOTAN_NATIVE_TOOLS") != "1")
        {
            Assert.Skip("Set UOTAN_NATIVE_TOOLS=1 on Windows with Alpine WSL and e2fsprogs to run this interoperability check.");
        }

        var probe = await RunWslAsync("which", "e2fsck");
        if (probe.ExitCode != 0)
        {
            Assert.Skip("Alpine WSL with e2fsprogs is unavailable.");
        }

        using var temp = new TempDirectory();
        var content = CreateDeterministicData(2 * 1024 * 1024);
        temp.WriteBytes("root/A.bin", content);
        temp.WriteBytes("root/B.bin", content);
        if (fragmented)
        {
            var reordered = new byte[content.Length];
            for (var i = 0; i < content.Length / BlockSize; i++)
            {
                content.AsSpan(i * BlockSize, BlockSize).CopyTo(
                    reordered.AsSpan((content.Length / BlockSize - i - 1) * BlockSize, BlockSize));
            }
            temp.WriteBytes("root/C.bin", reordered);
        }
        var output = Path.Combine(temp.Path, "shared.img");
        Ext4Builder.Build(Path.Combine(temp.Path, "root"), output,
            CreateOptions(Ext4BlockSharingMode.ShareDuplicateData));
        var linuxPath = ToWslPath(output);

        var check = await RunWslAsync("e2fsck", "-fn", linuxPath);
        Assert.True(check.ExitCode == 0, check.Output);
        var dump = await RunWslAsync("dumpe2fs", "-h", linuxPath);
        Assert.True(dump.ExitCode == 0, dump.Output);
        Assert.Contains("shared_blocks", dump.Output, StringComparison.Ordinal);
        var first = await RunWslAsync("debugfs", "-R", "blocks /A.bin", linuxPath);
        var second = await RunWslAsync("debugfs", "-R", "blocks /B.bin", linuxPath);
        Assert.True(first.ExitCode == 0, first.Output);
        Assert.True(second.ExitCode == 0, second.Output);
        Assert.Equal(NormalizeDebugfsBlocks(first.Output), NormalizeDebugfsBlocks(second.Output));
        Assert.NotEmpty(NormalizeDebugfsBlocks(first.Output));
        var extracted = Path.Combine(temp.Path, "dump.bin");
        var extract = await RunWslAsync("debugfs", "-R", $"dump /A.bin {ToWslPath(extracted)}", linuxPath);
        Assert.True(extract.ExitCode == 0, extract.Output);
        Assert.Equal(content, await File.ReadAllBytesAsync(extracted, TestContext.Current.CancellationToken));
        if (fragmented)
        {
            var reversed = await RunWslAsync("debugfs", "-R", $"dump /C.bin {ToWslPath(extracted)}", linuxPath);
            Assert.True(reversed.ExitCode == 0, reversed.Output);
            Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(temp.Path, "root/C.bin"), TestContext.Current.CancellationToken),
                await File.ReadAllBytesAsync(extracted, TestContext.Current.CancellationToken));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pack_PreservedMetadataWithSharedBlocks_PreservesIdentityCapacityAndContents(bool native)
    {
        if (native && (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("UOTAN_NATIVE_TOOLS") != "1"))
        {
            Assert.Skip("Set UOTAN_NATIVE_TOOLS=1 on Windows with Alpine WSL and e2fsprogs to run this interoperability check.");
        }
        if (native && (await RunWslAsync("which", "e2fsck")).ExitCode != 0)
        {
            Assert.Skip("Alpine WSL with e2fsprogs is unavailable.");
        }
        using var temp = new TempDirectory();
        var content = CreateDeterministicData(2 * 1024 * 1024);
        temp.WriteBytes("root/A.bin", content);
        temp.WriteBytes("root/B.bin", content);
        var original = Path.Combine(temp.Path, "original.img");
        var extracted = Path.Combine(temp.Path, "extracted");
        var rebuilt = Path.Combine(temp.Path, "rebuilt.img");
        var uuid = Guid.NewGuid().ToByteArray();
        Ext4Builder.Build(Path.Combine(temp.Path, "root"), original, new Ext4Options
        {
            Uuid = uuid,
            VolumeName = "shared-test",
            TargetSizeBytes = 32 * 1024 * 1024,
            CancellationToken = TestContext.Current.CancellationToken,
        });
        var handler = new Ext4FormatHandler();
        var unpack = await handler.UnpackAsync(original, extracted, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(unpack.Success, unpack.Summary);
        using (RepackMetadata.UsePackingOverrides(new Dictionary<string, string>
        {
            ["ReadOnly"] = "1",
            ["BlockSharingMode"] = nameof(Ext4BlockSharingMode.ShareDuplicateData),
            ["CapacityPolicy"] = "explicit",
            ["TargetSizeBytes"] = (32 * 1024 * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture),
        }))
        {
            var pack = await handler.PackAsync(extracted, rebuilt, TestContext.Current.CancellationToken);
            Assert.True(pack.Success, pack.Summary);
        }
        Assert.Equal(new FileInfo(original).Length, new FileInfo(rebuilt).Length);
        using var parser = new Ext4Parser(rebuilt);
        Assert.Equal(content, ReadFile(parser, "A.bin"));
        Assert.Equal(content, ReadFile(parser, "B.bin"));
        Assert.Equal(0x5000U, ReadRoCompatFeatures(rebuilt) & 0x5000U);
        var entries = parser.ListFiles().ToDictionary(static entry => entry.Path, StringComparer.Ordinal);
        Assert.NotEqual(entries["A.bin"].Inode, entries["B.bin"].Inode);
        Assert.Equal(ReadDataBlocks(rebuilt, entries["A.bin"].Inode), ReadDataBlocks(rebuilt, entries["B.bin"].Inode));
        using var image = File.OpenRead(rebuilt);
        image.Position = 1024 + 0x68;
        var identity = new byte[16];
        image.ReadExactly(identity);
        Assert.Equal(uuid, identity);
        var volume = new byte[16];
        image.ReadExactly(volume);
        Assert.Equal("shared-test", System.Text.Encoding.UTF8.GetString(volume).TrimEnd('\0'));
        Assert.NotEqual(0U, ReadRoCompatFeatures(rebuilt) & 0x400U);
        if (native)
        {
            var check = await RunWslAsync("e2fsck", "-fn", ToWslPath(rebuilt));
            Assert.True(check.ExitCode == 0, check.Output);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pack_EditedSharedSourceDeleted_InheritsPolicyUnlessExplicitlyOverridden(bool overridePolicy)
        => await VerifyEditedSharedSourceAsync(overridePolicy, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pack_EditedSharedSourceDeleted_PassesNativePolicyAndContentInspection(bool overridePolicy)
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("UOTAN_NATIVE_TOOLS") != "1")
        {
            Assert.Skip("Set UOTAN_NATIVE_TOOLS=1 on Windows with Alpine WSL and e2fsprogs to run this interoperability check.");
        }
        foreach (var tool in new[] { "e2fsck", "debugfs", "dumpe2fs" })
        {
            var probe = await RunWslAsync("which", tool);
            if (probe.ExitCode != 0)
            {
                Assert.Skip($"Alpine WSL with {tool} is unavailable; UOTAN_NATIVE_TOOLS=1 requires e2fsprogs.");
            }
        }
        await VerifyEditedSharedSourceAsync(overridePolicy, true);
    }

    private static async Task VerifyEditedSharedSourceAsync(bool overridePolicy, bool native)
    {
        using var temp = new TempDirectory();
        var content = CreateDeterministicData(BlockSize * 8);
        temp.WriteBytes("root/A.bin", content);
        temp.WriteBytes("root/B.bin", content);
        var original = Path.Combine(temp.Path, "original.img");
        var extracted = Path.Combine(temp.Path, "extracted");
        var rebuilt = Path.Combine(temp.Path, "rebuilt.img");
        Ext4Builder.Build(Path.Combine(temp.Path, "root"), original,
            CreateOptions(Ext4BlockSharingMode.ShareDuplicateData));
        var handler = new Ext4FormatHandler();
        var unpack = await handler.UnpackAsync(original, extracted, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(unpack.Success, unpack.Summary);
        File.Delete(original);
        var edited = content.ToArray();
        edited[0] ^= 0x5A;
        await File.WriteAllBytesAsync(Path.Combine(extracted, "A.bin"), edited, TestContext.Current.CancellationToken);
        using var overrides = overridePolicy
            ? RepackMetadata.UsePackingOverrides(new Dictionary<string, string>
            {
                ["ReadOnly"] = "0",
                ["BlockSharingMode"] = nameof(Ext4BlockSharingMode.Independent),
            })
            : null;
        var result = await handler.PackAsync(extracted, rebuilt, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Summary);
        using var parser = new Ext4Parser(rebuilt);
        Assert.Equal(edited, ReadFile(parser, "A.bin"));
        Assert.Equal(content, ReadFile(parser, "B.bin"));
        Assert.Equal(overridePolicy ? 0U : 0x5000U, ReadRoCompatFeatures(rebuilt) & 0x5000U);
        var entries = parser.ListFiles().ToDictionary(static entry => entry.Path, StringComparer.Ordinal);
        Assert.NotEqual(entries["A.bin"].Inode, entries["B.bin"].Inode);
        var a = ReadDataBlocks(rebuilt, entries["A.bin"].Inode);
        var b = ReadDataBlocks(rebuilt, entries["B.bin"].Inode);
        Assert.NotEqual(a[0], b[0]);
        Assert.Equal(overridePolicy ? 0 : 7, a.Zip(b).Count(static pair => pair.First == pair.Second));
        Assert.False(File.Exists(original));
        if (native)
        {
            var linuxPath = ToWslPath(rebuilt);
            var check = await RunWslAsync("e2fsck", "-fn", linuxPath);
            Assert.True(check.ExitCode == 0, check.Output);
            var header = await RunWslAsync("dumpe2fs", "-h", linuxPath);
            Assert.True(header.ExitCode == 0, header.Output);
            var features = header.Output.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(!overridePolicy, features.Contains("shared_blocks", StringComparer.Ordinal));
            Assert.Equal(!overridePolicy, features.Contains("read-only", StringComparer.Ordinal));
            var first = await RunWslAsync("debugfs", "-R", "blocks /A.bin", linuxPath);
            var second = await RunWslAsync("debugfs", "-R", "blocks /B.bin", linuxPath);
            Assert.True(first.ExitCode == 0, first.Output);
            Assert.True(second.ExitCode == 0, second.Output);
            var firstBlocks = NormalizeDebugfsBlocks(first.Output).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var secondBlocks = NormalizeDebugfsBlocks(second.Output).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(8, firstBlocks.Length);
            Assert.Equal(8, secondBlocks.Length);
            Assert.NotEqual(firstBlocks[0], secondBlocks[0]);
            Assert.Equal(overridePolicy ? 0 : 7, firstBlocks.Intersect(secondBlocks, StringComparer.Ordinal).Count());
            foreach (var (name, expected) in new[] { ("A.bin", edited), ("B.bin", content) })
            {
                var dumped = Path.Combine(temp.Path, $"native-{name}");
                var dump = await RunWslAsync("debugfs", "-R", $"dump /{name} {ToWslPath(dumped)}", linuxPath);
                Assert.True(dump.ExitCode == 0, dump.Output);
                Assert.Equal(expected, await File.ReadAllBytesAsync(dumped, TestContext.Current.CancellationToken));
            }
        }
    }

    private static Ext4Options CreateOptions(Ext4BlockSharingMode mode) => new()
    {
        BlockSharingMode = mode,
        ReadOnly = true,
        IncludeJournal = false,
        TargetSizeBytes = 32 * 1024 * 1024,
        CancellationToken = TestContext.Current.CancellationToken,
    };

    private static byte[] CreateDeterministicData(int length)
    {
        var data = new byte[length];
        new Random(0x4D534631).NextBytes(data);
        return data;
    }

    private static byte[] ReadFile(Ext4Parser parser, string path)
    {
        using var output = new MemoryStream();
        parser.ReadFile(path, output);
        return output.ToArray();
    }

    private static uint ReadRoCompatFeatures(string imagePath)
    {
        using var image = File.OpenRead(imagePath);
        image.Position = 1024 + 0x64;
        Span<byte> value = stackalloc byte[4];
        image.ReadExactly(value);
        return BinaryPrimitives.ReadUInt32LittleEndian(value);
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

    private static IReadOnlyList<long> ReadDataBlocks(string imagePath, uint inodeNumber)
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
        var inode = new byte[inodeSize];
        image.Position = checked(inodeTable * blockSize + local * inodeSize);
        image.ReadExactly(inode);

        var extentRoot = inode.AsSpan(40, 60);
        Assert.Equal(0xF30A, BinaryPrimitives.ReadUInt16LittleEndian(extentRoot));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(extentRoot[6..]));
        var extentCount = BinaryPrimitives.ReadUInt16LittleEndian(extentRoot[2..]);
        var result = new List<long>();
        for (var extentIndex = 0; extentIndex < extentCount; extentIndex++)
        {
            var extent = extentRoot[(12 + extentIndex * 12)..];
            var logical = BinaryPrimitives.ReadUInt32LittleEndian(extent);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(extent[4..]);
            var physical = BinaryPrimitives.ReadUInt32LittleEndian(extent[8..])
                | (long)BinaryPrimitives.ReadUInt16LittleEndian(extent[6..]) << 32;
            while (result.Count < logical)
            {
                result.Add(-1);
            }

            for (var i = 0; i < length; i++)
            {
                result.Add(physical + i);
            }
        }

        return result;
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

    private static string NormalizeDebugfsBlocks(string output)
        => string.Join(' ', output.Split(['\r', '\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Where(static part => part.All(char.IsDigit)));
}