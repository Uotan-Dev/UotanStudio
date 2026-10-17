using UotanStudio.Firmware.BootSecurity.BootImage;
using UotanStudio.Firmware.BootSecurity.BootImage.Models;
using UotanStudio.Firmware.BootSecurity.BootImage.Parser;
using UotanStudio.Firmware.BootSecurity.Workflow;
using Xunit;

namespace UotanStudio.Tests.Firmware;

public sealed class BootCmdlineServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uot-cmdline-" + Guid.NewGuid().ToString("N"));

    public BootCmdlineServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void CreatePlan_ExistingToken_BuildsPlanWithoutTouchingSource()
    {
        var image = CreateBoot("console=ttyS0 androidboot.selinux=enforcing");

        var plan = new BootCmdlineService().CreatePlan(image, "androidboot.selinux", "permissive");

        Assert.Equal("console=ttyS0 androidboot.selinux=enforcing", plan.OriginalCommandLine);
        Assert.Equal("console=ttyS0 androidboot.selinux=permissive", plan.UpdatedCommandLine);
        Assert.Equal("androidboot.selinux", plan.Key);
        Assert.Equal("permissive", plan.Value);
        Assert.NotEmpty(plan.Limitations);
        Assert.Equal("console=ttyS0 androidboot.selinux=enforcing", ReadCommandLine(image));
    }

    [Fact]
    public void CreatePlan_DuplicateAndDifferentlyCasedTokens_CollapsesToOneReplacement()
    {
        var image = CreateBoot("console=ttyS0 Androidboot.selinux=off androidboot.selinux=enforcing");

        var plan = new BootCmdlineService().CreatePlan(image, "androidboot.selinux", "permissive");

        Assert.Equal("console=ttyS0 androidboot.selinux=permissive", plan.UpdatedCommandLine);
    }

    [Fact]
    public void CreatePlan_BlankValue_PlanRemovesParameter()
    {
        var image = CreateBoot("console=ttyS0 androidboot.selinux=enforcing");

        var plan = new BootCmdlineService().CreatePlan(image, "androidboot.selinux", "   ");

        Assert.Null(plan.Value);
        Assert.Equal("console=ttyS0", plan.UpdatedCommandLine);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("androidboot selinux")]
    [InlineData("androidboot.selinux=enforcing")]
    public void CreatePlan_InvalidParameterName_Throws(string key)
    {
        var image = CreateBoot("console=ttyS0");

        Assert.Throws<ArgumentException>(() => new BootCmdlineService().CreatePlan(image, key, "permissive"));
    }

    [Fact]
    public void CreatePlan_ParameterNameBeyondLengthBound_Throws()
    {
        var image = CreateBoot("console=ttyS0");

        Assert.Throws<ArgumentException>(() => new BootCmdlineService()
            .CreatePlan(image, "a" + new string('x', BootCmdlineService.MaximumKeyLength), "permissive"));
    }

    [Theory]
    [InlineData("permissive enforcing")]
    [InlineData("a\tb")]
    [InlineData("value\nwith\nnewlines")]
    public void CreatePlan_InvalidValue_Throws(string value)
    {
        var image = CreateBoot("console=ttyS0");

        Assert.Throws<ArgumentException>(() => new BootCmdlineService().CreatePlan(image, "androidboot.selinux", value));
    }

    [Fact]
    public async Task ApplyAsync_WritesEditedHeaderAndKeepsPayload()
    {
        var image = CreateBoot("console=ttyS0 androidboot.mode=normal");
        var output = Path.Combine(_root, "edited.img");
        var service = new BootCmdlineService();

        var plan = service.CreatePlan(image, "androidboot.mode", "charger");
        await service.ApplyAsync(plan, output, TestContext.Current.CancellationToken);

        Assert.Equal("console=ttyS0 androidboot.mode=charger", ReadCommandLine(output));
        Assert.Equal("console=ttyS0 androidboot.mode=normal", ReadCommandLine(image));
        var parser = new BootImageParser();
        try
        {
            parser.ParseFile(output);
            Assert.Equal([1, 2, 3], parser.Kernel);
        }
        finally
        {
            parser.Dispose();
        }
    }

    [Fact]
    public async Task ApplyAsync_CommandLineBeyondHeaderCapacity_ThrowsAndWritesNothing()
    {
        var image = CreateBoot("console=ttyS0 " + new string('k', 600));
        var output = Path.Combine(_root, "overflow.img");
        var service = new BootCmdlineService();
        var plan = service.CreatePlan(image, "androidboot.filler", new string('v', BootCmdlineService.MaximumValueLength));

        await Assert.ThrowsAsync<ArgumentException>(() => service.ApplyAsync(plan, output, TestContext.Current.CancellationToken));

        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task ApplyAsync_Cancelled_ThrowsAndLeavesNoOutput()
    {
        var image = CreateBoot("console=ttyS0");
        var output = Path.Combine(_root, "cancelled.img");
        var service = new BootCmdlineService();
        var plan = service.CreatePlan(image, "androidboot.selinux", "permissive");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ApplyAsync(plan, output, cancellation.Token));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void CreatePlan_MissingBootImage_ThrowsFileNotFound()
    {
        Assert.Throws<FileNotFoundException>(() => new BootCmdlineService()
            .CreatePlan(Path.Combine(_root, "absent.img"), "androidboot.selinux", "permissive"));
    }

    [Fact]
    public void CreatePlan_UnparsableImage_ThrowsImageParseException()
    {
        var path = Path.Combine(_root, "garbage.img");
        File.WriteAllBytes(path, new byte[4096]);

        Assert.Throws<ImageParseException>(() => new BootCmdlineService()
            .CreatePlan(path, "androidboot.selinux", "permissive"));
    }

    private string CreateBoot(string commandLine, string name = "boot.img")
    {
        var path = Path.Combine(_root, name);
        new BootImagePacker
        {
            Header = new BootImageHeader
            {
                HeaderVersion = BootImageVersion.V3,
                PageSize = 4096u,
                HeaderSize = 1580u,
                CommandLine = commandLine,
                ImageName = "test",
                OsVersion = 0x123456,
                KernelAddr = 0x8000,
                RamdiskAddr = 0x100000,
                TagsAddr = 0x100,
                KernelLoadAddr = 0x123456789abcdef
            },
            Kernel = [1, 2, 3]
        }.PackToFile(path);
        return path;
    }

    private static string ReadCommandLine(string path)
    {
        using var parser = new BootImageParser();
        parser.ParseFile(path);
        return parser.Header.CommandLine ?? string.Empty;
    }

    public void Dispose() => Directory.Delete(_root, true);
}