using System.Text;

namespace UotanStudio.Delivery;

public static partial class DeliveryScriptGenerator
{
    /// <summary>Shares the existing device and author fields across executable script formats.</summary>
    internal static IEnumerable<string> BannerLines(DeliveryPlan plan, Func<string, string>? localize)
    {
        yield return "========================================";
        yield return "UotanStudio";
        if (plan.DeviceCodename.Length > 0)
            yield return T(localize, "Device", "Device: {0}", plan.DeviceCodename);
        if (plan.Information.Length > 0)
        {
            yield return T(localize, "AuthorInformation", "Author information:");
            foreach (var line in plan.Information.ReplaceLineEndings("\n").Split('\n')) yield return line;
        }
        yield return "========================================";
    }

    /// <summary>
    /// Emits the installer prerequisites: an unlocked bootloader, plus matching patched vbmeta for images AVB verifies;
    /// a package carrying its own update-binary additionally requires a custom recovery, because stock recovery rejects
    /// packages it cannot verify. These lines state prerequisites only and never promise a bootable result.
    /// <para>输出安装前置条件行：解锁 bootloader 与 AVB 校验镜像需要配套 patched vbmeta；自带 update-binary 的
    /// 卡刷包额外要求自定义 recovery，因为 stock recovery 会拒绝它无法验证的包。这些行只说明前提，不承诺可开机。</para>
    /// </summary>
    /// <param name="localize">The localization lookup, or null. <para>本地化查询，可为 null。</para></param>
    /// <param name="recoveryInstaller">Whether the output is the package's own update-binary. <para>输出是否为卡刷包自带的 update-binary。</para></param>
    /// <returns>The prerequisite lines to print. <para>要打印的前置条件行。</para></returns>
    internal static IEnumerable<string> PrerequisiteLines(Func<string, string>? localize, bool recoveryInstaller)
    {
        yield return T(localize, "UnlockedRequired",
            "An unlocked bootloader is required. Images verified by AVB also need matching patched vbmeta, otherwise the device may refuse to boot.");
        if (recoveryInstaller)
            yield return T(localize, "CustomRecoveryRequired",
                "This package runs its own update-binary and needs a custom recovery; stock recovery rejects packages it cannot verify.");
    }

    /// <summary>Prints literal batch text with delayed expansion disabled, including embedded quotes.</summary>
    internal static string BatchEcho(string text)
    {
        var result = new StringBuilder("echo(");
        foreach (var c in text)
        {
            if (c == '%') result.Append('%');
            else if (c is '^' or '"' or '&' or '|' or '<' or '>' or '(' or ')') result.Append('^');
            result.Append(c);
        }
        return result.ToString();
    }

    internal static string QuoteShell(string text) => Sh(text);
    internal static string QuoteEdify(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"")
        .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t") + "\"";
}