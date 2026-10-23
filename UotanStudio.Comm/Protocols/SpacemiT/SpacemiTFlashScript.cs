using System.Text;
using YamlDotNet.RepresentationModel;

namespace UotanStudio.Comm.Protocols.SpacemiT;

/// <summary>
/// One action of a SpacemiT <c>fastboot.yaml</c> flash script. Concrete actions are the
/// derived records; common fields carry the vendor retry/skip controls.
/// <para>SpacemiT <c>fastboot.yaml</c> 刷机脚本中的一个动作。具体动作为派生记录；
/// 公共字段承载厂商的重试/跳过控制。</para>
/// </summary>
/// <param name="SkipWhen">Guard expression against the variable bag; when it evaluates true the action is skipped. <para>对变量包求值的守卫表达式；为真时跳过该动作。</para></param>
/// <param name="SkipFail">When true, a failure after all retries is skipped instead of aborting the run. <para>为真时，重试耗尽后的失败会被跳过而不是中止整个流程。</para></param>
/// <param name="Retry">Total attempt count for the action (at least 1). <para>动作的总尝试次数（至少 1）。</para></param>
/// <param name="Timeout">Per-attempt wire timeout; <see langword="null"/> keeps the session default. <para>单次尝试的线上超时；为 <see langword="null"/> 时沿用会话默认。</para></param>
public abstract record SpacemiTFlashAction(
    string? SkipWhen,
    bool SkipFail,
    int Retry,
    TimeSpan? Timeout);

/// <summary>A <c>getvar</c> action; the answer can seed the variable bag via <c>set</c>/<c>set_var</c>. <para><c>getvar</c> 动作；应答可通过 <c>set</c>/<c>set_var</c> 写入变量包。</para></summary>
/// <param name="Variable">Variable name queried without the <c>getvar:</c> prefix. <para>查询的变量名，不含 <c>getvar:</c> 前缀。</para></param>
/// <param name="BagKey">Variable-bag key the answer is stored under, or <see langword="null"/> to discard. <para>应答写入的变量包键；为 <see langword="null"/> 时丢弃。</para></param>
/// <param name="SkipWhen">Guard expression evaluated against the variable bag; true skips the action. <para>对变量包求值的守卫表达式；为真时跳过该动作。</para></param>
/// <param name="SkipFail">True skips the action after all retries fail. <para>为真时重试耗尽后的失败会跳过该动作。</para></param>
/// <param name="Retry">Total attempt count, at least 1. <para>总尝试次数，至少 1。</para></param>
/// <param name="Timeout">Per-attempt wire timeout; null keeps the session default. <para>单次尝试的线上超时；null 沿用会话默认。</para></param>
public sealed record SpacemiTGetVarAction(string Variable, string? BagKey, string? SkipWhen, bool SkipFail, int Retry, TimeSpan? Timeout)
    : SpacemiTFlashAction(SkipWhen, SkipFail, Retry, Timeout);

/// <summary>A <c>stage</c> action: download a package file into device RAM without writing storage. <para><c>stage</c> 动作：把包内文件下载进设备内存，不写存储。</para></summary>
/// <param name="File">Package-relative file path to stage. <para>要暂存的包内相对文件路径。</para></param>
/// <param name="SkipWhen">Guard expression evaluated against the variable bag; true skips the action. <para>对变量包求值的守卫表达式；为真时跳过该动作。</para></param>
/// <param name="SkipFail">True skips the action after all retries fail. <para>为真时重试耗尽后的失败会跳过该动作。</para></param>
/// <param name="Retry">Total attempt count, at least 1. <para>总尝试次数，至少 1。</para></param>
/// <param name="Timeout">Per-attempt wire timeout; null keeps the session default. <para>单次尝试的线上超时；null 沿用会话默认。</para></param>
public sealed record SpacemiTStageAction(string File, string? SkipWhen, bool SkipFail, int Retry, TimeSpan? Timeout)
    : SpacemiTFlashAction(SkipWhen, SkipFail, Retry, Timeout);

/// <summary>A <c>continue</c> action: the device leaves fastboot to run the staged image and re-enumerates. <para><c>continue</c> 动作：设备退出 fastboot 运行暂存镜像并重新枚举。</para></summary>
/// <param name="SkipWhen">Guard expression evaluated against the variable bag; true skips the action. <para>对变量包求值的守卫表达式；为真时跳过该动作。</para></param>
/// <param name="SkipFail">True skips the action after all retries fail. <para>为真时重试耗尽后的失败会跳过该动作。</para></param>
/// <param name="Retry">Total attempt count, at least 1. <para>总尝试次数，至少 1。</para></param>
/// <param name="Timeout">Per-attempt wire timeout; null keeps the session default. <para>单次尝试的线上超时；null 沿用会话默认。</para></param>
public sealed record SpacemiTContinueAction(string? SkipWhen, bool SkipFail, int Retry, TimeSpan? Timeout)
    : SpacemiTFlashAction(SkipWhen, SkipFail, Retry, Timeout);

/// <summary>An <c>oem</c> action with a raw OEM command body. <para><c>oem</c> 动作，携带原始 OEM 命令体。</para></summary>
/// <param name="Args">Command body sent after the <c>oem </c> prefix. <para>在 <c>oem </c> 前缀之后发送的命令体。</para></param>
/// <param name="SkipWhen">Guard expression evaluated against the variable bag; true skips the action. <para>对变量包求值的守卫表达式；为真时跳过该动作。</para></param>
/// <param name="SkipFail">True skips the action after all retries fail. <para>为真时重试耗尽后的失败会跳过该动作。</para></param>
/// <param name="Retry">Total attempt count, at least 1. <para>总尝试次数，至少 1。</para></param>
/// <param name="Timeout">Per-attempt wire timeout; null keeps the session default. <para>单次尝试的线上超时；null 沿用会话默认。</para></param>
public sealed record SpacemiTOemAction(string Args, string? SkipWhen, bool SkipFail, int Retry, TimeSpan? Timeout)
    : SpacemiTFlashAction(SkipWhen, SkipFail, Retry, Timeout);

/// <summary>A <c>multi_flash</c> action: resolve partition tables from device size variables and flash the table plus every partition entry. <para><c>multi_flash</c> 动作：按设备尺寸变量解析分区表，并刷写分区表与全部分区条目。</para></summary>
/// <param name="RelatePartition">File-name templates such as <c>partition_{size0}.json</c>, where <c>{sizeN}</c> is replaced with the raw answer of the N-th size variable. <para>文件名模板（如 <c>partition_{size0}.json</c>），<c>{sizeN}</c> 被替换为第 N 个尺寸变量的原始应答。</para></param>
/// <param name="SkipWhen">Guard expression evaluated against the variable bag; true skips the action. <para>对变量包求值的守卫表达式；为真时跳过该动作。</para></param>
/// <param name="SkipFail">True skips the action after all retries fail. <para>为真时重试耗尽后的失败会跳过该动作。</para></param>
/// <param name="Retry">Total attempt count, at least 1. <para>总尝试次数，至少 1。</para></param>
/// <param name="Timeout">Per-attempt wire timeout; null keeps the session default. <para>单次尝试的线上超时；null 沿用会话默认。</para></param>
public sealed record SpacemiTMultiFlashAction(IReadOnlyList<string> RelatePartition, string? SkipWhen, bool SkipFail, int Retry, TimeSpan? Timeout)
    : SpacemiTFlashAction(SkipWhen, SkipFail, Retry, Timeout);

/// <summary>A direct <c>flash:&lt;partition&gt;</c> action for packages that hard-code targets. <para>直接 <c>flash:&lt;分区&gt;</c> 的动作，供硬编码目标的包使用。</para></summary>
/// <param name="Partition">Target partition name. <para>目标分区名。</para></param>
/// <param name="File">Package-relative image file. <para>包内相对镜像文件。</para></param>
/// <param name="SkipWhen">Guard expression evaluated against the variable bag; true skips the action. <para>对变量包求值的守卫表达式；为真时跳过该动作。</para></param>
/// <param name="SkipFail">True skips the action after all retries fail. <para>为真时重试耗尽后的失败会跳过该动作。</para></param>
/// <param name="Retry">Total attempt count, at least 1. <para>总尝试次数，至少 1。</para></param>
/// <param name="Timeout">Per-attempt wire timeout; null keeps the session default. <para>单次尝试的线上超时；null 沿用会话默认。</para></param>
public sealed record SpacemiTFlashPartitionAction(string Partition, string File, string? SkipWhen, bool SkipFail, int Retry, TimeSpan? Timeout)
    : SpacemiTFlashAction(SkipWhen, SkipFail, Retry, Timeout);

/// <summary>An <c>erase:&lt;partition&gt;</c> action. <para><c>erase:&lt;分区&gt;</c> 动作。</para></summary>
/// <param name="Partition">Partition name to erase. <para>要擦除的分区名。</para></param>
/// <param name="SkipWhen">Guard expression evaluated against the variable bag; true skips the action. <para>对变量包求值的守卫表达式；为真时跳过该动作。</para></param>
/// <param name="SkipFail">True skips the action after all retries fail. <para>为真时重试耗尽后的失败会跳过该动作。</para></param>
/// <param name="Retry">Total attempt count, at least 1. <para>总尝试次数，至少 1。</para></param>
/// <param name="Timeout">Per-attempt wire timeout; null keeps the session default. <para>单次尝试的线上超时；null 沿用会话默认。</para></param>
public sealed record SpacemiTEraseAction(string Partition, string? SkipWhen, bool SkipFail, int Retry, TimeSpan? Timeout)
    : SpacemiTFlashAction(SkipWhen, SkipFail, Retry, Timeout);

/// <summary>A <c>reboot</c> action, optionally into a target. <para><c>reboot</c> 动作，可指定目标。</para></summary>
/// <param name="Target">Reboot target suffix, or <see langword="null"/> for a plain reboot. <para>重启目标后缀；为 <see langword="null"/> 时普通重启。</para></param>
/// <param name="SkipWhen">Guard expression evaluated against the variable bag; true skips the action. <para>对变量包求值的守卫表达式；为真时跳过该动作。</para></param>
/// <param name="SkipFail">True skips the action after all retries fail. <para>为真时重试耗尽后的失败会跳过该动作。</para></param>
/// <param name="Retry">Total attempt count, at least 1. <para>总尝试次数，至少 1。</para></param>
/// <param name="Timeout">Per-attempt wire timeout; null keeps the session default. <para>单次尝试的线上超时；null 沿用会话默认。</para></param>
public sealed record SpacemiTRebootAction(string? Target, string? SkipWhen, bool SkipFail, int Retry, TimeSpan? Timeout)
    : SpacemiTFlashAction(SkipWhen, SkipFail, Retry, Timeout);

/// <summary>
/// A parsed SpacemiT <c>fastboot.yaml</c> flash script: package version, supported board
/// codenames and the ordered action list. Parsing uses the reflection-free
/// <see cref="YamlStream"/> representation so the loader stays AOT-compatible.
/// <para>解析后的 SpacemiT <c>fastboot.yaml</c> 刷机脚本：包版本、支持的项目代号与有序动作
/// 列表。解析使用无反射的 <see cref="YamlStream"/> 表示，加载器保持 AOT 兼容。</para>
/// </summary>
public sealed class SpacemiTFlashScript
{
    private const int MaxActions = 256;

    /// <summary>Gets the script version string. <para>获取脚本版本字符串。</para></summary>
    public string Version { get; }

    /// <summary>Gets the board codenames the package supports (e.g. <c>k1x</c>, <c>k3</c>). <para>获取包支持的项目代号（如 <c>k1x</c>、<c>k3</c>）。</para></summary>
    public IReadOnlyList<string> Support { get; }

    /// <summary>Gets the ordered actions to execute. <para>获取要执行的有序动作。</para></summary>
    public IReadOnlyList<SpacemiTFlashAction> Actions { get; }

    private SpacemiTFlashScript(string version, IReadOnlyList<string> support, IReadOnlyList<SpacemiTFlashAction> actions)
    {
        Version = version;
        Support = support;
        Actions = actions;
    }

    /// <summary>
    /// Loads a flash script from a <c>fastboot.yaml</c> file.
    /// <para>从 <c>fastboot.yaml</c> 文件加载刷机脚本。</para>
    /// </summary>
    /// <param name="path">Path to the YAML file. <para>YAML 文件路径。</para></param>
    /// <returns>The parsed script. <para>解析后的脚本。</para></returns>
    /// <exception cref="InvalidDataException">The file is not a valid SpacemiT flash script. <para>文件不是有效的 SpacemiT 刷机脚本。</para></exception>
    /// <exception cref="IOException">The file cannot be read. <para>文件无法读取。</para></exception>
    public static SpacemiTFlashScript Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Parse(stream);
    }

    /// <summary>
    /// Parses a flash script from untrusted YAML.
    /// <para>从不可信 YAML 解析刷机脚本。</para>
    /// </summary>
    /// <param name="stream">YAML source, read to EOF and rejected beyond the 1 MiB script-size bound. <para>YAML 来源，读到末尾；超过 1 MiB 脚本大小上限时拒绝。</para></param>
    /// <returns>The parsed script. <para>解析后的脚本。</para></returns>
    /// <exception cref="InvalidDataException">The YAML is not a valid SpacemiT flash script. <para>YAML 不是有效的 SpacemiT 刷机脚本。</para></exception>
    public static SpacemiTFlashScript Parse(Stream stream)
    {
        YamlStream yaml;
        try
        {
            using var buffered = new MemoryStream();
            stream.CopyTo(buffered, 64 * 1024);
            if (buffered.Length > 1024 * 1024)
            {
                throw new InvalidDataException("fastboot.yaml exceeds the 1 MiB size limit.");
            }

            buffered.Position = 0;
            yaml = [];
            yaml.Load(new StreamReader(buffered, Encoding.UTF8));
        }
        catch (Exception e) when (e is YamlDotNet.Core.YamlException or InvalidDataException && e is not InvalidDataException)
        {
            throw new InvalidDataException("Malformed fastboot.yaml.", e);
        }

        if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new InvalidDataException("fastboot.yaml: root must be a mapping.");
        }

        if (root.Children.TryGetValue("actions", out var actionsNode) && actionsNode is YamlSequenceNode rawActions)
        {
            if (rawActions.Children.Count > MaxActions)
            {
                throw new InvalidDataException($"fastboot.yaml declares {rawActions.Children.Count} actions, above the {MaxActions} limit.");
            }
        }
        else
        {
            throw new InvalidDataException("fastboot.yaml: actions is required.");
        }

        var version = GetString(root, "version") ?? string.Empty;
        var support = root.Children.TryGetValue("support", out var supportNode) && supportNode is YamlSequenceNode supportSeq
            ? supportSeq.Children
                .Select(node => (node as YamlScalarNode)?.Value?.Trim())
                .Where(value => !string.IsNullOrEmpty(value))
                .Cast<string>()
                .ToList()
            : [];

        var actions = new List<SpacemiTFlashAction>(rawActions.Children.Count);
        foreach (var entry in rawActions.Children)
        {
            actions.Add(MapAction(entry));
        }

        return new SpacemiTFlashScript(version, support, actions);
    }

    private static SpacemiTFlashAction MapAction(YamlNode entry)
    {
        if (entry is not YamlMappingNode mapping || mapping.Children.Count == 0)
        {
            throw new InvalidDataException("fastboot.yaml: each action must be a mapping with one action key.");
        }

        var keyNode = (YamlScalarNode)mapping.Children.Keys.First();
        var name = keyNode.Value?.Trim() ?? string.Empty;
        var body = mapping.Children[nameNode(name)] as YamlMappingNode ?? new YamlMappingNode();
        var skipWhen = GetString(body, "skip_when");
        var skipFail = GetBool(body, "skip_fail") ?? false;
        var retry = Math.Max(1, GetInt(body, "retry") ?? 1);
        var timeout = GetTimeout(body);

        return name switch
        {
            "getvar" => new SpacemiTGetVarAction(
                Require(GetString(body, "args"), name), GetString(body, "set") ?? GetString(body, "set_var"),
                skipWhen, skipFail, retry, timeout),
            "stage" => new SpacemiTStageAction(Require(GetString(body, "file"), name), skipWhen, skipFail, retry, timeout),
            "continue" => new SpacemiTContinueAction(skipWhen, skipFail, retry, timeout),
            "oem" => new SpacemiTOemAction(Require(GetString(body, "args"), name), skipWhen, skipFail, retry, timeout),
            "multi_flash" => new SpacemiTMultiFlashAction(GetStringList(body, "relate_partition"), skipWhen, skipFail, retry, timeout),
            "flash" => new SpacemiTFlashPartitionAction(
                Require(GetString(body, "partition"), name), Require(GetString(body, "file"), name),
                skipWhen, skipFail, retry, timeout),
            "erase" => new SpacemiTEraseAction(Require(GetString(body, "partition"), name), skipWhen, skipFail, retry, timeout),
            "reboot" => new SpacemiTRebootAction(GetString(body, "target"), skipWhen, skipFail, retry, timeout),
            _ => throw new InvalidDataException($"fastboot.yaml: unknown action '{name}'."),
        };
    }

    private static YamlScalarNode nameNode(string name) => new(name);

    private static TimeSpan? GetTimeout(YamlMappingNode body)
    {
        if (body.Children.TryGetValue("timeout", out var node) && node is YamlMappingNode timeout)
        {
            var ms = GetInt(timeout, "milliseconds") ?? 0;
            var seconds = GetInt(timeout, "seconds") ?? 0;
            var minutes = GetInt(timeout, "minutes") ?? 0;
            if (ms != 0 || seconds != 0 || minutes != 0)
            {
                return TimeSpan.FromMilliseconds(ms + 1000L * seconds + 60_000L * minutes);
            }
        }

        return null;
    }

    private static string? GetString(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var value) &&
        value is YamlScalarNode scalar
            ? scalar.Value
            : null;

    private static bool? GetBool(YamlMappingNode node, string key) =>
        GetString(node, key) is { } text && bool.TryParse(text, out var parsed) ? parsed : null;

    private static int? GetInt(YamlMappingNode node, string key)
    {
        if (GetString(node, key) is not { } text ||
            !int.TryParse(text, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            return null;
        }

        return parsed;
    }

    private static IReadOnlyList<string> GetStringList(YamlMappingNode node, string key)
    {
        if (!node.Children.TryGetValue(new YamlScalarNode(key), out var value) || value is not YamlSequenceNode sequence)
        {
            return [];
        }

        return sequence.Children
            .Select(item => (item as YamlScalarNode)?.Value?.Trim())
            .Where(item => !string.IsNullOrEmpty(item))
            .Cast<string>()
            .ToList();
    }

    private static string Require(string? value, string action)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"fastboot.yaml: action '{action}' is missing its required field.");
        }

        return value.Trim();
    }
}