namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>
/// Selects which device interface carries the replay-protected block area.
/// <para>选择承载重放保护块区域的设备接口。</para>
/// This client uses one distinct command code per interface, so the value is never inferred
/// from a partition name or from another interface's reply.
/// <para>本客户端为每个接口使用独立的命令码，因此该值绝不通过分区名或其他接口的应答推断。</para>
/// </summary>
/// <remarks>
/// The eMMC value matches the dedicated RPMB partition identifier and the UFS value matches logical unit 4.
/// <para>eMMC 取值对应专用 RPMB 分区标识符，UFS 取值对应逻辑单元 4。
/// 参见 （<c>MTK_DA_EMMC_PART_RPMB = 3</c>）与
/// （<c>UFS_LU4 = 4</c>）。</para>
/// </remarks>
public enum MtkRpmbTarget
{
    /// <summary>
    /// The eMMC RPMB partition, addressed by the RPMB-specific read and write commands.
    /// <para>eMMC RPMB 分区，通过 RPMB 专用读写命令寻址。</para>
    /// </summary>
    Emmc,
    /// <summary>
    /// The UFS RPMB logical unit, addressed by the separate UFS read and write commands.
    /// <para>UFS RPMB 逻辑单元，通过独立的 UFS 读写命令寻址。</para>
    /// </summary>
    Ufs
}

/// <summary>
/// Wire constants shared by every RPMB command family in this session.
/// <para>本会话中所有 RPMB 命令族共用的线协议常量。</para>
/// </summary>
public static class MtkRpmbWire
{
    /// <summary>
    /// Gets the fixed width of one RPMB access frame.
    /// <para>获取单个 RPMB 访问帧的固定宽度。</para>
    /// </summary>
    /// <value>
    /// Two hundred fifty-six bytes. This client refuses any write buffer whose length differs
    /// from this value and walks a whole RPMB area in steps of this size.
    /// <para>256 字节。本客户端会拒绝长度与此值不同的写入缓冲区，并按此步长遍历整个 RPMB 区域。</para>
    /// </value>
    /// <remarks>
    /// See for the rejected write length and
    /// for the read stride.
    /// <para>参见 （写入长度校验）与
    /// （读取步长）。</para>
    /// </remarks>
    public const int FrameLength = 0x100;

    /// <summary>
    /// Gets the byte width the Legacy DA uses when it addresses a physical eMMC partition.
    /// <para>Legacy DA 寻址 eMMC 物理分区时使用的字节宽度。</para>
    /// </summary>
    /// <value>
    /// Five hundred twelve bytes; the Legacy read and write headers carry byte offsets and byte counts,
    /// not frame indices, and expose no per-frame authentication status.
    /// <para>512 字节；Legacy 读写头携带的是字节偏移与字节数而非帧序号，也不暴露逐帧认证状态。</para>
    /// </value>
    /// <remarks>
    /// See, where the partition-selected read
    /// command sends big-endian byte address and length fields.
    /// <para>参见，其按分区选择的读命令
    /// 发送大端字节地址与长度字段。</para>
    /// </remarks>
    public const int LegacyBlockSize = 0x200;
}

/// <summary>
/// A frame-aligned RPMB range: the first frame index and how many consecutive frames to access.
/// <para>帧对齐的 RPMB 范围：起始帧序号与要访问的连续帧数量。</para>
/// </summary>
/// <param name="StartFrame">The zero-based first RPMB frame index.
/// <para>从零开始的首个 RPMB 帧序号。</para></param>
/// <param name="FrameCount">The number of consecutive frames requested.
/// <para>请求的连续帧数量。</para></param>
public readonly record struct MtkRpmbFrameRange(long StartFrame, int FrameCount)
{
    /// <summary>
    /// Gets the byte offset of <see cref="StartFrame"/> inside the RPMB area.
    /// <para>获取 <see cref="StartFrame"/> 在 RPMB 区域内的字节偏移。</para>
    /// </summary>
    /// <value>
    /// <see cref="StartFrame"/> multiplied by the frame width; the multiplication is checked.
    /// <para>起始帧序号乘以帧宽度；该乘法会做溢出检查。</para>
    /// </value>
    public long ByteOffset => checked(StartFrame * MtkRpmbWire.FrameLength);

    /// <summary>
    /// Gets the total byte length covered by the requested frames.
    /// <para>获取请求帧覆盖的总字节长度。</para>
    /// </summary>
    /// <value>
    /// <see cref="FrameCount"/> multiplied by the frame width; the multiplication is checked.
    /// <para>帧数量乘以帧宽度；该乘法会做溢出检查。</para>
    /// </value>
    public int ByteLength => checked(FrameCount * MtkRpmbWire.FrameLength);

    /// <summary>Rejects an unbounded, misaligned or out-of-report range before any byte reaches the transport.
    /// <para>在任何字节进入传输层之前，拒绝无界、错位或超出上报范围的要求。</para></summary>
    /// <param name="reportedCapacityBytes">The RPMB capacity the device reported, or <see langword="null"/> when the selected interface's geometry carries no RPMB size field.
    /// <para>设备上报的 RPMB 容量；所选接口的几何信息不含 RPMB 大小字段时为 <see langword="null"/>。</para></param>
    /// <param name="maximumFrames">The caller-configured ceiling on frames in one exchange.
    /// <para>调用方配置的单次交换帧数上限。</para></param>
    /// <exception cref="ArgumentOutOfRangeException">The start frame is negative, the count is not positive, the count exceeds <paramref name="maximumFrames"/>, or the range extends past <paramref name="reportedCapacityBytes"/>.
    /// <para>起始帧为负数、数量非正、数量超过 <paramref name="maximumFrames"/>，或范围超出 <paramref name="reportedCapacityBytes"/>。</para></exception>
    /// <exception cref="InvalidOperationException"><paramref name="reportedCapacityBytes"/> is zero, meaning the device reported no RPMB area at all.
    /// <para><paramref name="reportedCapacityBytes"/> 为零，表示设备完全未上报 RPMB 区域。</para></exception>
    public void Validate(long? reportedCapacityBytes, int maximumFrames)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(StartFrame);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(FrameCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(FrameCount, maximumFrames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFrames);
        if (reportedCapacityBytes == 0)
        {
            throw new InvalidOperationException("The device reported no RPMB capacity for the selected interface.");
        }
        if (reportedCapacityBytes is null)
        {
            return;
        }
        var capacity = reportedCapacityBytes.Value;
        if (ByteOffset > capacity || ByteLength > capacity - ByteOffset)
        {
            throw new ArgumentOutOfRangeException(nameof(StartFrame),
                "The requested RPMB frame range is outside the reported RPMB capacity.");
        }
    }
}

/// <summary>
/// Caller-supplied bounds and the single write opt-in for RPMB access.
/// <para>调用方提供的 RPMB 访问边界与唯一的写入开关。</para>
/// Reads are always bounded; writes exist only when <see cref="AllowAuthenticatedWrites"/> is set
/// explicitly, because the default record grants read access alone.
/// <para>读取始终有界；只有显式设置 <see cref="AllowAuthenticatedWrites"/> 时写入才可用，
/// 因为默认记录仅授予读取权限。</para>
/// </summary>
public sealed record MtkRpmbOptions
{
    /// <summary>
    /// Gets whether RPMB frame writes are permitted at all.
    /// <para>获取是否允许 RPMB 帧写入。</para>
    /// </summary>
    /// <value>
    /// <see langword="false"/> by default. This client exposes writes and a zero-fill erase, so
    /// the capability is real; this session refuses every write while the flag is <see langword="false"/>
    /// and never performs an erase on the caller's behalf.
    /// <para>默认为 <see langword="false"/>。本客户端确实提供写入与清零擦除，因此该能力存在；
    /// 标志为 <see langword="false"/> 时本会话拒绝所有写入，且绝不会代表调用方执行擦除。</para>
    /// </value>
    public bool AllowAuthenticatedWrites { get; init; }

    /// <summary>
    /// Gets the largest frame count accepted by one read exchange.
    /// <para>获取单次读取交换接受的最大帧数。</para>
    /// </summary>
    /// <value>
    /// One thousand forty-eight frames, one quarter of a mebibyte, by default; must be between one and
    /// four thousand ninety-six.
    /// <para>默认 1048 帧（四分之一 MiB）；必须介于 1 与 4096 之间。</para>
    /// </value>
    public int MaximumReadFrames { get; init; } = 1048;

    /// <summary>
    /// Gets the largest frame count accepted by one write exchange.
    /// <para>获取单次写入交换接受的最大帧数。</para>
    /// </summary>
    /// <value>
    /// Sixty-four frames, sixteen kibibytes, by default; must be between one and one thousand forty-eight.
    /// <para>默认 64 帧（16 KiB）；必须介于 1 与 1048 之间。</para>
    /// </value>
    public int MaximumWriteFrames { get; init; } = 64;

    /// <summary>
    /// Gets the cooperative whole-exchange deadline for one RPMB command family.
    /// <para>获取单个 RPMB 命令族完整交换的协作式截止时间。</para>
    /// </summary>
    /// <value>
    /// Two minutes by default; must be between one millisecond and twenty-four hours.
    /// <para>默认两分钟；必须介于 1 毫秒与 24 小时之间。</para>
    /// </value>
    public int OperationTimeoutMs { get; init; } = 120000;

    /// <summary>
    /// Validates every bound before the session acquires the operation lease.
    /// <para>在会话获取操作租约之前校验所有边界。</para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A frame ceiling or the deadline is outside its supported range.
    /// <para>帧上限或截止时间超出支持范围。</para>
    /// </exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumReadFrames);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumReadFrames, 4096);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumWriteFrames);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumWriteFrames, 1048);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(OperationTimeoutMs);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(OperationTimeoutMs, 86400000);
    }
}