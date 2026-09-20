using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Models;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Packer;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Parser;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Services.Firmware;
using UotanStudio.ViewModels.Workspace;
using Xunit;

namespace UotanStudio.Tests.Firmware;

[Collection("Workspace state")]
public sealed class BootRepackMetadataTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uotan-boot-config-" + Guid.NewGuid().ToString("N"));
    private string Image => Path.Combine(_root, "boot.img");
    private string DirectoryPath => Path.Combine(_root, "extracted");
    private string Output => Path.Combine(_root, "rebuilt.img");
    private CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public BootRepackMetadataTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    public async Task Boot_AllVersions_AllEntryPoints_ConsumeEmbeddedConfiguration(int version, bool application)
    {
        CreateBoot(version);
        if (application)
        {
            var result = await FirmwareService.Instance.UnpackBootAsync(Image, DirectoryPath, Cancellation);
            Assert.True(result.Success, result.Summary);
        }
        else
        {
            var result = await new BootImageFormatHandler().UnpackAsync(Image, DirectoryPath, cancellationToken: Cancellation);
            Assert.True(result.Success, result.Summary);
        }
        AssertNoLegacyFiles();
        var metadata = Assert.IsType<RepackMetadata>(RepackMetadata.Read(DirectoryPath));
        var header = metadata.Configuration["header"]!.AsObject();
        Assert.Equal(version, header["headerVersion"]!.GetValue<int>());
        Assert.Equal(8, header["ids"]!.AsArray().Count);
        Assert.True(metadata.CanPackDirectory);
        Assert.True(PackingService.BuildImage(DirectoryPath, Output).Success);
        Assert.Equal(File.ReadAllBytes(Image), File.ReadAllBytes(Output));

        header["commandLine"] = "console=ttyS1 edited=1";
        await metadata.SaveAsync(DirectoryPath, Cancellation);
        var packed = await FirmwareService.Instance.PackBootAsync(DirectoryPath, Output, Cancellation);
        Assert.True(packed.Success, packed.Summary);
        using var parser = new BootImageParser();
        parser.ParseFile(Output);
        Assert.Equal("console=ttyS1 edited=1", parser.Header.CommandLine);
        Assert.Equal((BootImageVersion)version, parser.Header.HeaderVersion);
        Assert.Equal(new byte[] { 4, 5, 6 }, parser.Ramdisk);
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    public async Task VendorBoot_PreservesHeaderAndRamdiskTable(int version, bool application)
    {
        var packer = new VendorBootPacker
        {
            Header = new VendorBootHeader
            {
                HeaderVersion = (uint)version,
                PageSize = 4096,
                VendorName = "test-device",
                VendorCmdline = "vendor.test=1",
                KernelLoadAddress = 0x8000,
                RamdiskLoadAddress = 0x1234,
                TagsLoadAddress = 0x5678,
                DtbAddress = 0x123456789abcdef
            },
            VendorRamdisk = [1, 2, 3, 4],
            Dtb = [5, 6],
            Bootconfig = version == 4 ? [7, 8] : []
        };
        if (version == 4) packer.RamdiskTable.Add(new()
        {
            RamdiskName = "platform",
            RamdiskOffset = 1,
            RamdiskSize = 3,
            RamdiskType = 1,
            RamdiskBoardId = Enumerable.Range(0, 16).Select(i => (uint)(i + 5)).ToArray()
        });
        packer.PackToFile(Image);
        if (application)
        {
            var result = await FirmwareService.Instance.UnpackBootAsync(Image, DirectoryPath, Cancellation);
            Assert.True(result.Success, result.Summary);
        }
        else
        {
            var result = await new BootImageFormatHandler().UnpackAsync(Image, DirectoryPath, cancellationToken: Cancellation);
            Assert.True(result.Success, result.Summary);
        }
        AssertNoLegacyFiles();
        Assert.True(PackingService.BuildImage(DirectoryPath, Output).Success);
        Assert.Equal(File.ReadAllBytes(Image), File.ReadAllBytes(Output));
        var metadata = RepackMetadata.Read(DirectoryPath)!;
        metadata.Configuration["vendorHeader"]!["vendorCmdline"] = "vendor.edited=1";
        if (version == 4) metadata.Configuration["ramdiskTable"]![0]!["ramdiskBoardId"]![3] = 123u;
        await metadata.SaveAsync(DirectoryPath, Cancellation);
        var packed = await FirmwareService.Instance.PackBootAsync(DirectoryPath, Output, Cancellation);
        Assert.True(packed.Success, packed.Summary);
        using var parser = new VendorBootParser();
        parser.ParseFile(Output);
        Assert.Equal("vendor.edited=1", parser.Header.VendorCmdline);
        Assert.Equal("test-device", parser.Header.VendorName);
        Assert.Equal(packer.Header.DtbAddress, parser.Header.DtbAddress);
        Assert.Equal(packer.VendorRamdisk, parser.VendorRamdisk);
        if (version == 4) Assert.Equal(123u, parser.RamdiskTable[0].RamdiskBoardId[3]);
    }

    [Fact]
    public async Task MissingComponent_AndInvalidConfiguration_LeaveOutputIntact()
    {
        CreateBoot(4);
        Assert.True((await new BootImageFormatHandler().UnpackAsync(Image, DirectoryPath, cancellationToken: Cancellation)).Success);
        File.Delete(Path.Combine(DirectoryPath, "ramdisk"));
        File.WriteAllText(Output, "keep");
        Assert.False((await FirmwareService.Instance.PackBootAsync(DirectoryPath, Output, Cancellation)).Success);
        Assert.Equal("keep", File.ReadAllText(Output));
        var metadata = RepackMetadata.Read(DirectoryPath)!;
        metadata.Configuration["magic"] = "unknown";
        await metadata.SaveAsync(DirectoryPath, Cancellation);
        Assert.False((await FirmwareService.Instance.PackBootAsync(DirectoryPath, Output, Cancellation)).Success);
        Assert.Equal("keep", File.ReadAllText(Output));
    }

    [Fact]
    public async Task Explorer_OpensExistingDirectoryConfiguration_EvenWhenTextNeedsRepair()
    {
        var node = new ExplorerNode(DirectoryPath, isDirectory: true);
        Assert.Null(node.RepackConfigurationPath);
        CreateBoot(2);
        Assert.True((await new BootImageFormatHandler().UnpackAsync(Image, DirectoryPath, cancellationToken: Cancellation)).Success);
        var path = Path.Combine(DirectoryPath, RepackMetadata.FileName);
        Assert.Equal(path, node.RepackConfigurationPath);
        Assert.Null(new ExplorerNode(DirectoryPath, isDirectory: false).RepackConfigurationPath);
        Assert.Null(new ExplorerNode(DirectoryPath, isDirectory: true, isPlaceholder: true).RepackConfigurationPath);
        var child = Directory.CreateDirectory(Path.Combine(DirectoryPath, "child")).FullName;
        Assert.Null(new ExplorerNode(child, isDirectory: true).RepackConfigurationPath);
        File.WriteAllText(path, "{unfinished edit");
        Assert.Equal(path, node.RepackConfigurationPath);
    }

    [Fact]
    public async Task ImagePanel_UsesUnifiedFormatWithoutSettings()
    {
        CreateBoot(2);
        Assert.True((await new BootImageFormatHandler().UnpackAsync(Image, DirectoryPath, cancellationToken: Cancellation)).Success);
        var result = PackingService.BuildImage(DirectoryPath, Output);
        Assert.True(result.Success, result.Summary);
        Assert.Equal(File.ReadAllBytes(Image), File.ReadAllBytes(Output));
    }

    [Fact]
    public async Task ExplicitContexts_ConcurrentBootRoundTrips_ConsumeTheirOwnEditedConfiguration()
    {
        CreateBoot(4);
        var previousRoot = RepackMetadata.DefaultMetadataRoot;
        var unrelated = Path.Combine(_root, "unrelated");
        RepackMetadata.DefaultMetadataRoot = unrelated;
        try
        {
            async Task RoundTrip(string name, bool local)
            {
                var directory = Path.Combine(_root, name, "boot");
                var context = local ? RepackMetadataContext.LocalDirectory
                    : RepackMetadataContext.FromProjectRoot(Path.Combine(_root, name, "metadata"));
                var result = await FirmwareService.Instance.UnpackBootAsync(Image, directory, Cancellation, context);
                Assert.True(result.Success, result.Summary);
                var metadata = Assert.IsType<RepackMetadata>(RepackMetadata.Read(directory, context));
                metadata.Configuration["header"]!["commandLine"] = name;
                var metadataPath = RepackMetadata.ResolveActualMetadataPath(directory, context)!;
                await metadata.SaveAsync(Path.GetDirectoryName(metadataPath)!, Cancellation);
                var output = Path.Combine(_root, name, "rebuilt.img");
                result = await FirmwareService.Instance.PackBootAsync(directory, output, Cancellation, context);
                Assert.True(result.Success, result.Summary);
                using var parser = new BootImageParser();
                parser.ParseFile(output);
                Assert.Equal(name, parser.Header.CommandLine);
                Assert.Equal(new byte[] { 4, 5, 6 }, parser.Ramdisk);
            }
            await Task.WhenAll(RoundTrip("project-a", false), RoundTrip("project-b", false), RoundTrip("local", true));
            Assert.False(Directory.Exists(unrelated));
        }
        finally { RepackMetadata.DefaultMetadataRoot = previousRoot; }
    }

    private void CreateBoot(int version)
    {
        new BootImagePacker
        {
            Header = new BootImageHeader
            {
                HeaderVersion = (BootImageVersion)version,
                PageSize = version < 3 ? 2048u : 4096u,
                HeaderSize = version == 4 ? 1584u : version == 3 ? 1580u : 1664u,
                CommandLine = "console=ttyS0",
                ImageName = "test",
                OsVersion = 0x123456,
                KernelAddr = 0x8000,
                RamdiskAddr = 0x100000,
                TagsAddr = 0x100,
                KernelLoadAddr = 0x123456789abcdef
            },
            Kernel = [1, 2, 3],
            Ramdisk = [4, 5, 6],
            Second = version < 3 ? [7, 8] : [],
            RecoveryDtbo = version is 1 or 2 ? [9, 10] : [],
            Dtb = version == 2 ? [11, 12] : [],
            Signature = version == 4 ? [13, 14] : []
        }.PackToFile(Image);
    }

    [Fact]
    public async Task ImagePanel_AutoPack_UsesMetadataRegardlessOfOutputExtension()
    {
        CreateBoot(4);
        Assert.True((await new BootImageFormatHandler().UnpackAsync(Image, DirectoryPath, cancellationToken: Cancellation)).Success);
        Assert.True(PackingService.BuildImage(DirectoryPath, Output + ".cpio").Success);
        Assert.Equal(File.ReadAllBytes(Image), File.ReadAllBytes(Output + ".cpio"));
    }

    [Fact]
    public async Task LargeComponent_StreamsBeyondPreviousAllocationLimit()
    {
        CreateBoot(4);
        Assert.True((await new BootImageFormatHandler().UnpackAsync(Image, DirectoryPath, cancellationToken: Cancellation)).Success);
        const long size = 257L * 1024 * 1024;
        using (var kernel = File.OpenWrite(Path.Combine(DirectoryPath, "kernel")))
        {
            kernel.SetLength(size);
            kernel.Position = size - 1;
            kernel.WriteByte(0x5a);
        }
        var result = await FirmwareService.Instance.PackBootAsync(DirectoryPath, Output, Cancellation);
        Assert.True(result.Success, result.Summary);
        using var image = File.OpenRead(Output);
        image.Position = 8;
        using var reader = new BinaryReader(image);
        Assert.Equal((uint)size, reader.ReadUInt32());
        image.Position = 4096 + size - 1;
        Assert.Equal(0x5a, image.ReadByte());
        image.Position = 4096 + size;
        Assert.Equal(4, image.ReadByte());
    }

    [Fact]
    public async Task EmptyRamdisk_DoesNotRequirePhantomComponent()
    {
        new BootImagePacker { Header = new() { HeaderVersion = BootImageVersion.V4, HeaderSize = 1584 }, Kernel = [1] }.PackToFile(Image);
        Assert.True((await new BootImageFormatHandler().UnpackAsync(Image, DirectoryPath, cancellationToken: Cancellation)).Success);
        Assert.False(File.Exists(Path.Combine(DirectoryPath, "ramdisk")));
        var result = await FirmwareService.Instance.PackBootAsync(DirectoryPath, Output, Cancellation);
        Assert.True(result.Success, result.Summary);
        Assert.Equal(File.ReadAllBytes(Image), File.ReadAllBytes(Output));
    }

    private void AssertNoLegacyFiles()
    {
        Assert.True(File.Exists(Path.Combine(DirectoryPath, RepackMetadata.FileName)));
        Assert.False(File.Exists(Path.Combine(DirectoryPath, "header")));
        Assert.False(File.Exists(Path.Combine(DirectoryPath, "header.json")));
        Assert.False(File.Exists(Path.Combine(DirectoryPath, "dtbo.json")));
    }
}