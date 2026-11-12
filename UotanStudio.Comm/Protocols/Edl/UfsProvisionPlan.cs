using System.Xml;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>A bounded snapshot of structurally validated commands, not validation against device provisioning capabilities.</summary>
internal sealed class UfsProvisionPlan
{
    private UfsProvisionPlan(string[] commands, int? slot, bool commit)
    {
        Commands = Array.AsReadOnly(commands);
        Slot = slot;
        Commit = commit;
    }

    internal IReadOnlyList<string> Commands { get; }

    internal int? Slot { get; }

    /// <summary>Gets whether the plan persists the configuration on the device; the epilogue of a committing
    /// plan may reset the target before it can acknowledge the last command.
    /// <para>获取该计划是否在设备上持久化配置；提交型计划的结语可能在确认最后一条命令之前复位目标。</para></summary>
    internal bool Commit { get; }

    internal static UfsProvisionPlan Prepare(UfsCommon common, IReadOnlyList<UfsBody> bodies,
        UfsEpilogue epilogue, bool commit, int? slot)
    {
        ArgumentNullException.ThrowIfNull(common);
        ArgumentNullException.ThrowIfNull(bodies);
        ArgumentNullException.ThrowIfNull(epilogue);
        if (slot is < 0 || bodies.Count is 0 or > 256)
        {
            throw new InvalidDataException("UFS provisioning requires a nonnegative slot and one to 256 unique LUN bodies.");
        }
        CheckByte(common.BNumberLU, nameof(common.BNumberLU));
        CheckByte(common.BBootEnable, nameof(common.BBootEnable));
        CheckByte(common.BDescrAccessEn, nameof(common.BDescrAccessEn));
        CheckByte(common.BInitPowerMode, nameof(common.BInitPowerMode));
        CheckByte(common.BHighPriorityLUN, nameof(common.BHighPriorityLUN));
        CheckByte(common.BSecureRemovalType, nameof(common.BSecureRemovalType));
        CheckByte(common.BInitActiveICCLevel, nameof(common.BInitActiveICCLevel));
        CheckByte(common.BConfigDescrLock, nameof(common.BConfigDescrLock));
        CheckWord(common.WPeriodicRTCUpdate, nameof(common.WPeriodicRTCUpdate));
        CheckByte(epilogue.LUNumToGrow, nameof(epilogue.LUNumToGrow));
        var ids = new HashSet<int>();
        List<string> commands = [with(bodies.Count + 2), FirehoseXmlBuilder.BuildUfsCommon(common, slot)];
        foreach (var body in bodies)
        {
            ArgumentNullException.ThrowIfNull(body);
            CheckByte(body.LUNum, nameof(body.LUNum));
            if (!ids.Add(body.LUNum))
            {
                throw new InvalidDataException("UFS provisioning contains a duplicate LUN.");
            }
            CheckByte(body.BLUEnable, nameof(body.BLUEnable));
            CheckByte(body.BBootLUNID, nameof(body.BBootLUNID));
            CheckByte(body.BDataReliability, nameof(body.BDataReliability));
            CheckByte(body.BLUWriteProtect, nameof(body.BLUWriteProtect));
            CheckByte(body.BMemoryType, nameof(body.BMemoryType));
            CheckByte(body.BLogicalBlockSize, nameof(body.BLogicalBlockSize));
            CheckByte(body.BProvisioningType, nameof(body.BProvisioningType));
            CheckWord(body.WContextCapacities, nameof(body.WContextCapacities));
            if (body.SizeInKb < 0 || body.SizeInKb > long.MaxValue / 1024)
            {
                throw new InvalidDataException("The UFS LUN size is negative or exceeds supported byte addressing.");
            }
            if (body.Desc is { } description)
            {
                if (description.Length > 4096)
                {
                    throw new InvalidDataException("The UFS description exceeds the supported limit.");
                }
                XmlConvert.VerifyXmlChars(description);
            }
            commands.Add(FirehoseXmlBuilder.BuildUfsBody(body, slot));
        }
        commands.Add(FirehoseXmlBuilder.BuildUfsEpilogue(epilogue, commit, slot));
        return new UfsProvisionPlan([.. commands], slot, commit);
    }

    private static void CheckByte(int value, string name)
    {
        if (value is < byte.MinValue or > byte.MaxValue)
        {
            throw new InvalidDataException($"UFS field '{name}' does not fit an unsigned byte.");
        }
    }

    private static void CheckWord(int value, string name)
    {
        if (value is < ushort.MinValue or > ushort.MaxValue)
        {
            throw new InvalidDataException($"UFS field '{name}' does not fit an unsigned word.");
        }
    }
}