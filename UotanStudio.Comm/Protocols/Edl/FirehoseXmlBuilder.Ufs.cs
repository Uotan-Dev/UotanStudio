using System.Text;

namespace UotanStudio.Comm.Protocols.Edl;

public static partial class FirehoseXmlBuilder
{
    /// <summary>
    /// Builds the UFS provisioning common &lt;ufs&gt; XML command.
    /// <para>构建 UFS provisioning 公共 &lt;ufs&gt; XML 命令。</para>
    /// </summary>
    /// <param name="ufs">The common configuration. 公共配置。</param>
    /// <param name="slot">Optional slot number. 可选槽位编号。</param>
    /// <returns>The UFS common XML string. UFS 公共 XML 字符串。</returns>
    public static string BuildUfsCommon(UfsCommon ufs, int? slot = null)
    {
        var sb = new StringBuilder();
        sb.Append(EdlConstants.XmlHeader);
        sb.Append(EdlConstants.DataOpen);
        sb.Append("<ufs");
        sb.Append($" bNumberLU=\"{ufs.BNumberLU}\"");
        sb.Append($" bBootEnable=\"{ufs.BBootEnable}\"");
        sb.Append($" bDescrAccessEn=\"{ufs.BDescrAccessEn}\"");
        sb.Append($" bInitPowerMode=\"{ufs.BInitPowerMode}\"");
        sb.Append($" bHighPriorityLUN=\"{ufs.BHighPriorityLUN}\"");
        sb.Append($" bSecureRemovalType=\"{ufs.BSecureRemovalType}\"");
        sb.Append($" bInitActiveICCLevel=\"{ufs.BInitActiveICCLevel}\"");
        sb.Append($" wPeriodicRTCUpdate=\"{ufs.WPeriodicRTCUpdate}\"");
        if (ufs.BConfigDescrLock != 0)
        {
            sb.Append($" bConfigDescrLock=\"{ufs.BConfigDescrLock}\"");
        }

        if (slot.HasValue && slot.Value != 0)
        {
            sb.Append($" slot=\"{slot.Value}\"");
        }

        sb.Append(" />\n");
        sb.Append(EdlConstants.DataClose);
        return sb.ToString();
    }

    /// <summary>
    /// Builds the UFS provisioning per-LUN body &lt;ufs&gt; XML command.
    /// <para>构建 UFS provisioning 的每 LUN 主体 &lt;ufs&gt; XML 命令。</para>
    /// </summary>
    /// <param name="ufs">The LUN body configuration. LUN 主体配置。</param>
    /// <param name="slot">Optional slot number. 可选槽位编号。</param>
    /// <returns>The UFS body XML string. UFS 主体 XML 字符串。</returns>
    public static string BuildUfsBody(UfsBody ufs, int? slot = null)
    {
        var sb = new StringBuilder();
        sb.Append(EdlConstants.XmlHeader);
        sb.Append(EdlConstants.DataOpen);
        sb.Append("<ufs");
        sb.Append($" LUNum=\"{ufs.LUNum}\"");
        sb.Append($" bLUEnable=\"{ufs.BLUEnable}\"");
        sb.Append($" bBootLunID=\"{ufs.BBootLUNID}\"");
        sb.Append($" size_in_kb=\"{ufs.SizeInKb}\"");
        sb.Append($" bDataReliability=\"{ufs.BDataReliability}\"");
        sb.Append($" bLUWriteProtect=\"{ufs.BLUWriteProtect}\"");
        sb.Append($" bMemoryType=\"{ufs.BMemoryType}\"");
        sb.Append($" bLogicalBlockSize=\"{ufs.BLogicalBlockSize}\"");
        sb.Append($" bProvisioningType=\"{ufs.BProvisioningType}\"");
        // Attribute key mirrors qdl firehose.c "wContextCapabilities" (plural -Capabilities).
        // 属性键对齐 qdl firehose.c 的 "wContextCapabilities"（复数 -Capabilities）。
        sb.Append($" wContextCapabilities=\"{ufs.WContextCapacities}\"");
        if (!string.IsNullOrEmpty(ufs.Desc))
        {
            sb.Append($" desc=\"{EscapeXml(ufs.Desc)}\"");
        }

        if (slot.HasValue && slot.Value != 0)
        {
            sb.Append($" slot=\"{slot.Value}\"");
        }

        sb.Append(" />\n");
        sb.Append(EdlConstants.DataClose);
        return sb.ToString();
    }

    /// <summary>
    /// Builds the UFS provisioning epilogue &lt;ufs&gt; XML command (LUNtoGrow + commit).
    /// <para>构建 UFS provisioning 结语 &lt;ufs&gt; XML 命令（LUNtoGrow + commit）。</para>
    /// </summary>
    /// <param name="ufs">The epilogue configuration. 结语配置。</param>
    /// <param name="commit">Whether to commit the configuration. 是否提交配置。</param>
    /// <param name="slot">Optional slot number. 可选槽位编号。</param>
    /// <returns>The UFS epilogue XML string. UFS 结语 XML 字符串。</returns>
    public static string BuildUfsEpilogue(UfsEpilogue ufs, bool commit, int? slot = null)
    {
        var sb = new StringBuilder();
        sb.Append(EdlConstants.XmlHeader);
        sb.Append(EdlConstants.DataOpen);
        sb.Append("<ufs");
        // Attribute key mirrors qdl firehose.c "LUNtoGrow" (capitalization: LUNtoGrow).
        // 属性键对齐 qdl firehose.c 的 "LUNtoGrow"（注意大小写：LUNtoGrow）。
        sb.Append($" LUNtoGrow=\"{ufs.LUNumToGrow}\"");
        sb.Append($" commit=\"{(commit ? 1 : 0)}\"");
        if (slot.HasValue && slot.Value != 0)
        {
            sb.Append($" slot=\"{slot.Value}\"");
        }

        sb.Append(" />\n");
        sb.Append(EdlConstants.DataClose);
        return sb.ToString();
    }

    /// <summary>
    /// Builds a UFS reset pseudo-command: a &lt;ufs&gt; epilogue with commit="0" that discards
    /// any pending UFS provisioning configuration on the target without persisting.
    /// <para>构建 UFS 复位伪命令：通过 commit="0" 的 &lt;ufs&gt; 结语丢弃目标端待生效的 UFS
    /// provisioning 配置而不持久化。</para>
    /// </summary>
    /// <param name="slot">Optional slot number. 可选槽位编号。</param>
    /// <returns>The UFS reset XML string. UFS 复位 XML 字符串。</returns>
    public static string BuildUfsReset(int? slot = null)
    {
        var sb = new StringBuilder();
        sb.Append(EdlConstants.XmlHeader);
        sb.Append(EdlConstants.DataOpen);
        sb.Append("<ufs");
        sb.Append(" LUNtoGrow=\"0\"");
        sb.Append(" commit=\"0\"");
        if (slot.HasValue && slot.Value != 0)
        {
            sb.Append($" slot=\"{slot.Value}\"");
        }

        sb.Append(" />\n");
        sb.Append(EdlConstants.DataClose);
        return sb.ToString();
    }

}