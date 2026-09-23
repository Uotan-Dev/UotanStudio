using System.Text;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Lp.Models;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Lp.Reading;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Lp.Writing;
using UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.IO;
using UotanStudio.Services.Flash;
using UotanStudio.Tests.Comm.TestDoubles;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class FlashSuperEmptyServiceTests
{
    [Fact]
    public void Prepare_StandaloneEmptyMetadata_ProducesValidatedBytes()
    {
        using var root = new TempDirectory();
        var bytes = EmptyMetadata();
        var path = root.WriteBytes("super_empty.img", bytes);

        var prepared = FlashSuperEmptyService.Prepare(path);

        Assert.Equal(bytes, prepared.MetadataImage);
        Assert.Equal(0x100000, prepared.DeviceSize);
    }

    [Fact]
    public void Prepare_NonEmptySuper_RejectsBeforeOpeningDevice()
    {
        using var root = new TempDirectory();
        var path = root.WriteBytes("super_empty.img", [.. EmptyMetadata(), 0]);

        Assert.Throws<InvalidDataException>(() => FlashSuperEmptyService.Prepare(path));
    }

    [Fact]
    public void Build_ValidatedEmptyMetadata_ExpandsToCanonicalZeroFilledPhysicalSuper()
    {
        using var root = new TempDirectory();
        var prepared = FlashSuperEmptyService.Prepare(root.WriteBytes("super_empty.img", EmptyMetadata()));
        var output = Path.Combine(root.Path, "physical-super.sparse");

        FlashSuperEmptySparse.Build(prepared, output, TestContext.Current.CancellationToken);

        using var image = SparseReader.FromImageFile(output);
        using var expanded = new MemoryStream();
        image.WriteRawToStream(expanded);
        using var canonical = new MemoryStream(new byte[prepared.DeviceSize], writable: true);
        MetadataWriter.WriteToImageStream(canonical, MetadataReader.ReadFromImageStream(
            new MemoryStream(prepared.MetadataImage, writable: false)));
        Assert.Equal(canonical.ToArray(), expanded.ToArray());
        Assert.True(new FileInfo(output).Length < prepared.DeviceSize);
    }

    [Theory]
    [InlineData("no", "0x100000", false)]
    [InlineData("yes", "0x200000", false)]
    [InlineData("yes", "0x100000", true)]
    public void Apply_DeviceModeAndCapacity_OnlyValidFastbootdDownloadsAndUpdates(
        string userspace, string deviceSize, bool expectedWrite)
    {
        using var root = new TempDirectory();
        var prepared = FlashSuperEmptyService.Prepare(root.WriteBytes("super_empty.img", EmptyMetadata()));
        var transport = new ScriptedCommTransport();
        transport.Enqueue("OKAY" + userspace);
        if (userspace == "yes")
            transport.Enqueue("OKAY" + deviceSize);
        if (expectedWrite)
        {
            transport.Enqueue("OKAY"); // getvar:max-download-size
            transport.Enqueue($"DATA{prepared.MetadataImage.Length:x8}");
            transport.Enqueue("OKAY"); // download payload
            transport.Enqueue("OKAY"); // update-super
        }

        using var session = new FastbootSession(transport, timeoutMs: 3000);
        if (expectedWrite)
        {
            FlashSuperEmptyService.Apply(session, prepared, TestContext.Current.CancellationToken);
            Assert.Equal("update-super:super:wipe", Encoding.UTF8.GetString(transport.Writes[^1]));
            Assert.Contains(transport.Writes, write => write.AsSpan().SequenceEqual(prepared.MetadataImage));
        }
        else
        {
            Assert.ThrowsAny<Exception>(() =>
                FlashSuperEmptyService.Apply(session, prepared, TestContext.Current.CancellationToken));
            Assert.DoesNotContain(transport.Writes,
                write => Encoding.UTF8.GetString(write).StartsWith("download:", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Apply_DownloadFailure_DoesNotSendUpdateSuper()
    {
        using var root = new TempDirectory();
        var prepared = FlashSuperEmptyService.Prepare(root.WriteBytes("super_empty.img", EmptyMetadata()));
        var transport = new ScriptedCommTransport();
        transport.Enqueue("OKAYyes");
        transport.Enqueue("OKAY0x100000");
        transport.Enqueue("OKAY");
        transport.Enqueue($"DATA{prepared.MetadataImage.Length:x8}");
        transport.Enqueue("FAILdownload rejected");

        using var session = new FastbootSession(transport, timeoutMs: 3000);
        Assert.ThrowsAny<Exception>(() =>
            FlashSuperEmptyService.Apply(session, prepared, TestContext.Current.CancellationToken));

        Assert.DoesNotContain(transport.Writes,
            write => Encoding.UTF8.GetString(write).StartsWith("update-super:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("0x100000", "0x100000", true, 1)]
    [InlineData("0x100000", "0x3000", true, 2)]
    [InlineData("0x200000", "0x100000", false, 0)]
    public void ApplyBootloader_PhysicalSuperCapacity_GatesSparseFlash(
        string size, string downloadLimit, bool expectedFlash, int minimumChunks)
    {
        using var root = new TempDirectory();
        var prepared = FlashSuperEmptyService.Prepare(root.WriteBytes("super_empty.img", EmptyMetadata()));
        var transport = new ScriptedCommTransport
        {
            OnWrite = data =>
            {
                var text = Encoding.UTF8.GetString(data);
                var response = text switch
                {
                    "getvar:is-userspace" => "OKAY",
                    "getvar:partition-size:super" => "OKAY" + size,
                    "getvar:max-download-size" => "OKAY" + downloadLimit,
                    "flash:super" => "OKAY",
                    _ when text.StartsWith("download:", StringComparison.Ordinal) =>
                        "DATA" + text["download:".Length..],
                    _ => "OKAY"
                };
                return [Encoding.UTF8.GetBytes(response)];
            }
        };
        using var session = new FastbootSession(transport, timeoutMs: 3000);

        if (expectedFlash)
        {
            FlashSuperEmptyService.ApplyBootloader(session, prepared, TestContext.Current.CancellationToken);
            Assert.True(transport.Writes.Count(write => Encoding.UTF8.GetString(write) == "flash:super") >= minimumChunks);
            foreach (var write in transport.Writes.Where(write =>
                Encoding.UTF8.GetString(write).StartsWith("download:", StringComparison.Ordinal)))
                Assert.True(Convert.ToInt64(Encoding.UTF8.GetString(write)["download:".Length..], 16) <=
                    Convert.ToInt64(downloadLimit[2..], 16));
            Assert.DoesNotContain(transport.Writes,
                write => Encoding.UTF8.GetString(write).StartsWith("update-super:", StringComparison.Ordinal));
        }
        else
        {
            Assert.Throws<InvalidDataException>(() =>
                FlashSuperEmptyService.ApplyBootloader(session, prepared, TestContext.Current.CancellationToken));
            Assert.DoesNotContain(transport.Writes,
                write => Encoding.UTF8.GetString(write).StartsWith("download:", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ApplyBootloader_DownloadFailure_DoesNotFlashSuper()
    {
        using var root = new TempDirectory();
        var prepared = FlashSuperEmptyService.Prepare(root.WriteBytes("super_empty.img", EmptyMetadata()));
        var transport = new ScriptedCommTransport
        {
            OnWrite = data =>
            {
                var text = Encoding.UTF8.GetString(data);
                var response = text switch
                {
                    "getvar:is-userspace" => "OKAY",
                    "getvar:partition-size:super" => "OKAY0x100000",
                    "getvar:max-download-size" => "OKAY0x3000",
                    _ when text.StartsWith("download:", StringComparison.Ordinal) =>
                        "DATA" + text["download:".Length..],
                    _ => "FAILdownload rejected"
                };
                return [Encoding.UTF8.GetBytes(response)];
            }
        };
        using var session = new FastbootSession(transport, timeoutMs: 3000);

        Assert.ThrowsAny<Exception>(() =>
            FlashSuperEmptyService.ApplyBootloader(session, prepared, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(transport.Writes, write => Encoding.UTF8.GetString(write) == "flash:super");
    }

    internal static byte[] EmptyMetadata()
    {
        var metadata = new LpMetadata
        {
            Geometry = new LpMetadataGeometry
            {
                MetadataMaxSize = 65536,
                MetadataSlotCount = 2,
                LogicalBlockSize = 4096
            },
            Partitions = [new() { Name = "system_a"u8.ToArray() }],
            Groups = [new() { Name = "default"u8.ToArray() }],
            BlockDevices = [new()
            {
                Size = 0x100000,
                FirstLogicalSector = 536,
                Alignment = 4096,
                PartitionName = "super"u8.ToArray()
            }]
        };
        return [.. MetadataWriter.SerializeGeometry(metadata.Geometry), .. MetadataWriter.SerializeMetadata(metadata)];
    }
}