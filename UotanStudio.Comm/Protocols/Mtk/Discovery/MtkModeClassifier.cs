using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Protocols.Mtk.Discovery;

/// <summary>
/// The explained result of a descriptor-level MediaTek mode classification.
/// <para>描述符级联发科模式分类的、带依据的结果。</para>
/// </summary>
/// <param name="Mode">The classified stage, <see cref="MtkUsbMode.Unknown"/> when evidence is
/// missing or contradictory. <para>分类得到的阶段；证据缺失或矛盾时为 <see cref="MtkUsbMode.Unknown"/>。</para></param>
/// <param name="Reason">A localization-neutral explanation naming the decisive descriptors; never
/// empty. <para>指明决定性描述符的、与本地化无关的解释文本；永不为空。</para></param>
public sealed record MtkModeClassification(MtkUsbMode Mode, string Reason);

/// <summary>
/// Classifies a MediaTek USB endpoint into a boot stage from its VID/PID and per-interface bulk
/// endpoint shapes. The classifier is a pure function over observed descriptors: it never probes
/// the device, never orders candidates by enumeration position, and returns
/// <see cref="MtkUsbMode.Unknown"/> whenever evidence is missing or contradictory.
/// <para>依据 VID/PID 与逐接口 bulk 端点形状，将联发科 USB 端点分类到某个启动阶段。分类器是基于已观测
/// 描述符的纯函数：从不探测设备，从不按枚举位置排序候选项；证据缺失或矛盾时一律返回
/// <see cref="MtkUsbMode.Unknown"/>。</para>
/// </summary>
public static class MtkModeClassifier
{
    private const ushort MediaTekVid = 0x0E8D;
    private const ushort BootRomPid = 0x0003;
    private const ushort DownloadPid = 0x2000;
    private const ushort VcomPid = 0x2001;

    /// <summary>
    /// Classifies the device as Boot ROM, preloader, DA runtime, logging-only, agentless ADB, or
    /// unknown from its observed interface and endpoint metadata.
    /// <para>依据已观测的接口与端点元数据，将设备分类为 Boot ROM、preloader、DA 运行、仅日志、
    /// 无代理 ADB 或未知。</para>
    /// </summary>
    /// <param name="device">
    /// The enumerated USB device. <para>已枚举的 USB 设备。</para>
    /// </param>
    /// <returns>
    /// The stage plus the reason naming the decisive evidence.
    /// <para>阶段以及指明关键证据的原因。</para>
    /// </returns>
    public static MtkModeClassification Classify(UsbDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.VendorId != MediaTekVid)
        {
            return new MtkModeClassification(MtkUsbMode.Unknown,
                $"The vendor id 0x{device.VendorId:X4} is not the MediaTek vendor 0x0E8D.");
        }

        if (device.ProductId == BootRomPid)
        {
            return device.InterfaceMetadataObserved && device.Interfaces.Count > 0
                ? new MtkModeClassification(MtkUsbMode.BootRom,
                    $"The bootrom product id 0x{BootRomPid:X4} was observed together with {device.Interfaces.Count} enumerated interface(s).")
                : new MtkModeClassification(MtkUsbMode.BootRom,
                    $"The bootrom product id 0x{BootRomPid:X4} is unique to the Boot ROM stage; interface metadata was not observed.");
        }

        if (device.ProductId is not (DownloadPid or VcomPid))
        {
            return new MtkModeClassification(MtkUsbMode.Unknown,
                $"The product id 0x{device.ProductId:X4} is not a known MediaTek boot product id (0x0003, 0x2000, 0x2001).");
        }

        if (!device.InterfaceMetadataObserved || device.Interfaces.Count == 0)
        {
            return new MtkModeClassification(MtkUsbMode.Unknown,
                $"The preloader/DA product id 0x{device.ProductId:X4} presents several stages; interface metadata was not observed, so the stage cannot be derived.");
        }

        var commandPairs = 0;
        var loggingOnly = 0;
        var adbInterfaces = 0;
        foreach (var interfaceInfo in device.Interfaces)
        {
            if (interfaceInfo.Class == 0xFF && interfaceInfo.SubClass == 0x42 && interfaceInfo.Protocol == 0x01)
            {
                adbInterfaces++;
                continue;
            }

            if (interfaceInfo.Class != 0xFF)
            {
                continue;
            }

            var bulkIn = interfaceInfo.Endpoints.Count(static endpoint => IsBulk(endpoint) && endpoint.IsIn);
            var bulkOut = interfaceInfo.Endpoints.Count(static endpoint => IsBulk(endpoint) && !endpoint.IsIn);
            if (bulkIn > 0 && bulkOut > 0)
            {
                commandPairs++;
            }
            else if (bulkIn > 0)
            {
                loggingOnly++;
            }
        }

        if (commandPairs > 1)
        {
            return new MtkModeClassification(MtkUsbMode.Unknown,
                $"{commandPairs} vendor bulk command pairs were observed; the command interface is ambiguous and must not be resolved by enumeration order.");
        }

        if (commandPairs == 1 && adbInterfaces > 0)
        {
            return new MtkModeClassification(MtkUsbMode.Unknown,
                "A vendor bulk command pair and an ADB debug-bridge interface were both observed; the stage evidence conflicts.");
        }

        if (commandPairs == 1)
        {
            return loggingOnly > 0
                ? new MtkModeClassification(MtkUsbMode.DaRuntime,
                    $"One vendor bulk command pair plus {loggingOnly} logging-only interface(s) were observed on product id 0x{device.ProductId:X4}; the connection-agent query still cross-checks this descriptor-level stage.")
                : new MtkModeClassification(MtkUsbMode.Preloader,
                    $"Exactly one vendor bulk command pair and no logging interface were observed on product id 0x{device.ProductId:X4}; a running DA on the same product id cannot be excluded without the connection-agent query.");
        }

        if (adbInterfaces > 0)
        {
            return new MtkModeClassification(MtkUsbMode.AgentlessAdb,
            $"Only the ff/42/01 ADB debug-bridge interface(s) ({adbInterfaces}) were observed; no vendor bulk command pair is present.");
        }

        if (loggingOnly > 0)
        {
            return new MtkModeClassification(MtkUsbMode.LoggingOnly,
                $"{loggingOnly} logging-only vendor interface(s) (bulk IN without a paired bulk OUT) were observed and no command pair exists.");
        }

        return new MtkModeClassification(MtkUsbMode.Unknown,
            $"Product id 0x{device.ProductId:X4} exposes {device.Interfaces.Count} interface(s) without any bulk command, logging, or ADB shape.");
    }

    internal static bool IsBulk(UsbEndpointInfo endpoint) => (endpoint.Attributes & 0x03) == 0x02;
}