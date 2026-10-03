namespace UotanStudio.Comm.Protocols.Mtk.Profile;

/// <summary>
/// The frozen identity of the Legacy DA wire profile that governed one agent startup: which
/// storage family was selected, the DA1 response bytes the agent reported together with any caller
/// assertion, the completion framing, the NAND size layout and the agent packet length.
/// <para>一次代理启动所遵循的 Legacy DA 线配置的冻结身份：选定的存储家族、代理上报的 DA1 响应字节以及调用方
/// 断言、完成帧布局、NAND 容量布局与代理包长。</para>
/// The record is derived from <see cref="MtkLegacyOptions"/> and the observed handshake without
/// reinterpreting anything: it states which scripted transcript cell a run corresponds to, so status-code,
/// completion and shutdown behavior can be pinned to an explicit profile instead of inferred from device bytes.
/// <para>本记录由 <see cref="MtkLegacyOptions"/> 与实测握手派生且不重新解释任何内容：它声明本次运行对应哪一格脚本化
/// transcript，使状态码、完成信息与关闭行为可以钉在显式配置上，而不是从设备字节推测。</para>
/// </summary>
public sealed record MtkLegacyDaProfile
{
    /// <summary>
    /// Gets the storage family the agent was initialized for.
    /// <para>获取代理初始化所面向的存储家族。</para>
    /// </summary>
    public required MtkXFlashStorageKind StorageKind { get; init; }

    /// <summary>
    /// Gets the three-byte DA1 inventory response asserted by the caller, if any.
    /// <para>获取调用方断言的三字节 DA1 清单响应；未断言时为空。</para>
    /// </summary>
    /// <value>
    /// Six hexadecimal characters, or <see langword="null"/> when the profile accepted whatever the agent
    /// reported. <para>六个十六进制字符；配置接受代理实际上报内容时为 <see langword="null"/>。</para>
    /// </value>
    public string? AssertedStageOneResponseHex { get; init; }

    /// <summary>
    /// Gets the three-byte DA1 inventory response the agent actually reported.
    /// <para>获取代理实际上报的三字节 DA1 清单响应。</para>
    /// </summary>
    /// <value>Six hexadecimal characters observed during the startup this profile governs.
    /// <para>本配置所辖启动过程中观测到的六个十六进制字符。</para></value>
    public required string ObservedStageOneResponseHex { get; init; }

    /// <summary>
    /// Gets the selected startup completion framing.
    /// <para>获取选定的启动完成帧布局。</para>
    /// </summary>
    public required MtkLegacyCompletionProfile Completion { get; init; }

    /// <summary>
    /// Gets whether the inventory uses the 64-bit NAND size layout.
    /// <para>获取清单是否使用 64 位 NAND 容量布局。</para>
    /// </summary>
    public required bool NandSize64Bit { get; init; }

    /// <summary>
    /// Gets the bounded DA2 upload packet length.
    /// <para>获取有界的 DA2 上传包长。</para>
    /// </summary>
    public required int AgentPacketLength { get; init; }

    /// <summary>
    /// Freezes the profile identity from explicit caller options and the selected storage family.
    /// <para>从显式调用方选项与选定的存储家族冻结配置身份。</para>
    /// </summary>
    /// <param name="options">The validated Legacy options. 已校验的 Legacy 选项。</param>
    /// <param name="storageKind">The explicitly selected family. 显式选择的家族。</param>
    /// <param name="observedStageOneResponseHex">The DA1 response bytes reported by the agent.
    /// <para>代理上报的 DA1 响应字节。</para></param>
    /// <returns>
    /// The immutable profile record. <para>不可变的配置记录。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.
    /// <para><paramref name="options"/> 为 <see langword="null"/>。</para></exception>
    public static MtkLegacyDaProfile From(MtkLegacyOptions options, MtkXFlashStorageKind storageKind,
        string observedStageOneResponseHex)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(observedStageOneResponseHex);
        return new MtkLegacyDaProfile
        {
            StorageKind = storageKind,
            AssertedStageOneResponseHex = options.StageOneResponseHex,
            ObservedStageOneResponseHex = observedStageOneResponseHex,
            Completion = options.CompletionProfile,
            NandSize64Bit = options.NandSize64Bit,
            AgentPacketLength = options.AgentPacketLength
        };
    }

    /// <summary>
    /// Renders a localization-neutral description for evidence and diagnostics.
    /// <para>渲染与本地化无关的描述，用于证据与诊断。</para>
    /// </summary>
    /// <returns>
    /// A stable single-line profile description. <para>稳定的单行配置描述。</para>
    /// </returns>
    public string Describe() =>
        $"legacy-da profile: kind={StorageKind}, da1-response={ObservedStageOneResponseHex}" +
        $"{(AssertedStageOneResponseHex is { } asserted ? $" (asserted {asserted})" : string.Empty)}, " +
        $"completion={Completion}, nand-size64={NandSize64Bit}, agent-packet={AgentPacketLength}";
}