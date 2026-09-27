using UotanStudio.Comm.Transports.Usb.Abstractions;

namespace UotanStudio.Comm.Protocols.Mtk.Discovery;

/// <summary>
/// The terminal state of a command-interface selection attempt.
/// <para>命令接口选择尝试的终态。</para>
/// </summary>
public enum MtkCommandInterfaceStatus
{
    /// <summary>
    /// Exactly one interface passed every shape, class, and bound-endpoint check; its
    /// <see cref="Transports.Usb.Abstractions.UsbInterfaceInfo.InterfaceNumber"/> is authoritative.
    /// <para>恰好有一个接口通过全部形状、类与绑定端点检查；其
    /// <see cref="Transports.Usb.Abstractions.UsbInterfaceInfo.InterfaceNumber"/> 即为权威结果。</para>
    /// </summary>
    Selected,

    /// <summary>
    /// The backend reported no per-interface metadata, so no positive or negative claim is made and
    /// the caller's prior explicit interface selection is honoured.
    /// <para>后端未报告逐接口元数据，因此不作任何肯定或否定断言，沿用调用方先前的显式接口选择。</para>
    /// </summary>
    NoMetadata,

    /// <summary>
    /// No vendor bulk command pair exists on the endpoint.
    /// <para>端点上不存在任何厂商 bulk 命令对。</para>
    /// </summary>
    NoCommandInterface,

    /// <summary>
    /// Only logging-shaped vendor interfaces (bulk IN without a paired bulk OUT) were found;
    /// commands must never be sent to them.
    /// <para>仅找到日志形状的厂商接口（bulk-IN 且无配对 bulk-OUT）；绝不得向其发送命令。</para>
    /// </summary>
    LoggingOnly,

    /// <summary>
    /// Several interfaces passed the command shape without a decisive filter; binding one by
    /// enumeration order is forbidden, so the caller must disambiguate via
    /// <see cref="MtkOpenOptions.CommandInterfaceSubClass"/> or an explicit interface choice.
    /// <para>多个接口通过命令形状检查且无决定性过滤条件；禁止按枚举顺序绑定，
    /// 调用方必须通过 <see cref="MtkOpenOptions.CommandInterfaceSubClass"/> 或显式接口选择消除歧义。</para>
    /// </summary>
    Ambiguous,

    /// <summary>
    /// The endpoints actually claimed by the opened USB session belong to no candidate command
    /// interface, so the session is bound to the wrong interface.
    /// <para>已打开 USB 会话实际占用的端点不属于任何候选命令接口，说明会话绑定在了错误的接口上。</para>
    /// </summary>
    BoundEndpointMismatch
}

/// <summary>
/// The explained outcome of selecting the MTK boot command interface from observed descriptors.
/// <para>依据已观测描述符选择 MTK boot 命令接口的、带依据的结果。</para>
/// </summary>
/// <param name="Status">The terminal state of the attempt. <para>尝试的终态。</para></param>
/// <param name="InterfaceNumber">The selected interface number, or <see langword="null"/> unless
/// <paramref name="Status"/> is <see cref="MtkCommandInterfaceStatus.Selected"/>.
/// <para>选定的接口编号；除非 <paramref name="Status"/> 为 <see cref="MtkCommandInterfaceStatus.Selected"/>，否则为 <see langword="null"/>。</para></param>
/// <param name="Reason">A localization-neutral explanation naming the decisive descriptors; never
/// empty. <para>指明决定性描述符的、与本地化无关的解释文本；永不为空。</para></param>
public sealed record MtkCommandInterfaceResult(
    MtkCommandInterfaceStatus Status,
    byte? InterfaceNumber,
    string Reason);

/// <summary>
/// Selects the single vendor-specific bulk command interface (one bulk IN plus one bulk OUT)
/// from observed USB descriptors. Logging-shaped interfaces are excluded explicitly, candidates
/// are never resolved by enumeration order, and optionally the endpoints actually claimed by the
/// opened session must belong to the selected interface. The policy is a pure function; it opens
/// nothing and sends nothing.
/// <para>从已观测的 USB 描述符中选出唯一的厂商特定 bulk 命令接口（一个 bulk-IN 加一个 bulk-OUT）。
/// 日志形状接口会被显式排除，候选项绝不按枚举顺序裁决，并可选择要求已打开会话实际占用的端点
/// 属于被选接口。该策略为纯函数；不打开、不发送任何内容。</para>
/// </summary>
public static class MtkCommandInterfacePolicy
{
    /// <summary>
    /// Evaluates the descriptor evidence and reports the one command interface, or the reason no
    /// unambiguous selection was possible.
    /// <para>评估描述符证据，报告唯一命令接口，或无法做出无歧义选择的原因。</para>
    /// </summary>
    /// <param name="device">
    /// The enumerated USB device whose interfaces are evaluated. <para>被评估接口的所属已枚举 USB 设备。</para>
    /// </param>
    /// <param name="options">
    /// Optional class/subclass filters; <see langword="null"/> applies the vendor-specific
    /// <c>0xFF</c> class default. <para>可选的类/子类过滤；为 <see langword="null"/> 时应用厂商特定 <c>0xFF</c> 类默认值。</para>
    /// </param>
    /// <param name="boundEndpointIn">
    /// The bulk IN endpoint address actually claimed by the opened session, or <c>0</c> when the
    /// backend does not expose endpoint-level binding. <para>已打开会话实际占用的 bulk-IN 端点地址；后端不暴露端点绑定时为 <c>0</c>。</para>
    /// </param>
    /// <param name="boundEndpointOut">
    /// The bulk OUT endpoint address actually claimed by the opened session, or <c>0</c> when the
    /// backend does not expose endpoint-level binding. <para>已打开会话实际占用的 bulk-OUT 端点地址；后端不暴露端点绑定时为 <c>0</c>。</para>
    /// </param>
    /// <returns>
    /// The terminal selection state with an explanatory reason.
    /// <para>带解释原因的终态选择结果。</para>
    /// </returns>
    public static MtkCommandInterfaceResult Select(UsbDeviceInfo device, MtkOpenOptions? options = null,
        byte boundEndpointIn = 0, byte boundEndpointOut = 0)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!device.InterfaceMetadataObserved || device.Interfaces.Count == 0)
        {
            return new MtkCommandInterfaceResult(MtkCommandInterfaceStatus.NoMetadata, null,
                "The backend reported no per-interface metadata; the caller-provided interface binding is preserved.");
        }

        var requiredClass = options?.CommandInterfaceClass ?? 0xFF;
        var requiredSubClass = options?.CommandInterfaceSubClass;
        var candidates = new List<UsbInterfaceInfo>();
        var loggingCandidates = 0;
        foreach (var interfaceInfo in device.Interfaces)
        {
            if (interfaceInfo.Class != requiredClass
                || (requiredSubClass is { } sub && interfaceInfo.SubClass != sub))
            {
                continue;
            }

            if (interfaceInfo.Class == 0xFF && interfaceInfo.SubClass == 0x42 && interfaceInfo.Protocol == 0x01)
            {
                continue;
            }

            var hasIn = interfaceInfo.Endpoints.Any(static endpoint => MtkModeClassifier.IsBulk(endpoint) && endpoint.IsIn);
            var hasOut = interfaceInfo.Endpoints.Any(static endpoint => MtkModeClassifier.IsBulk(endpoint) && !endpoint.IsIn);
            if (hasIn && hasOut)
            {
                candidates.Add(interfaceInfo);
            }
            else if (hasIn)
            {
                loggingCandidates++;
            }
        }

        if (boundEndpointIn != 0 && boundEndpointOut != 0)
        {
            var matching = candidates
                .Where(candidate => candidate.Endpoints.Any(endpoint => endpoint.IsIn &&
                                endpoint.EndpointAddress == boundEndpointIn)
                            && candidate.Endpoints.Any(endpoint => !endpoint.IsIn &&
                                endpoint.EndpointAddress == boundEndpointOut))
                .ToList();
            if (candidates.Count > 0 && matching.Count == 0)
            {
                return new MtkCommandInterfaceResult(MtkCommandInterfaceStatus.BoundEndpointMismatch, null,
                    $"The claimed endpoints 0x{boundEndpointIn:X2}/0x{boundEndpointOut:X2} belong to no candidate command interface; the session is bound to the wrong interface.");
            }

            candidates = matching.Count > 0 ? matching : candidates;
        }

        if (candidates.Count == 1)
        {
            return new MtkCommandInterfaceResult(MtkCommandInterfaceStatus.Selected,
                candidates[0].InterfaceNumber,
                $"Interface {candidates[0].InterfaceNumber} (class 0x{candidates[0].Class:X2} subclass 0x{candidates[0].SubClass:X2}) is the only bulk command pair.");
        }

        if (candidates.Count > 1)
        {
            var numbers = string.Join(", ", candidates.Select(static candidate => candidate.InterfaceNumber.ToString()));
            return new MtkCommandInterfaceResult(MtkCommandInterfaceStatus.Ambiguous, null,
                $"Interfaces {numbers} all present a bulk command pair; binding one by enumeration order is forbidden, so an explicit subclass or interface filter is required.");
        }

        return loggingCandidates > 0
            ? new MtkCommandInterfaceResult(MtkCommandInterfaceStatus.LoggingOnly, null,
                $"Only {loggingCandidates} logging-shaped interface(s) (bulk IN without a paired bulk OUT) were observed; commands must not be sent here.")
            : new MtkCommandInterfaceResult(MtkCommandInterfaceStatus.NoCommandInterface, null,
                $"No interface matched the vendor bulk command shape (class 0x{requiredClass:X2} with one bulk IN and one bulk OUT).");
    }
}