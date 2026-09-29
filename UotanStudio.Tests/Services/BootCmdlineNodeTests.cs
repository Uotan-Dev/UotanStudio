using UotanStudio.Firmware.BootSecurity.BootImage;
using UotanStudio.Firmware.BootSecurity.BootImage.Models;
using UotanStudio.Firmware.BootSecurity.BootImage.Parser;
using UotanStudio.Services;
using UotanStudio.Services.Execution;
using UotanStudio.ViewModels.NodeViewModels;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class BootCmdlineNodeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uot-cmdline-node-" + Guid.NewGuid().ToString("N"));

    public BootCmdlineNodeTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ExecuteAsync_EditExistingParameter_RepacksImageAndReportsBothLines()
    {
        var source = CreateBoot("console=ttyS0 androidboot.veritymode=enforcing");
        var output = Path.Combine(_root, "edited.img");
        var node = FirmwareNodeViewModel.CreateCmdlineEditNode();

        SetInput(node, "Image", source);
        SetInput(node, "Parameter", "androidboot.veritymode");
        SetInput(node, "Value", "logging");
        SetInput(node, "Output Path", output);
        await node.ExecuteAsync(new TestExecutionContext(TestContext.Current.CancellationToken));

        Assert.Equal(output, GetOutput(node, "Boot Image"));
        using var parser = new BootImageParser();
        parser.ParseFile(output);
        Assert.Equal("console=ttyS0 androidboot.veritymode=logging", parser.Header.CommandLine);
        Assert.Contains("androidboot.veritymode=logging", (string)GetOutput(node, "Report")!);
    }

    [Fact]
    public async Task ExecuteAsync_BlankValue_RemovesParameter()
    {
        var source = CreateBoot("console=ttyS0 androidboot.selinux=enforcing");
        var output = Path.Combine(_root, "removed.img");
        var node = FirmwareNodeViewModel.CreateCmdlineEditNode();

        SetInput(node, "Image", source);
        SetInput(node, "Parameter", "androidboot.selinux");
        SetInput(node, "Value", "  ");
        SetInput(node, "Output Path", output);
        await node.ExecuteAsync(new TestExecutionContext(TestContext.Current.CancellationToken));

        using var parser = new BootImageParser();
        parser.ParseFile(output);
        Assert.Equal("console=ttyS0", parser.Header.CommandLine);
        Assert.Contains("<removed>", (string)GetOutput(node, "Report")!);
    }

    [Fact]
    public async Task ExecuteAsync_MissingOutputPath_FailsWithoutWriting()
    {
        var source = CreateBoot("console=ttyS0");
        var node = FirmwareNodeViewModel.CreateCmdlineEditNode();

        SetInput(node, "Image", source);
        SetInput(node, "Parameter", "androidboot.selinux");
        SetInput(node, "Value", "permissive");

        await Assert.ThrowsAsync<ArgumentException>(() => node.ExecuteAsync(new TestExecutionContext(TestContext.Current.CancellationToken)));
    }

    private string CreateBoot(string commandLine)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".img");
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

    private static void SetInput(FlowNodeViewModel node, string title, object value)
        => Assert.Single(node.Input, port => port.Title == title).Data = value;

    private static object? GetOutput(FlowNodeViewModel node, string title)
        => Assert.Single(node.Output, port => port.Title == title).Data;

    public void Dispose() => Directory.Delete(_root, true);

    private sealed class TestExecutionContext(CancellationToken cancellationToken) : INodeExecutionContext, IUnifiedLog
    {
        public IUnifiedLog Log => this;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public void ReportProgress(double progress) { }
        public void Write(LogLevel level, string source, string message) { }
        public void Write(LogLevel level, string source, string format, params object[] args) { }
        public void SetMinimumLevel(LogLevel level) { }
    }
}