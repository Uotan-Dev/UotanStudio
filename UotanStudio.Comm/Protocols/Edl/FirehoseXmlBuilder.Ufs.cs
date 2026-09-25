using System.Text;

namespace UotanStudio.Comm.Protocols.Edl;

public static partial class FirehoseXmlBuilder
{
    /// <summary>
    /// Builds the UFS provisioning common &lt;ufs&gt; XML command.
    /// <para>构建 UFS provisioning 公共 &lt;ufs&gt; XML 命令。</para>
    /// </summary>
    /// <param name="ufs">The common configuration supplying the device-level descriptor attributes written into the command.<para>提供写入命令的设备级描述符属性的公共配置。</para></param>
    /// <param name="slot">Optional slot number; the attribute is omitted when <see langword="null"/> or 0.<para>可选槽位编号；为 <see langword="null"/> 或 0 时省略该属性。</para></param>
    /// <returns>A complete Firehose packet carrying a self-closing &lt;ufs&gt; element with the common attributes.<para>包含带公共属性、自闭合 &lt;ufs&gt; 元素的完整 Firehose 数据包。</para></returns>
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
    /// <param name="ufs">The LUN body configuration supplying the logical unit attributes written into the command.<para>提供写入命令的逻辑单元属性的 LUN 主体配置。</para></param>
    /// <param name="slot">Optional slot number; the attribute is omitted when <see langword="null"/> or 0.<para>可选槽位编号；为 <see langword="null"/> 或 0 时省略该属性。</para></param>
    /// <returns>A complete Firehose packet carrying a self-closing &lt;ufs&gt; element with the per-LUN attributes; the descriptor value is XML-escaped.<para>包含带每 LUN 属性、自闭合 &lt;ufs&gt; 元素的完整 Firehose 数据包；描述符值经过 XML 转义。</para></returns>
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
    /// <param name="ufs">The epilogue configuration supplying the logical unit number to grow.<para>提供待扩展逻辑单元编号的结语配置。</para></param>
    /// <param name="commit">Whether the target persists the provisioning on completion; rendered as <c>1</c> or <c>0</c>.<para>目标是否在完成后持久化配置；渲染为 <c>1</c> 或 <c>0</c>。</para></param>
    /// <param name="slot">Optional slot number; the attribute is omitted when <see langword="null"/> or 0.<para>可选槽位编号；为 <see langword="null"/> 或 0 时省略该属性。</para></param>
    /// <returns>A complete Firehose packet carrying the epilogue &lt;ufs&gt; element.<para>包含结语 &lt;ufs&gt; 元素的完整 Firehose 数据包。</para></returns>
    public static string BuildUfsEpilogue(UfsEpilogue ufs, bool commit, int? slot = null)
    {
        var sb = new StringBuilder();
        sb.Append(EdlConstants.XmlHeader);
        sb.Append(EdlConstants.DataOpen);
        sb.Append("<ufs");
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
    /// <param name="slot">Optional slot number; the attribute is omitted when <see langword="null"/> or 0.<para>可选槽位编号；为 <see langword="null"/> 或 0 时省略该属性。</para></param>
    /// <returns>A complete Firehose packet with <c>LUNtoGrow="0"</c> and <c>commit="0"</c>.<para>包含 <c>LUNtoGrow="0"</c> 与 <c>commit="0"</c> 的完整 Firehose 数据包。</para></returns>
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