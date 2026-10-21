using UotanStudio.Services.Devices;
using UotanStudio.ViewModels.Nodes;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// Aggregate specs for the MediaTek and OpenHarmony HDC protocol nodes. The specs feed the built-in catalog through
/// <see cref="CreateDescriptors"/> and do not register themselves.
/// <para>联发科与 OpenHarmony HDC 协议节点的汇总规格。规格经 <see cref="CreateDescriptors"/> 进入内置目录，自身不执行注册。</para>
/// </summary>
public static class MtkHdcNodeSpecs
{
    /// <summary>
    /// Every MediaTek and HDC node spec, in stable type-key order.
    /// <para>全部联发科与 HDC 节点规格，按稳定类型键顺序排列。</para>
    /// </summary>
    public static IReadOnlyList<DeviceProtocolNodeSpec> All { get; } =
    [
        new("Device.Hdc.Disconnect", DeviceProtocolKind.Hdc,
            NodeCapability.RequiresDevice | NodeCapability.MutatesDevice, HdcDisconnectNodeViewModel.CreateDisconnectNode),
        new("Device.Hdc.Forward", DeviceProtocolKind.Hdc,
            NodeCapability.RequiresDevice | NodeCapability.PatchSafe, HdcForwardNodeViewModel.CreateForwardNode),
        new("Device.Hdc.Install", DeviceProtocolKind.Hdc,
            NodeCapability.RequiresDevice | NodeCapability.MutatesDevice, HdcInstallNodeViewModel.CreateInstallNode),
        new("Device.Hdc.Property", DeviceProtocolKind.Hdc,
            NodeCapability.RequiresDevice | NodeCapability.ReadsDevice | NodeCapability.PatchSafe,
            HdcPropertyNodeViewModel.CreatePropertyNode),
        new("Device.Hdc.Pull", DeviceProtocolKind.Hdc,
            NodeCapability.RequiresDevice | NodeCapability.ReadsDevice | NodeCapability.PatchSafe,
            HdcPullNodeViewModel.CreatePullNode),
        new("Device.Hdc.Push", DeviceProtocolKind.Hdc,
            NodeCapability.RequiresDevice | NodeCapability.MutatesDevice, HdcPushNodeViewModel.CreatePushNode),
        new("Device.Hdc.Remount", DeviceProtocolKind.Hdc,
            NodeCapability.RequiresDevice | NodeCapability.MutatesDevice, HdcRemountNodeViewModel.CreateRemountNode),
        new("Device.Hdc.Shell", DeviceProtocolKind.Hdc,
            NodeCapability.RequiresDevice | NodeCapability.MutatesDevice, HdcShellNodeViewModel.CreateShellNode),
        new("Device.Hdc.Uninstall", DeviceProtocolKind.Hdc,
            NodeCapability.RequiresDevice | NodeCapability.MutatesDevice, HdcUninstallNodeViewModel.CreateUninstallNode),
        new("Device.Hdc.Wait", DeviceProtocolKind.Hdc,
            NodeCapability.RequiresDevice | NodeCapability.ReadsDevice | NodeCapability.PatchSafe,
            HdcWaitNodeViewModel.CreateWaitNode),
        new("Device.Mtk.Operation", DeviceProtocolKind.Mtk,
            NodeCapability.RequiresDevice | NodeCapability.MutatesDevice, MtkDeviceOperationNodeViewModel.CreateOperationNode),
        new("Device.Mtk.Session", DeviceProtocolKind.Mtk,
            NodeCapability.RequiresDevice | NodeCapability.ReadsDevice | NodeCapability.PatchSafe,
            MtkDeviceSessionNodeViewModel.CreateSessionNode),
        new("Device.Mtk.Wait", DeviceProtocolKind.Mtk,
            NodeCapability.RequiresDevice | NodeCapability.ReadsDevice | NodeCapability.PatchSafe,
            MtkWaitNodeViewModel.CreateWaitNode)
    ];

    /// <summary>
    /// Builds catalog descriptors for every spec without registering them.
    /// <para>为全部规格构建目录描述符但不注册。</para>
    /// </summary>
    /// <returns>The descriptors mirroring each spec's key, factory, capabilities and confirmation flag.<para>与各规格的类型键、工厂、能力与确认标志一致的描述符。</para></returns>
    public static IReadOnlyList<NodeDescriptor> CreateDescriptors() =>
        [.. All.Select(static spec => spec.ToCatalogDescriptor())];
}