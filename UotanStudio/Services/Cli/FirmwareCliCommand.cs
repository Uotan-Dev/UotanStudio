namespace UotanStudio.Services.Cli;

/// <summary>
/// The firmware CLI verb.
/// <para>固件 CLI 子命令。</para>
/// </summary>
public enum FirmwareCliVerb
{
    /// <summary>List registered formats.</summary>
    /// <para>列出已注册格式。</para>
    List,

    /// <summary>Read metadata for a file.</summary>
    /// <para>读取文件元数据。</para>
    Info,

    /// <summary>Unpack a firmware file to a directory.</summary>
    /// <para>将固件文件解包到目录。</para>
    Unpack,

    /// <summary>Pack an input directory/file into a firmware file.</summary>
    /// <para>将输入目录/文件打包为固件文件。</para>
    Pack,

    /// <summary>Flash an image to a device partition (Fastboot/EDL/RKDL/ADB).</summary>
    /// <para>将镜像刷写到设备分区（Fastboot/EDL/RKDL/ADB）。</para>
    Flash,

    /// <summary>Disable AVB verification on a VBMeta image.</summary>
    /// <para>禁用 VBMeta 镜像的 AVB 校验。</para>
    DisableVerification,
}

/// <summary>
/// Parsed firmware CLI command. AOT-friendly (no reflection).
/// <para>解析后的固件 CLI 命令。AOT 友好（无反射）。</para>
/// </summary>
public sealed class FirmwareCliCommand
{
    /// <summary>The verb to execute.</summary>
    /// <para>要执行的子命令。</para>
    public required FirmwareCliVerb Verb { get; init; }

    /// <summary>Primary positional input path (info/unpack/pack).</summary>
    /// <para>主位置输入路径（info/unpack/pack）。</para>
    public string? InputPath { get; init; }

    /// <summary>Output directory (unpack) or output file (pack).</summary>
    /// <para>输出目录（unpack）或输出文件（pack）。</para>
    public string? OutputPath { get; init; }

    /// <summary>Optional explicit format name (matches <c>IFormatHandler.FormatName</c> case-insensitively).</summary>
    /// <para>可选的显式格式名（按 <c>IFormatHandler.FormatName</c> 不区分大小写匹配）。</para>
    public string? FormatName { get; init; }

    /// <summary>Whether <c>--no-overwrite</c> was passed (unpack only; default is overwrite).</summary>
    /// <para>是否传入了 <c>--no-overwrite</c>（仅 unpack；默认覆盖）。</para>
    public bool NoOverwrite { get; init; }

    /// <summary>Target partition name (flash only).</summary>
    /// <para>目标分区名（仅 flash）。</para>
    public string? Partition { get; init; }

    /// <summary>Preferred device serial number (flash only; optional).</summary>
    /// <para>首选设备序列号（仅 flash；可选）。</para>
    public string? Serial { get; init; }

    /// <summary>Whether help was requested (<c>--help</c>/<c>-h</c>).</summary>
    /// <para>是否请求了帮助（<c>--help</c>/<c>-h</c>）。</para>
    public bool HelpRequested { get; init; }

    /// <summary>Whether version was requested (<c>--version</c>/<c>-v</c>).</summary>
    /// <para>是否请求了版本（<c>--version</c>/<c>-v</c>）。</para>
    public bool VersionRequested { get; init; }

    /// <summary>Whether this command is a help/version-only invocation (no verb).</summary>
    /// <para>是否仅为帮助/版本调用（无子命令）。</para>
    public bool IsHelpOrVersion => HelpRequested || VersionRequested;
}

/// <summary>
/// Result of parsing the firmware CLI arguments: either a ready command or an error message
/// paired with the <c>Usage</c> exit code.
/// </summary>
/// <para>固件 CLI 参数解析结果：要么是就绪命令，要么是错误信息（搭配 <c>Usage</c> 退出码）。</para>
public readonly record struct FirmwareCliParseResult(
    FirmwareCliCommand? Command,
    string? Error,
    bool IsError)
{
    /// <summary>Creates a successful parse result.</summary>
    /// <para>创建成功的解析结果。</para>
    public static FirmwareCliParseResult Ok(FirmwareCliCommand command) => new(command, null, false);

    /// <summary>Creates an error parse result.</summary>
    /// <para>创建错误的解析结果。</para>
    public static FirmwareCliParseResult Fail(string error) => new(null, error, true);
}