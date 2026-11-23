using System.Text.Json;
using System.Text.RegularExpressions;

namespace UotanStudio.Scripting.Validation;

/// <summary>
/// Central validation for all values crossing the script/host boundary.
/// <para>跨越脚本与宿主边界的全部值的统一校验。</para>
/// </summary>
public static partial class ScriptInputValidator
{
    private const int MaximumPathLength = 512;
    private const int MaximumTextLength = 2_048;

    /// <summary>
    /// Validates a partition name.
    /// <para>校验分区名。</para>
    /// </summary>
    /// <param name="value">Candidate name.<para>候选名称。</para></param>
    /// <returns>The trimmed original name.<para>去掉首尾空白的原名称。</para></returns>
    /// <exception cref="ArgumentException">The name is empty or contains unsupported characters.
    /// <para>名称为空或含不支持的字符。</para></exception>
    public static string PartitionName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var candidate = value.Trim();
        if (candidate.Length > 64 || !PartitionNamePattern().IsMatch(candidate))
        {
            throw new ArgumentException("Invalid partition name.", nameof(value));
        }

        return candidate;
    }

    /// <summary>
    /// Validates a device variable name.
    /// <para>校验设备变量名。</para>
    /// </summary>
    /// <param name="value">Candidate name.<para>候选名称。</para></param>
    /// <returns>The trimmed original name.<para>去掉首尾空白的原名称。</para></returns>
    /// <exception cref="ArgumentException">The name is empty or too long.<para>名称为空或过长。</para></exception>
    public static string VariableName(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var candidate = value.Trim();
        if (candidate.Length > 128 || !VariableNamePattern().IsMatch(candidate))
        {
            throw new ArgumentException("Invalid device variable name.", nameof(value));
        }

        return candidate;
    }

    /// <summary>
    /// Validates an A/B slot name.
    /// <para>校验 A/B 槽位名。</para>
    /// </summary>
    /// <param name="value">Candidate slot.<para>候选槽位。</para></param>
    /// <returns>The normalized slot without a leading underscore.<para>去掉前导下划线后的规范化槽位。</para></returns>
    /// <exception cref="ArgumentException">The slot is not a recognized A/B slot.<para>槽位不是可识别的 A/B 槽位。</para></exception>
    public static string Slot(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim().TrimStart('_') switch
        {
            "a" => "a",
            "b" => "b",
            _ => throw new ArgumentException("Slot must be 'a' or 'b'.", nameof(value))
        };
    }

    /// <summary>
    /// Validates a reboot target.
    /// <para>校验重启目标。</para>
    /// </summary>
    /// <param name="value">Candidate mode.<para>候选模式。</para></param>
    /// <returns>The normalized lower-case mode.<para>规范化为小写的模式。</para></returns>
    /// <exception cref="ArgumentException">The mode is not supported.<para>模式不受支持。</para></exception>
    public static string RebootMode(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var candidate = value.Trim().ToLowerInvariant();
        return candidate is "system" or "bootloader" or "fastboot" or "recovery" or "edl" or "poweroff"
            ? candidate
            : throw new ArgumentException("Unsupported reboot mode.", nameof(value));
    }

    /// <summary>
    /// Validates a path supplied by a script without touching the file system.
    /// <para>校验脚本提供的路径，不触碰文件系统。</para>
    /// </summary>
    /// <param name="value">Candidate path.<para>候选路径。</para></param>
    /// <returns>The trimmed original path.<para>去掉首尾空白的原路径。</para></returns>
    /// <exception cref="ArgumentException">The path is not rooted, too long, or contains a parent segment.
    /// <para>路径不是绝对路径、过长或含上级目录段。</para></exception>
    public static string AbsolutePath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var candidate = value.Trim();
        if (candidate.Length > MaximumPathLength || !Path.IsPathRooted(candidate))
        {
            throw new ArgumentException("An absolute path is required.", nameof(value));
        }

        var segments = candidate.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment == ".."))
        {
            throw new ArgumentException("Parent path segments are not allowed.", nameof(value));
        }

        return candidate;
    }

    /// <summary>
    /// Validates user-visible text passed to a log or UI API.
    /// <para>校验传给日志或 UI API 的用户可见文本。</para>
    /// </summary>
    /// <param name="value">Candidate text.<para>候选文本。</para></param>
    /// <param name="maxLength">Accepted length bound.<para>接受的长度上限。</para></param>
    /// <param name="parameterName">Parameter name used in errors.<para>错误中使用的参数名。</para></param>
    /// <returns>The trimmed original text.<para>去掉首尾空白的原文本。</para></returns>
    /// <exception cref="ArgumentException">The text is empty or longer than <paramref name="maxLength"/>.
    /// <para>文本为空或长于 <paramref name="maxLength"/>。</para></exception>
    public static string Text(string value, int maxLength, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var candidate = value.Trim();
        return candidate.Length <= maxLength
            ? candidate
            : throw new ArgumentException("Text exceeds the accepted length.", parameterName);
    }

    /// <summary>
    /// Converts a value between whitelisted units.
    /// <para>在白名单单位之间转换数值。</para>
    /// </summary>
    /// <param name="value">Finite source value.<para>有限的源数值。</para></param>
    /// <param name="from">Source unit token.<para>源单位记号。</para></param>
    /// <param name="to">Target unit token.<para>目标单位记号。</para></param>
    /// <returns>The converted finite value.<para>转换后的有限数值。</para></returns>
    /// <exception cref="ArgumentException">A unit is unknown, units belong to different families, or the result is not finite.
    /// <para>单位未知、单位属于不同族，或结果不是有限数值。</para></exception>
    public static double ConvertUnit(double value, string from, string to)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentException("Value must be finite.", nameof(value));
        }

        var source = UnitFactor(from, nameof(from));
        var target = UnitFactor(to, nameof(to));
        if (!string.Equals(source.Family, target.Family, StringComparison.Ordinal))
        {
            throw new ArgumentException("Units belong to different families.", nameof(to));
        }

        var converted = value * source.Factor / target.Factor;
        return double.IsFinite(converted)
            ? converted
            : throw new ArgumentException("Conversion result is not finite.", nameof(value));
    }

    /// <summary>
    /// Validates a declarative UI panel description.
    /// <para>校验声明式 UI 面板描述。</para>
    /// </summary>
    /// <param name="json">JSON text submitted by a script.<para>脚本提交的 JSON 文本。</para></param>
    /// <param name="maxElements">Maximum accepted child element count.<para>接受的子元素数量上限。</para></param>
    /// <returns>The original JSON text when every check passes.<para>全部检查通过时的原 JSON 文本。</para></returns>
    /// <exception cref="ArgumentException">The description is malformed or exceeds a bound.
    /// <para>描述格式错误或超出上限。</para></exception>
    public static string PanelDescription(string json, int maxElements)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        if (json.Length > MaximumTextLength * 8)
        {
            throw new ArgumentException("Panel description is too long.", nameof(json));
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                MaxDepth = 8,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || type.GetString() != "panel")
            {
                throw new ArgumentException("Panel description must be an object of type 'panel'.", nameof(json));
            }

            ValidateOptionalText(root, "title", 256, nameof(json));
            if (!root.TryGetProperty("children", out var children))
            {
                return json;
            }

            if (children.ValueKind != JsonValueKind.Array || children.GetArrayLength() > maxElements)
            {
                throw new ArgumentException("Panel children are missing or exceed the element budget.", nameof(json));
            }

            foreach (var child in children.EnumerateArray())
            {
                if (child.ValueKind != JsonValueKind.Object
                    || !child.TryGetProperty("type", out var childType)
                    || childType.ValueKind != JsonValueKind.String
                    || !IsSupportedElement(childType.GetString()))
                {
                    throw new ArgumentException("Unsupported panel element type.", nameof(json));
                }

                ValidateOptionalText(child, "label", 256, nameof(json));
                if (child.TryGetProperty("id", out var id)
                    && (id.ValueKind != JsonValueKind.String
                        || id.GetString() is not { Length: > 0 and <= 64 } idText
                        || !ElementIdPattern().IsMatch(idText)))
                {
                    throw new ArgumentException("Invalid panel element id.", nameof(json));
                }
            }

            return json;
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Panel description is not valid JSON.", nameof(json), exception);
        }
    }

    private static void ValidateOptionalText(JsonElement element, string name, int maxLength, string parameterName)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.String
            || value.GetString() is not { Length: > 0 and <= 256 } text
            || text.Length > maxLength)
        {
            throw new ArgumentException($"Invalid panel {name}.", parameterName);
        }
    }

    private static bool IsSupportedElement(string? type) => type is
        "label" or "button" or "checkbox" or "input" or "select" or "progress";

    private static (string Family, double Factor) UnitFactor(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return value.Trim().ToLowerInvariant() switch
        {
            "meter" => ("length", 1d),
            "kilometer" => ("length", 1_000d),
            "centimeter" => ("length", 0.01d),
            "millimeter" => ("length", 0.001d),
            "mile" => ("length", 1_609.344d),
            "inch" => ("length", 0.0254d),
            "byte" => ("data", 1d),
            "kilobyte" => ("data", 1_024d),
            "megabyte" => ("data", 1_048_576d),
            "gigabyte" => ("data", 1_073_741_824d),
            "second" => ("time", 1d),
            "minute" => ("time", 60d),
            "hour" => ("time", 3_600d),
            _ => throw new ArgumentException("Unsupported unit.", parameterName)
        };
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex PartitionNamePattern();

    [GeneratedRegex("^[A-Za-z0-9_.:-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex VariableNamePattern();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ElementIdPattern();
}