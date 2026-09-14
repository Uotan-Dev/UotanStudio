using System.Text;

namespace UotanStudio.Services.Delivery;

internal static partial class DeliveryScriptGenerator
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