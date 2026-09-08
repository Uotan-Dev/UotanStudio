using System.Globalization;
using UotanStudio.Services.Devices;
using UotanStudio.Services.Execution;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>HDC operations with stable typed workflow ports.
/// <para>具有稳定类型端口的 HDC 工作流操作。</para></summary>
public sealed class HdcNodeViewModel : FlowNodeViewModel
{
    /// <summary>Registered HDC operation names. 已注册 HDC 操作名称。</summary>
    public static IReadOnlyList<string> Operations { get; } =
        ["Shell", "GetProperty", "Send", "Receive", "Install", "Uninstall", "Forward", "Reverse", "ListForwards", "Remount"];

    private HdcNodeViewModel(string operation) : base("HDC " + operation, NodeCategory.Flash)
        => TypeKey = "HDC." + operation;

    /// <summary>Creates an operation and its typed input/output ports. 创建操作及其类型化输入输出端口。</summary>
    public static HdcNodeViewModel Create(string operation)
    {
        if (!Operations.Contains(operation)) throw new ArgumentException("Unknown HDC operation.", nameof(operation));
        var node = new HdcNodeViewModel(operation);
        node.Input.Add(new() { Title = "Device", PortType = NodePortType.Device });
        void Input(string title, NodePortType type) => node.Input.Add(new() { Title = title, PortType = type });
        switch (operation)
        {
            case "Shell": Input("Command", NodePortType.Text); break;
            case "GetProperty": Input("Name", NodePortType.Text); break;
            case "Send":
            case "Receive": Input("Local Path", NodePortType.Path); Input("Remote Path", NodePortType.Text); break;
            case "Install": Input("Package", NodePortType.Path); break;
            case "Uninstall": Input("Bundle", NodePortType.Text); break;
            case "Forward":
            case "Reverse": Input("Local Port", NodePortType.Number); Input("Remote Port", NodePortType.Number); break;
        }
        node.Output.Add(new() { Title = "Result", PortType = NodePortType.Text });
        return node;
    }

    /// <summary>Executes by TypeKey; failures and cancellation propagate to the engine.
    /// <para>按 TypeKey 执行，失败与取消传播给引擎。</para></summary>
    public override async Task ExecuteAsync(INodeExecutionContext context)
    {
        var device = NodeDataUtil.ToDevice(ReadInput("Device"))
            ?? throw new InvalidOperationException("An HDC device input is required.");
        var service = HdcDeviceService.FromSettings();
        var ct = context.CancellationToken;
        ct.ThrowIfCancellationRequested();
        string Text(string port) => Convert.ToString(ReadInput(port), CultureInfo.InvariantCulture)
            ?? throw new ArgumentException($"Missing {port} input.");
        int Port(string name) => int.Parse(Text(name), CultureInfo.InvariantCulture);
        var result = TypeKey switch
        {
            "HDC.Shell" => await service.ShellAsync(device, Text("Command"), ct).ConfigureAwait(false),
            "HDC.GetProperty" => await service.GetPropertyAsync(device, Text("Name"), ct).ConfigureAwait(false),
            "HDC.Send" => await service.SendFileAsync(device, Text("Local Path"), Text("Remote Path"), ct).ConfigureAwait(false),
            "HDC.Receive" => await service.ReceiveFileAsync(device, Text("Remote Path"), Text("Local Path"), ct).ConfigureAwait(false),
            "HDC.Install" => await service.InstallAsync(device, Text("Package"), ct).ConfigureAwait(false),
            "HDC.Uninstall" => await service.UninstallAsync(device, Text("Bundle"), ct).ConfigureAwait(false),
            "HDC.Forward" => await service.ForwardAsync(device, Port("Local Port"), Port("Remote Port"), ct: ct).ConfigureAwait(false),
            "HDC.Reverse" => await service.ForwardAsync(device, Port("Local Port"), Port("Remote Port"), reverse: true, ct: ct).ConfigureAwait(false),
            "HDC.ListForwards" => await service.RunTaskAsync(device, ["fport", "ls"], ct).ConfigureAwait(false),
            "HDC.Remount" => await service.RunTaskAsync(device, ["target", "mount"], ct).ConfigureAwait(false),
            _ => throw new NotSupportedException(TypeKey)
        };
        WriteOutput("Result", result);
        context.ReportProgress(1);
    }
}
