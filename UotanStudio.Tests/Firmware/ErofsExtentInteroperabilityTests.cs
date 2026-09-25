using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Builder;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Compression;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Models;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs.Parser;
using UotanStudio.Firmware.BoundedContexts.FileSystem.Rebuilding;
using UotanStudio.Firmware.SharedKernel.Repacking;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class ErofsExtentInteroperabilityTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public async Task ReadExtentRecords_KernelLayout_PreservesContentAndPassesNativeChecker(int recordSize)
    {
        var directory = Path.Combine(Path.GetTempPath(), "uotan-erofs-extent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var sourceDirectory = Path.Combine(directory, "source");
            Directory.CreateDirectory(sourceDirectory);
            var payload = Enumerable.Repeat((byte)'Q', 8 * 4096).ToArray();
            await File.WriteAllBytesAsync(Path.Combine(sourceDirectory, "data"), payload, TestContext.Current.CancellationToken);
            var imagePath = Path.Combine(directory, "extracted.img");
            new ErofsBuilder().Build(sourceDirectory, imagePath, new ErofsOptions
            {
                Compress = true,
                UseCompactedIndexes = false,
                TailPacking = false,
                CompressedTailPacking = false,
                Fragments = false,
                Dedup = false,
                SuperblockChecksum = false
            });
            byte[] image;
            ulong nid;
            List<(long Lcn, byte Type, int ClusterOfs, long Pblk, uint Delta0, uint Delta1)> indexes;
            using (var source = new ErofsParser(imagePath))
            {
                nid = Assert.Single(source.EnumerateFiles()).Nid;
                indexes = source.DumpIndexClusters("data");
                Assert.Equal(8, indexes.Count);
                Assert.True(indexes[0].Pblk > 0);
            }
            image = await File.ReadAllBytesAsync(imagePath, TestContext.Current.CancellationToken);
            var superblock = ErofsSuperblock.FromImage(image);
            var inodeOffset = checked((int)superblock.GetInodeOffset(nid));
            var inode = ErofsInode.FromBytes(image.AsSpan(inodeOffset, ErofsInode.ExtendedSize));
            Assert.Equal(ErofsInode.DataLayoutCompressed, inode.DataLayout);
            var headerOffset = (inodeOffset + inode.InlineDataOffset + 7) & ~7;
            var indexStart = (headerOffset + ZErofsMapHeader.Size + recordSize - 1) & ~(recordSize - 1);
            image.AsSpan(headerOffset, indexStart + (recordSize == 4 ? 8 : 0)
                + (recordSize == 8 ? 8 * 8 : recordSize) - headerOffset).Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(headerOffset), recordSize > 8 ? 1u : 0u);
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(headerOffset + 4),
                (ushort)(ZErofsMapHeader.AdviseExtents
                    | (uint)(System.Numerics.BitOperations.Log2((uint)recordSize / 4) << ZErofsMapHeader.ExtRecSizeBit)));

            if (recordSize == 8)
            {
                var compressed = new byte[ErofsCompression.MaximumOutputSize(4096)];
                var length = ErofsCompression.Compress(payload, 0, 4096,
                    compressed, 0, compressed.Length, 1);
                Assert.InRange(length, 1, 4096);
                var physical = image.Length;
                Array.Resize(ref image, image.Length + 4096);
                compressed.AsSpan(0, length).CopyTo(image.AsSpan(physical));
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(1024 + 36), checked((uint)(image.Length / 4096)));
                for (var i = 0; i < indexes.Count; i++)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(indexStart + i * 8),
                        0x10000000u | (uint)length);
                    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(indexStart + i * 8 + 4),
                        checked((uint)physical));
                }
            }
            else
            {
                var compressed = new byte[ErofsCompression.MaximumOutputSize(payload.Length)];
                var length = ErofsCompression.Compress(payload, 0, payload.Length,
                    compressed, 0, compressed.Length, 1);
                Assert.InRange(length, 1, 4096);
                var physical = image.Length;
                Array.Resize(ref image, image.Length + 4096);
                compressed.AsSpan(0, length).CopyTo(image.AsSpan(physical));
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(1024 + 36), checked((uint)(image.Length / 4096)));
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(indexStart), 0x10000000u | (uint)length);
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(indexStart + 4), checked((uint)physical));
            }
            await File.WriteAllBytesAsync(imagePath, image, TestContext.Current.CancellationToken);

            using (var parser = new ErofsParser(imagePath))
            {
                Assert.Equal(payload, parser.ReadFileBytes("data"));
                var source = parser.ReadRebuildOptions().SourceInodes![nid];
                Assert.Equal(recordSize, source.SourceExtentRecordSize);
                Assert.Equal(4096, source.SourceExtentClusterSize);
            }
            var metadataRoot = Path.Combine(directory, "metadata");
            using (RepackMetadata.UseContext(RepackMetadataContext.FromProjectRoot(metadataRoot)))
            {
                var unpack = await new ErofsFormatHandler().UnpackAsync(imagePath,
                    Path.Combine(directory, "extracted"), cancellationToken: TestContext.Current.CancellationToken);
                Assert.True(unpack.Success, unpack.Summary);
            }
            var snapshot = Path.Combine(metadataRoot, "extracted");
            var metadataPath = Assert.Single(Directory.GetFiles(snapshot, "metadata.json", SearchOption.AllDirectories));
            using (var document = JsonDocument.Parse(await File.ReadAllTextAsync(metadataPath, TestContext.Current.CancellationToken)))
            {
                var record = document.RootElement.GetProperty("rebuild").GetProperty("erofs")
                    .GetProperty("inodes").EnumerateArray()
                    .Single(entry => entry.GetProperty("path").GetString()!.EndsWith("data", StringComparison.Ordinal));
                Assert.Equal(recordSize, record.GetProperty("sourceExtentRecordSize").GetInt32());
                Assert.Equal(4096, record.GetProperty("sourceExtentClusterSize").GetInt32());
            }
            Assert.DoesNotContain(Directory.GetFiles(snapshot, "*", SearchOption.AllDirectories),
                path => Path.GetExtension(path).Equals(".img", StringComparison.OrdinalIgnoreCase));
            File.Delete(imagePath);
            Directory.Delete(sourceDirectory, recursive: true);
            Directory.Delete(Path.Combine(directory, "extracted"), recursive: true);
            imagePath = Path.Combine(directory, "replayed.erofs");
            FileSystemPreservation.ReplaySnapshot(snapshot, imagePath, TestContext.Current.CancellationToken);
            using (var replay = new ErofsParser(imagePath))
            {
                Assert.Equal(payload, replay.ReadFileBytes("data"));
            }
            if (!OperatingSystem.IsWindows())
            {
                return;
            }
            var check = await RunWsl("fsck.erofs", WslPath(imagePath));
            if (check.ExitCode == 127)
            {
                Assert.Skip("Alpine erofs-utils are unavailable.");
            }
            Assert.True(check.ExitCode == 0, check.Output);
            var mountpoint = "/tmp/uotan-erofs-extent-" + Guid.NewGuid().ToString("N");
            var mkdir = await RunWsl("mkdir", mountpoint);
            Assert.True(mkdir.ExitCode == 0, mkdir.Output);
            try
            {
                var mount = await RunWslRoot("mount", "-t", "erofs", "-o", "loop,ro", WslPath(imagePath), mountpoint);
                Assert.True(mount.ExitCode == 0, mount.Output);
                try
                {
                    var readback = await RunWsl("sha256sum", mountpoint + "/data");
                    Assert.True(readback.ExitCode == 0, readback.Output);
                    Assert.Equal(Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
                        readback.Output.Split(' ')[0]);
                }
                finally
                {
                    var unmount = await RunWslRoot("umount", mountpoint);
                    Assert.True(unmount.ExitCode == 0, unmount.Output);
                }
            }
            finally
            {
                var remove = await RunWsl("rmdir", mountpoint);
                Assert.True(remove.ExitCode == 0, remove.Output);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string WslPath(string path)
        => "/mnt/" + char.ToLowerInvariant(path[0]) + "/" + path[3..].Replace('\\', '/');

    private static Task<(int ExitCode, string Output)> RunWsl(params string[] arguments)
        => RunWslAsUser(false, arguments);

    private static Task<(int ExitCode, string Output)> RunWslRoot(params string[] arguments)
        => RunWslAsUser(true, arguments);

    private static async Task<(int ExitCode, string Output)> RunWslAsUser(bool root, params string[] arguments)
    {
        var start = new ProcessStartInfo("wsl.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in (root ? new[] { "-d", "Alpine", "-u", "root", "--" }
                     : new[] { "-d", "Alpine", "--" }).Concat(arguments))
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, await stdout + await stderr);
    }
}