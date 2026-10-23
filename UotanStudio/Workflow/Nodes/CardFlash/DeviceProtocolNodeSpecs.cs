using UotanStudio.Workflow.Registry;

namespace UotanStudio.Workflow.Nodes;

/// <summary>
/// Aggregate specs for the EDL, RKDL, ADB, wait and Fastboot protocol nodes in this folder. The built-in catalog
/// registers them through <see cref="CreateDescriptors"/>.
/// <para>本目录的 EDL、RKDL、ADB、等待与 Fastboot 协议节点汇总规格。内置目录经 <see cref="CreateDescriptors"/> 注册它们。</para>
/// </summary>
public static class DeviceProtocolNodeSpecs
{
    /// <summary>
    /// Every EDL, RKDL, ADB, wait and Fastboot protocol node spec, in stable type-key order.
    /// <para>全部 EDL、RKDL、ADB、等待与 Fastboot 协议节点规格，按稳定类型键顺序排列。</para>
    /// </summary>
    public static IReadOnlyList<DeviceProtocolNodeSpec> All { get; } =
    [
        .. DeviceEdlNodeViewModel.Specs,
        .. DeviceRkdlNodeViewModel.Specs,
        .. DeviceAdbNodeViewModel.Specs,
        .. DeviceWaitNodeViewModel.Specs,
        .. DeviceFastbootNodeViewModel.Specs
    ];

    /// <summary>
    /// Builds catalog descriptors for every spec without registering them.
    /// <para>为全部规格构建目录描述符但不注册。</para>
    /// </summary>
    /// <returns>The descriptors mirroring each spec's key, factory, capabilities and confirmation flag.
    /// <para>与各规格的类型键、工厂、能力与确认标志一致的描述符。</para></returns>
    public static IReadOnlyList<NodeDescriptor> CreateDescriptors() =>
        [.. All.Select(static spec => spec.ToCatalogDescriptor())];
}