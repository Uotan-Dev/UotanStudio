namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// An immutable, preflighted UFS provisioning descriptor set: one common configuration header, one
/// body per logical unit, and one epilogue carrying the grow target plus the commit decision.
/// <para>不可变且经过预检的 UFS provisioning 描述符集合：一条公共配置头、每个逻辑单元一条主体，
/// 以及一条携带扩容目标与提交决定的结语。</para>
/// </summary>
/// <remarks>
/// <para>
/// Every bound is enforced before any Firehose command reaches a device, so a descriptor set that
/// could never be sent is never constructed. The count, size and description bounds sit inside what
/// the protocol layer accepts, and the logical-unit window follows the reference provisioning table,
/// which indexes logical units 0 through 7. Descriptors are copied on construction, so mutating the
/// caller's objects afterwards cannot change what will be sent.
/// </para>
/// <para>
/// 所有上限都在任何 Firehose 命令到达设备之前生效，因此永远发不出去的描述符集合不会被构造出来。数量、
/// 大小与描述上限都落在协议层可接受范围之内，逻辑单元窗口参考原始 provisioning 配置表对 0 至 7 号
/// 逻辑单元的索引方式。构造时会复制描述符，因此调用方对象之后的修改无法改变将要发送的内容。
/// </para>
/// </remarks>
public sealed class UfsProvisionProfile
{
    /// <summary>
    /// The largest number of per-LUN bodies one profile may describe. The reference provisioning
    /// table indexes logical units 0 through 7 and the protocol ceiling is 256, so this bound keeps a
    /// hand-written or downloaded profile well below both.
    /// <para>单个配置文件可描述的最大每 LUN 主体数量。参考配置表索引逻辑单元 0 至 7，协议上限为
    /// 256，因此该上限让手工编写或下载的配置文件远低于两者。</para>
    /// </summary>
    /// <value>Sixty-four logical units. <para>六十四个逻辑单元。</para></value>
    public const int MaximumLunCount = 64;

    /// <summary>
    /// The largest size, in kilobytes, any single LUN may declare: one tebibyte.
    /// <para>单个 LUN 可声明的最大大小（千字节）：一 tebibyte。</para>
    /// </summary>
    /// <value>One tebibyte expressed in kilobytes. <para>以千字节表示的一 tebibyte。</para></value>
    public const long MaximumLunSizeInKb = 1_073_741_824L;

    /// <summary>
    /// The longest per-LUN description a profile accepts, matching the protocol ceiling.
    /// <para>配置文件可接受的每 LUN 描述最大长度，与协议上限一致。</para>
    /// </summary>
    /// <value>Four thousand ninety-six characters. <para>四千零九十六个字符。</para></value>
    public const int MaximumDescriptionLength = 4096;

    /// <summary>
    /// The largest profile document, in bytes, that a loader reads from an untrusted source.
    /// <para>加载器会从不可信来源读取的最大配置文件字节数。</para>
    /// </summary>
    /// <value>Five hundred twelve kibibytes. <para>五百十二 kibibyte。</para></value>
    public const int MaximumProfileBytes = 512 * 1024;

    /// <summary>
    /// The smallest block width, in bytes, a target may report and still be treated as UFS media.
    /// The 512-byte width of eMMC and similar media is below this bound and is refused.
    /// <para>目标上报后仍可被视为 UFS 介质的最小块宽度（字节）。eMMC 等介质的 512 字节宽度低于此上限，会被拒绝。</para>
    /// </summary>
    /// <value>One thousand twenty-four bytes. <para>一千零二十四字节。</para></value>
    public const int MinimumUfsBlockBytes = 1024;

    /// <summary>
    /// The largest number of attributes one descriptor element may carry before a profile file is refused.
    /// <para>配置文件在被拒绝前，单个描述符元素可携带的最大属性数。</para>
    /// </summary>
    /// <value>Thirty-two attributes. <para>三十二个属性。</para></value>
    public const int MaximumDescriptorAttributes = 32;

    /// <summary>
    /// Initializes a validated profile, rejecting anything the protocol itself would refuse when it
    /// frames the commands and storing private copies of the descriptors.
    /// <para>初始化经过校验的配置文件，凡协议成帧时会拒绝的内容一律拒绝，并保存描述符的私有副本。</para>
    /// </summary>
    /// <param name="common">The device-level configuration descriptor attributes.<para>设备级配置描述符属性。</para></param>
    /// <param name="bodies">The per-LUN body descriptors, one command each; at least one is required.<para>每 LUN 主体描述符，每个生成一条命令；至少需要一条。</para></param>
    /// <param name="epilogue">The epilogue descriptor carrying the LUN to grow.<para>携带待扩容 LUN 的结语描述符。</para></param>
    /// <param name="commit">Whether the target persists the descriptors when the sequence completes; <see langword="false"/> leaves them pending until a later commit.<para>目标是否在序列完成时持久化描述符；<see langword="false"/> 表示待后续提交后才生效。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="common"/>, <paramref name="bodies"/> or <paramref name="epilogue"/> is <see langword="null"/>, or one element of <paramref name="bodies"/> is <see langword="null"/>.<para><paramref name="common"/>、<paramref name="bodies"/> 或 <paramref name="epilogue"/> 为 <see langword="null"/>，或 <paramref name="bodies"/> 中某个元素为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">A descriptor exceeds a bound on this type or a range the protocol enforces when it frames the commands.<para>某个描述符超出本类型的上限，或超出协议成帧时强制的范围。</para></exception>
    public UfsProvisionProfile(UfsCommon common, IReadOnlyList<UfsBody> bodies, UfsEpilogue epilogue,
        bool commit = false)
    {
        ArgumentNullException.ThrowIfNull(common);
        ArgumentNullException.ThrowIfNull(bodies);
        ArgumentNullException.ThrowIfNull(epilogue);
        var snapshot = ValidateBodies(common, bodies);
        var grownLun = epilogue.LUNumToGrow;
        if (grownLun is < 0 or > byte.MaxValue)
        {
            throw new InvalidDataException(
                $"The epilogue names LUN {grownLun} to grow, outside the 0-{byte.MaxValue} range.");
        }

        UfsProvisionPlan.Prepare(common, snapshot, epilogue, commit, 0);
        Common = Clone(common);
        Bodies = snapshot;
        Epilogue = new UfsEpilogue { LUNumToGrow = grownLun };
        Commit = commit;
        TotalSizeInKb = snapshot.Aggregate(0L, static (total, body) => checked(total + body.SizeInKb));
    }

    /// <summary>
    /// Gets the device-level configuration descriptor attributes sent in the common command.
    /// <para>获取公共命令中发送的设备级配置描述符属性。</para>
    /// </summary>
    /// <value>A private copy of the common descriptor. <para>公共描述符的私有副本。</para></value>
    public UfsCommon Common { get; }

    /// <summary>
    /// Gets the per-LUN body descriptors, one command each, in the order they are sent.
    /// <para>获取每 LUN 主体描述符，每个生成一条命令，顺序与发送顺序一致。</para>
    /// </summary>
    /// <value>An immutable snapshot of the validated bodies. <para>经过校验的主体的不可变快照。</para></value>
    public IReadOnlyList<UfsBody> Bodies { get; }

    /// <summary>
    /// Gets the epilogue descriptor carrying the LUN to grow.
    /// <para>获取携带待扩容 LUN 的结语描述符。</para>
    /// </summary>
    /// <value>A private copy of the epilogue descriptor. <para>结语描述符的私有副本。</para></value>
    public UfsEpilogue Epilogue { get; }

    /// <summary>
    /// Gets whether the target persists the descriptors when the sequence completes.
    /// <para>获取目标是否在序列完成时持久化描述符。</para>
    /// </summary>
    /// <value><see langword="true"/> frames the epilogue with a commit value of one;
    /// <see langword="false"/> leaves the configuration pending.
    /// <para><see langword="true"/> 使结语以提交值 1 成帧；<see langword="false"/> 让配置保持待生效。</para></value>
    public bool Commit { get; }

    /// <summary>
    /// Gets the summed declared LUN capacity in kilobytes.
    /// <para>获取已声明 LUN 容量的千字节总和。</para>
    /// </summary>
    /// <value>The sum of every body size. <para>所有主体大小之和。</para></value>
    public long TotalSizeInKb { get; }

    /// <summary>
    /// Checks the declared capacities against the geometry the target itself reported for LUN 0, so a
    /// caller cannot substitute a remembered or hand-typed capacity for what the device says.
    /// <para>依据目标自身上报的 LUN 0 几何信息核对已声明容量，使调用方无法用记忆或手输的容量替代设备实际值。</para>
    /// </summary>
    /// <param name="lunZeroGeometry">The geometry read from the device for physical partition 0.
    /// <para>从设备读取的 0 号物理分区几何信息。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="lunZeroGeometry"/> is <see langword="null"/>.
    /// <para><paramref name="lunZeroGeometry"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="InvalidDataException">The reported block width is not a UFS width, or a
    /// declared LUN size is not a whole number of the reported blocks.
    /// <para>上报的块宽度不是 UFS 宽度，或已声明的 LUN 大小不是上报块数的整数倍。</para></exception>
    public void ValidateAgainstGeometry(FirehoseStorageGeometry lunZeroGeometry)
    {
        ArgumentNullException.ThrowIfNull(lunZeroGeometry);
        var blockBytes = lunZeroGeometry.SectorSize;
        if (blockBytes < MinimumUfsBlockBytes || (blockBytes & (blockBytes - 1)) != 0)
        {
            throw new InvalidDataException(
                $"LUN 0 reports {blockBytes}-byte blocks, which is not a UFS block width; a power-of-two width of at least {MinimumUfsBlockBytes} bytes is required.");
        }

        foreach (var body in Bodies)
        {
            var declaredBytes = checked(body.SizeInKb * 1024L);
            if (declaredBytes % blockBytes != 0)
            {
                throw new InvalidDataException(
                    $"LUN {body.LUNum} declares {body.SizeInKb} KB, which is not a whole number of the {blockBytes}-byte blocks the target reported.");
            }
        }
    }

    /// <summary>
    /// Builds the exact packets the profile would send, using the protocol's own framing and range
    /// checks, so a caller can report the wire content without touching a device.
    /// <para>使用协议自身的成帧与范围检查构建配置文件将要发送的精确数据包，使调用方无需接触设备即可报告线上内容。</para>
    /// </summary>
    /// <param name="slot">The Firehose slot the sequence would be framed for. <para>该序列将成帧所使用的 Firehose 槽位。</para></param>
    /// <returns>The framed packets in send order: the common command, one body per LUN, then the epilogue.
    /// <para>按发送顺序排列的成帧数据包：公共命令、每 LUN 一条主体命令，最后是结语。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is negative.
    /// <para><paramref name="slot"/> 为负数。</para></exception>
    /// <exception cref="InvalidDataException">The profile violates a protocol range.
    /// <para>配置文件违反协议范围。</para></exception>
    public IReadOnlyList<string> FrameCommands(int slot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        return UfsProvisionPlan.Prepare(Common, Bodies, Epilogue, Commit, slot).Commands;
    }

    private static UfsCommon Clone(UfsCommon source) => new()
    {
        BNumberLU = source.BNumberLU,
        BBootEnable = source.BBootEnable,
        BDescrAccessEn = source.BDescrAccessEn,
        BInitPowerMode = source.BInitPowerMode,
        BHighPriorityLUN = source.BHighPriorityLUN,
        BSecureRemovalType = source.BSecureRemovalType,
        BInitActiveICCLevel = source.BInitActiveICCLevel,
        WPeriodicRTCUpdate = source.WPeriodicRTCUpdate,
        BConfigDescrLock = source.BConfigDescrLock
    };

    private static IReadOnlyList<UfsBody> ValidateBodies(UfsCommon common, IReadOnlyList<UfsBody> bodies)
    {
        if (bodies.Count is 0 or > MaximumLunCount)
        {
            throw new InvalidDataException(
                $"A UFS provisioning profile needs one to {MaximumLunCount} LUN bodies but declares {bodies.Count}.");
        }

        var declaredLogicalUnits = common.BNumberLU;
        var snapshot = new List<UfsBody>(bodies.Count);
        var seen = new HashSet<int>();
        foreach (var body in bodies)
        {
            ArgumentNullException.ThrowIfNull(body);
            if (!seen.Add(body.LUNum))
            {
                throw new InvalidDataException($"LUN {body.LUNum} is described more than once.");
            }





            if (body.LUNum is < 0 or >= MaximumLunCount)
            {
                throw new InvalidDataException(
                    $"LUN {body.LUNum} is outside the 0-{MaximumLunCount - 1} window a profile accepts.");
            }

            if (declaredLogicalUnits > 0 && body.LUNum >= declaredLogicalUnits)
            {
                throw new InvalidDataException(
                    $"LUN {body.LUNum} is outside the {declaredLogicalUnits} logical units the common descriptor declares.");
            }

            if (body.SizeInKb < 0 || body.SizeInKb > MaximumLunSizeInKb)
            {
                throw new InvalidDataException(
                    $"LUN {body.LUNum} declares {body.SizeInKb} KB, outside the 0-{MaximumLunSizeInKb} KB range.");
            }

            if (body.Desc is { Length: > MaximumDescriptionLength } description)
            {
                throw new InvalidDataException(
                    $"LUN {body.LUNum} carries a {description.Length}-character description, above the {MaximumDescriptionLength}-character limit.");
            }

            snapshot.Add(new UfsBody
            {
                LUNum = body.LUNum,
                BLUEnable = body.BLUEnable,
                BBootLUNID = body.BBootLUNID,
                SizeInKb = body.SizeInKb,
                BDataReliability = body.BDataReliability,
                BLUWriteProtect = body.BLUWriteProtect,
                BMemoryType = body.BMemoryType,
                BLogicalBlockSize = body.BLogicalBlockSize,
                BProvisioningType = body.BProvisioningType,
                WContextCapacities = body.WContextCapacities,
                Desc = body.Desc
            });
        }

        return snapshot.AsReadOnly();
    }
}