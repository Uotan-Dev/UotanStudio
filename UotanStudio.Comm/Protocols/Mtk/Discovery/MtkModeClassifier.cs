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
    private const ushort LgVid = 0x1004;
    private const ushort OppoVid = 0x22D9;
    private const ushort SonyVid = 0x0FCE;

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
            return ClassifyOemEndpoint(device);
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

    /// <summary>
    /// Resolves an OEM rebranded MediaTek download endpoint into a concrete boot stage. The reference client
    /// filters enumeration to vendor ids <c>0x0E8D</c>, <c>0x1004</c>, <c>0x22d9</c> and <c>0x0FCE</c>
    /// (<c>mtkclient/Library/Connection/usblib.py</c>, line 307), and the product table
    /// (<c>mtkclient/config/usb_ids.py</c>, lines 8-14) names each OEM product's stage: LG
    /// <c>0x1004:0x6000</c> and OPPO <c>0x22d9:0x0006</c> are labelled preloader ports, while Sony
    /// <c>0x0FCE:0xF200</c> (line 10), <c>0xD1E9</c> (line 11), <c>0xD1E2</c> (line 12) and <c>0xD1EC</c>
    /// (line 13) are labelled bootrom ports.
    /// <para>将 OEM 换牌的联发科下载端点解析为具体启动阶段。参考客户端把枚举过滤限定在厂商
    /// <c>0x0E8D</c>、<c>0x1004</c>、<c>0x22d9</c> 与 <c>0x0FCE</c>
    /// （<c>mtkclient/Library/Connection/usblib.py</c> 第 307 行），而产品表
    /// （<c>mtkclient/config/usb_ids.py</c> 第 8-14 行）指明了每个 OEM 产品的阶段：LG
    /// <c>0x1004:0x6000</c> 与 OPPO <c>0x22d9:0x0006</c> 标注为 preloader 端口，而 Sony
    /// <c>0x0FCE:0xF200</c>（第 10 行）、<c>0xD1E9</c>（第 11 行）、<c>0xD1E2</c>（第 12 行）与
    /// <c>0xD1EC</c>（第 13 行）标注为 bootrom 端口。</para>
    /// </summary>
    /// <remarks>
    /// <para>Sony <c>0x0FCE:0xD1DD</c> (line 14) is the one OEM product whose reference comment names only a device
    /// model (<c>Sony F3111</c>) rather than a boot stage; it shares the same sentinel value and the same Sony
    /// bootrom group as the four explicitly labelled Brom ports, so it is classified as Boot ROM on that grouping
    /// while the model-only comment is preserved here as the weaker evidence. Every other product id under these
    /// OEM vendors is outside the reference port table and stays <see cref="MtkUsbMode.Unknown"/>; no mapping is
    /// invented for it.</para>
    /// <para>Sony <c>0x0FCE:0xD1DD</c>（第 14 行）是唯一参考注释只写设备型号（<c>Sony F3111</c>）而非启动阶段的
    /// OEM 产品；它与四个明确标注为 Brom 的端口共享同一哨兵值与同一 Sony bootrom 分组，因此按该分组分类为 Boot ROM，
    /// 同时在此保留型号注释作为较弱证据。这些 OEM 厂商下的其它产品 ID 都不在参考端口表内，保持
    /// <see cref="MtkUsbMode.Unknown"/>，不为其臆造映射。</para>
    /// </remarks>
    /// <param name="device">The enumerated OEM USB device. <para>已枚举的 OEM USB 设备。</para></param>
    /// <returns>The boot stage plus the reason naming the decisive vendor and product ids.
    /// <para>启动阶段以及指明关键厂商与产品 ID 的原因。</para></returns>
    private static MtkModeClassification ClassifyOemEndpoint(UsbDeviceInfo device)
    {
        if (device.VendorId is not (LgVid or OppoVid or SonyVid))
        {
            return new MtkModeClassification(MtkUsbMode.Unknown,
                $"The vendor id 0x{device.VendorId:X4} is not the MediaTek vendor 0x0E8D.");
        }

        return (device.VendorId, device.ProductId) switch
        {
            (LgVid, 0x6000) => new MtkModeClassification(MtkUsbMode.Preloader,
                $"The LG download port 0x{device.VendorId:X4}:0x{device.ProductId:X4} is recorded as a preloader port in the reference product table (usb_ids.py, line 8)."),
            (OppoVid, 0x0006) => new MtkModeClassification(MtkUsbMode.Preloader,
                $"The OPPO download port 0x{device.VendorId:X4}:0x{device.ProductId:X4} is recorded as a preloader port in the reference product table (usb_ids.py, line 9)."),
            (SonyVid, 0xF200 or 0xD1E9 or 0xD1E2 or 0xD1EC or 0xD1DD) => new MtkModeClassification(MtkUsbMode.BootRom,
                $"The Sony download port 0x{device.VendorId:X4}:0x{device.ProductId:X4} is recorded among the bootrom ports in the reference product table (usb_ids.py, lines 10-14)."),
            _ => new MtkModeClassification(MtkUsbMode.Unknown,
                $"The vendor id 0x{device.VendorId:X4} is an OEM MediaTek rebranding vendor, but the product id 0x{device.ProductId:X4} is not a documented OEM download port (LG 0x6000, OPPO 0x0006, Sony 0xF200, 0xD1E9, 0xD1E2, 0xD1EC, 0xD1DD).")
        };
    }
}