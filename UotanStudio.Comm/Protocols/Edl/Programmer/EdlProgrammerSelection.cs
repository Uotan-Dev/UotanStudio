namespace UotanStudio.Comm.Protocols.Edl.Programmer;

/// <summary>
/// The verdict of matching a programmer catalog against one Sahara device identity.
/// <para>针对单个 Sahara 设备身份在 programmer 目录中执行匹配的结论。</para>
/// </summary>
public enum EdlProgrammerMatchDecision
{
    /// <summary>
    /// Exactly one catalog entry positively matched the declared identity fields.
    /// <para>目录中恰好一个条目按声明的身份子段正匹配。</para>
    /// </summary>
    Matched,

    /// <summary>
    /// Candidates carried sufficient evidence and every one of them explicitly contradicted the identity.
    /// <para>候选项具备充分证据，但每一个都与设备身份显式矛盾。</para>
    /// </summary>
    NoMatch,

    /// <summary>
    /// Several catalog entries positively matched and none can be eliminated by evidence.
    /// <para>多个目录条目正匹配，且无法依据证据排除任何一个。</para>
    /// </summary>
    Ambiguous,

    /// <summary>
    /// The identity is absent or every candidate lacks positive matching evidence; no guessing is allowed.
    /// <para>身份缺失，或每个候选都缺少正匹配证据；不允许任何猜测。</para>
    /// </summary>
    NeedsExplicitSelection
}

/// <summary>
/// How one catalog candidate fared against the device identity.
/// <para>单个目录候选项对照设备身份的评估结果。</para>
/// </summary>
public enum EdlProgrammerCandidateOutcome
{
    /// <summary>
    /// Every declared constraint of the candidate positively matched the identity.
    /// <para>候选项声明的每个约束均与身份正匹配。</para>
    /// </summary>
    Matched,

    /// <summary>
    /// A declared constraint explicitly contradicted the identity.
    /// <para>某个声明的约束与身份显式矛盾。</para>
    /// </summary>
    Rejected,

    /// <summary>
    /// The candidate declares no identity evidence, or the device identity itself is absent.
    /// <para>候选项未声明身份证据，或设备身份本身缺失。</para>
    /// </summary>
    InsufficientEvidence
}

/// <summary>
/// The per-candidate matching explanation retained with a selection.
/// <para>随选择结果一并保留的逐候选匹配说明。</para>
/// </summary>
/// <param name="FileName">
/// The candidate display file name. <para>候选项展示文件名。</para>
/// </param>
/// <param name="Sha256Hex">
/// The candidate declared SHA-256 digest. <para>候选项声明的 SHA-256 摘要。</para>
/// </param>
/// <param name="Outcome">
/// The candidate evaluation outcome. <para>候选项评估结果。</para>
/// </param>
/// <param name="Reason">
/// The matching basis or rejection explanation. <para>匹配依据或拒绝原因。</para>
/// </param>
public sealed record EdlProgrammerCandidateReason(
    string FileName,
    string Sha256Hex,
    EdlProgrammerCandidateOutcome Outcome,
    string Reason);

/// <summary>
/// The outcome of one programmer selection run: the decision, the winning descriptor if any,
/// the actually measured image hash if supplied, and the per-candidate reasons.
/// <para>单次 programmer 选择的结果：结论、胜出描述符（若有）、实测镜像哈希（若已提供），以及逐候选原因。</para>
/// A selection is only upload-eligible when <see cref="Decision"/> is <see cref="EdlProgrammerMatchDecision.Matched"/>
/// and the session-measured hash equals <see cref="EdlProgrammerDescriptor.Sha256Hex"/>.
/// <para>仅当 <see cref="Decision"/> 为 <see cref="EdlProgrammerMatchDecision.Matched"/>，且会话实测哈希与
/// <see cref="EdlProgrammerDescriptor.Sha256Hex"/> 一致时，该选择才允许上传。</para>
/// </summary>
public sealed record EdlProgrammerSelection
{
    /// <summary>
    /// Gets the overall matching decision.
    /// <para>获取整体匹配结论。</para>
    /// </summary>
    public required EdlProgrammerMatchDecision Decision { get; init; }

    /// <summary>
    /// Gets the uniquely matched descriptor, present only for <see cref="EdlProgrammerMatchDecision.Matched"/>.
    /// <para>获取唯一匹配的描述符，仅在 <see cref="EdlProgrammerMatchDecision.Matched"/> 时存在。</para>
    /// </summary>
    public EdlProgrammerDescriptor? Descriptor { get; init; }

    /// <summary>
    /// Gets the SHA-256 hex digest actually measured from the image stream during the selection run.
    /// <para>获取选择过程中对镜像流实测的 SHA-256 十六进制摘要。</para>
    /// </summary>
    /// <value>The measured digest, or <see langword="null"/> when no stream was hashed.
    /// <para>实测摘要；未对流计算哈希时为 <see langword="null"/>。</para></value>
    public string? ActualSha256Hex { get; init; }

    /// <summary>
    /// Gets the Sahara identity the catalog was matched against.
    /// <para>获取用于对照目录匹配的 Sahara 身份。</para>
    /// </summary>
    /// <value>The identity, or <see langword="null"/> when the device did not report one.
    /// <para>设备身份；设备未上报时为 <see langword="null"/>。</para></value>
    public SaharaDeviceIdentity? Identity { get; init; }

    /// <summary>
    /// Gets the matching basis or rejection reason of every evaluated candidate.
    /// <para>获取每个被评估候选项的匹配依据或拒绝原因。</para>
    /// </summary>
    public required IReadOnlyList<EdlProgrammerCandidateReason> PerCandidateReasons { get; init; }

    /// <summary>
    /// Gets whether this selection is eligible to upload a programmer.
    /// <para>获取该选择是否具备上传 programmer 的资格。</para>
    /// </summary>
    /// <value><see langword="true"/> only for a unique match with a descriptor attached.
    /// <para>仅在唯一匹配且附带描述符时为 <see langword="true"/>。</para></value>
    public bool IsUploadEligible => Decision == EdlProgrammerMatchDecision.Matched && Descriptor is not null;
}