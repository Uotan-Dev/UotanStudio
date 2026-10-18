namespace UotanStudio.Comm.Protocols.Edl.Programmer;

/// <summary>
/// The built-in rule selector: a candidate matches only when every identity constraint it explicitly
/// declares positively matches the Sahara report, and it declares at least one such constraint.
/// <para>内置规则选择器：仅当候选项显式声明的每个身份约束都与 Sahara 报告正匹配，
/// 且至少声明了一个此类约束时，才判定为匹配。</para>
/// Undeclared constraints never count as evidence, file names are never consulted, and a missing
/// device identity or missing candidate evidence yields <see cref="EdlProgrammerMatchDecision.NeedsExplicitSelection"/>
/// instead of a guess. Two positive matches are reported as <see cref="EdlProgrammerMatchDecision.Ambiguous"/>.
/// <para>未声明的约束从不计为证据，从不查阅文件名；设备身份缺失或候选证据不足时返回
/// <see cref="EdlProgrammerMatchDecision.NeedsExplicitSelection"/> 而非猜测；两个正匹配上报
/// <see cref="EdlProgrammerMatchDecision.Ambiguous"/>。</para>
/// </summary>
public sealed class EdlRuleProgrammerSelector : IEdlProgrammerSelector
{
    /// <summary>
    /// Gets the shared stateless instance.
    /// <para>获取无状态的共享实例。</para>
    /// </summary>
    public static EdlRuleProgrammerSelector Instance { get; } = new();

    /// <summary>Evaluates every catalog descriptor against the identity and classifies the run.
    /// <para>对照身份评估目录中的每个描述符，并对本次评估定性。</para></summary>
    /// <param name="identity">The Sahara-reported identity, or <see langword="null"/>.
    /// <para>Sahara 上报的身份，或 <see langword="null"/>。</para></param>
    /// <param name="catalog">The validated catalog.<para>已校验的目录。</para></param>
    /// <param name="cancellationToken">Cancellation checked before evaluation.
    /// <para>评估前检查的取消令牌。</para></param>
    /// <returns>The selection with per-candidate reasons; <see cref="EdlProgrammerSelection.Descriptor"/> is set only for <see cref="EdlProgrammerMatchDecision.Matched"/>.
    /// <para>含逐候选理由的选择结果；仅在 <see cref="EdlProgrammerMatchDecision.Matched"/> 时设置 <see cref="EdlProgrammerSelection.Descriptor"/>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="catalog"/> is <see langword="null"/>.
    /// <para><paramref name="catalog"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="OperationCanceledException">The token was already cancelled.
    /// <para>令牌已被取消。</para></exception>
    public EdlProgrammerSelection Select(SaharaDeviceIdentity? identity, EdlProgrammerCatalog catalog,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        cancellationToken.ThrowIfCancellationRequested();
        var reasons = new List<EdlProgrammerCandidateReason>(catalog.Descriptors.Count);
        EdlProgrammerDescriptor? winner = null;
        var matchedCount = 0;
        var rejectedCount = 0;
        foreach (var descriptor in catalog.Descriptors)
        {
            var (outcome, reason) = Evaluate(descriptor, identity);
            reasons.Add(new EdlProgrammerCandidateReason(descriptor.FileName, descriptor.Sha256Hex, outcome, reason));
            if (outcome == EdlProgrammerCandidateOutcome.Matched)
            {
                matchedCount++;
                winner = descriptor;
            }
            else if (outcome == EdlProgrammerCandidateOutcome.Rejected)
            {
                rejectedCount++;
            }
        }

        var decision = matchedCount switch
        {
            0 when identity is null => EdlProgrammerMatchDecision.NeedsExplicitSelection,
            0 => rejectedCount > 0 ? EdlProgrammerMatchDecision.NoMatch : EdlProgrammerMatchDecision.NeedsExplicitSelection,
            1 => EdlProgrammerMatchDecision.Matched,
            _ => EdlProgrammerMatchDecision.Ambiguous
        };
        if (decision != EdlProgrammerMatchDecision.Matched)
        {
            winner = null;
        }

        return new EdlProgrammerSelection
        {
            Decision = decision,
            Descriptor = winner,
            Identity = identity,
            PerCandidateReasons = reasons
        };
    }

    private static (EdlProgrammerCandidateOutcome Outcome, string Reason) Evaluate(
        EdlProgrammerDescriptor descriptor, SaharaDeviceIdentity? identity)
    {
        if (identity is null)
        {
            return (EdlProgrammerCandidateOutcome.InsufficientEvidence,
                "The device did not report a Sahara identity; positive matching is impossible.");
        }

        if (!descriptor.HasIdentityEvidence)
        {
            return (EdlProgrammerCandidateOutcome.InsufficientEvidence,
                "The descriptor declares no HWID or OEM public-key-hash constraint, so it cannot positively match.");
        }

        if (descriptor.HardwareId is { } hardwareId)
        {
            var mask = descriptor.HardwareIdMask ?? ulong.MaxValue;
            if ((identity.HardwareId & mask) != (hardwareId & mask))
            {
                return (EdlProgrammerCandidateOutcome.Rejected,
                    $"HWID 0x{identity.HardwareId:X16} does not satisfy the declared 0x{hardwareId:X16} mask 0x{mask:X16}.");
            }
        }

        if (descriptor.OemPublicKeyHashes is { } hashes)
        {
            var reported = identity.OemPublicKeyHashHex.ToLowerInvariant();
            if (!hashes.Any(declared => string.Equals(declared, reported, StringComparison.Ordinal)))
            {
                return (EdlProgrammerCandidateOutcome.Rejected,
                    $"OEM public-key hash {reported} is not among the declared hashes.");
            }
        }

        var basis = descriptor.HardwareId is not null && descriptor.OemPublicKeyHashes is not null
            ? "declared HWID mask and OEM public-key hash both matched"
            : descriptor.HardwareId is not null
                ? "declared HWID mask matched"
                : "declared OEM public-key hash matched";
        return (EdlProgrammerCandidateOutcome.Matched, $"Positive match: {basis}.");
    }
}