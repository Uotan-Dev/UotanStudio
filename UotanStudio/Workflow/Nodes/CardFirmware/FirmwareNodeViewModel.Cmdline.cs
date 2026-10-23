using Avalonia;
using UotanStudio.Firmware.BootSecurity.Workflow;
using UotanStudio.Services;
using UotanStudio.Workflow.Execution;

namespace UotanStudio.Workflow.Nodes;

public partial class FirmwareNodeViewModel
{
    /// <summary>Creates the boot kernel command line edit node.<para>创建 boot 内核命令行编辑节点。</para></summary>
    /// <returns>The configured command line edit node with its ports and defaults.<para>配置好输入输出端口与默认参数的命令行编辑节点。</para></returns>
    public static FirmwareNodeViewModel CreateCmdlineEditNode()
    {
        var node = new FirmwareNodeViewModel("Boot Cmdline")
        {
            Location = new Point(100, 100),
            TypeKey = "Boot.CommandLine.Edit"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Image", PortType = NodePortType.Path });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Parameter",
            PortType = NodePortType.Text,
            InitialValue = "androidboot.selinux"
        });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Value",
            PortType = NodePortType.Text,
            InitialValue = "permissive"
        });
        node.Input.Add(new NodeConnectorViewModel { Title = "Output Path", PortType = NodePortType.Path });
        node.Output.Add(new NodeConnectorViewModel { Title = "Boot Image", PortType = NodePortType.Path });
        node.Output.Add(new NodeConnectorViewModel { Title = "Report", PortType = NodePortType.Text });
        return node;
    }

    private async Task ExecuteCmdlineEditAsync(INodeExecutionContext context)
    {
        var path = PathText(ReadInput("Image"));
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException("Boot image input was not found.", path);
        }

        var output = PathText(ReadInput("Output Path"));
        if (string.IsNullOrWhiteSpace(output))
        {
            throw new ArgumentException("Output Path is required so the source boot image is never overwritten.");
        }

        var parameter = ReadInput("Parameter")?.ToString();
        var value = ReadInput("Value")?.ToString();
        var service = new BootCmdlineService();
        var plan = service.CreatePlan(path, parameter ?? string.Empty, string.IsNullOrWhiteSpace(value) ? null : value.Trim());
        await service.ApplyAsync(plan, output, context.CancellationToken).ConfigureAwait(false);
        var report = $"{plan.Key}={(plan.Value is null ? "<removed>" : plan.Value)}  {plan.OriginalCommandLine}  ->  {plan.UpdatedCommandLine}";
        WriteOutput("Boot Image", output);
        WriteOutput("Report", report);
        context.Log.Write(LogLevel.Info, Title, report);
    }
}