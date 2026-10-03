using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace UotanStudio.Comm.Protocols.Edl.Auth;

/// <summary>Selects which OnePlus project verification command family a profile belongs to.
/// <para>选择某个配置所属的 OnePlus 项目校验命令家族。</para></summary>
public enum EdlOnePlusProjectFamily
{
    /// <summary>Uses the <c>setprojmodel</c> command with the fixed reference timestamp.
    /// <para>使用 <c>setprojmodel</c> 命令与固定参考时间戳。</para></summary>
    ProjectModel,

    /// <summary>Uses the <c>setswprojmodel</c> command with the timestamp reported by <c>setprocstart</c>.
    /// <para>使用 <c>setswprojmodel</c> 命令与 <c>setprocstart</c> 上报的时间戳。</para></summary>
    SoftwareProjectModel
}

/// <summary>
/// Carries the per-project constants that a OnePlus verification token is built from.
/// <para>保存构造 OnePlus 校验令牌所需的逐项目常量。</para>
/// </summary>
/// <remarks>
/// <para>The reference tools keep these constants in a fixed table instead of reading them from the target, so a
/// profile is only as correct as the project selection it was resolved from.
/// <see cref="ReferenceTimestamp"/> is the literal the reference tools embed; the software-project family mixes
/// the device timestamp reported by <c>setprocstart</c> into its AES key instead of replacing this literal.</para>
/// <para>参考工具把这些常量写死在固定表中，而不是从目标读取，因此配置的正确性完全取决于它所依据的项目选择。
/// <see cref="ReferenceTimestamp"/> 是参考工具内置的字面量；软件项目家族把 <c>setprocstart</c> 上报的设备时间戳
/// 混入 AES 密钥，而不是替换该字面量。</para>
/// </remarks>
public sealed record EdlOnePlusProjectProfile
{
    /// <summary>Gets the timestamp literal the reference tools embed in every verification token.
    /// <para>获取参考工具写入每个校验令牌的固定时间戳。</para></summary>
    /// <value>The documented value 1604949411. <para>文档取值 1604949411。</para></value>
    public const long ReferenceTimestampDefault = 1604949411;

    /// <summary>Gets the project name written into the token.
    /// <para>获取写入令牌的项目名。</para></summary>
    /// <value>At most 64 ASCII letters or digits; a hexadecimal project code for the software-project family.
    /// <para>最多 64 个 ASCII 字母或数字；软件项目家族使用十六进制项目代码。</para></value>
    public required string ProjectId { get; init; }

    /// <summary>Gets the project production key mixed into the model hash.
    /// <para>获取混入型号摘要的项目生产密钥。</para></summary>
    /// <value>Sixteen hexadecimal characters. <para>十六个十六进制字符。</para></value>
    public required string ProductionKey { get; init; }

    /// <summary>Gets the project random postfix written into the token.
    /// <para>获取写入令牌的项目随机后缀。</para></summary>
    /// <value>Sixteen ASCII letters or digits. <para>十六个 ASCII 字母或数字。</para></value>
    public required string RandomPostfix { get; init; }

    /// <summary>Gets the firmware version string written into the token.
    /// <para>获取写入令牌的固件版本字符串。</para></summary>
    /// <value>A non-blank ASCII string such as <c>guacamoles_21_O.22_191107</c>.
    /// <para>非空 ASCII 字符串，例如 <c>guacamoles_21_O.22_191107</c>。</para></value>
    public required string VersionString { get; init; }

    /// <summary>Gets the command family that this profile feeds.
    /// <para>获取该配置对应的命令家族。</para></summary>
    /// <value>The verification family. <para>校验家族。</para></value>
    public required EdlOnePlusProjectFamily Family { get; init; }

    /// <summary>Gets the customer flag written into the project-model token.
    /// <para>获取写入项目型号令牌的定制标志。</para></summary>
    /// <value>A non-negative flag; the reference table uses zero.
    /// <para>非负标志；参考表取 0。</para></value>
    public int CustomerFlag { get; init; }

    /// <summary>Gets the ATO build state written into the software-project token.
    /// <para>获取写入软件项目令牌的 ATO 构建状态。</para></summary>
    /// <value>A non-negative state; the reference table uses zero.
    /// <para>非负状态；参考表取 0。</para></value>
    public int AtoBuildState { get; init; }

    /// <summary>Gets the flash mode written into the software-project token.
    /// <para>获取写入软件项目令牌的刷机模式。</para></summary>
    /// <value>A non-negative mode; the reference table uses zero.
    /// <para>非负模式；参考表取 0。</para></value>
    public int FlashMode { get; init; }

    /// <summary>Gets the timestamp literal written into both token layouts.
    /// <para>获取写入两种令牌布局的时间戳字面量。</para></summary>
    /// <value>A positive value; <see cref="ReferenceTimestampDefault"/> by default.
    /// <para>正数；默认取 <see cref="ReferenceTimestampDefault"/>。</para></value>
    public long ReferenceTimestamp { get; init; } = ReferenceTimestampDefault;

    /// <summary>Gets the short device description that this profile was resolved from.
    /// <para>获取解析该配置时所依据的简短设备描述。</para></summary>
    /// <value>The catalog description, or an empty string for a hand-built profile.
    /// <para>目录描述；手工构造的配置为空字符串。</para></value>
    public string Description { get; init; } = string.Empty;

    /// <summary>Validates the field shapes and the family-specific project code.
    /// <para>校验字段格式与家族相关的项目代码。</para></summary>
    /// <exception cref="ArgumentException">A field has the wrong length, character class or sign.
    /// <para>某个字段的长度、字符类别或符号不正确。</para></exception>
    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ProjectId);
        if (ProjectId.Length > 64 || !ProjectId.All(char.IsAsciiLetterOrDigit))
        {
            throw new ArgumentException("The profile project name is not at most 64 ASCII letters or digits.");
        }

        if (ProductionKey.Length != 16 || !ProductionKey.All(char.IsAsciiHexDigit))
        {
            throw new ArgumentException("The profile production key is not sixteen hexadecimal characters.");
        }

        if (RandomPostfix.Length != 16 || !RandomPostfix.All(char.IsAsciiLetterOrDigit))
        {
            throw new ArgumentException("The profile random postfix is not sixteen ASCII letters or digits.");
        }

        if (string.IsNullOrWhiteSpace(VersionString) || !VersionString.All(char.IsAscii))
        {
            throw new ArgumentException("The profile version string must be non-blank ASCII.");
        }

        if (!Enum.IsDefined(Family))
        {
            throw new ArgumentException("The profile verification family is not supported.");
        }

        if (ReferenceTimestamp <= 0 || CustomerFlag < 0 || AtoBuildState < 0 || FlashMode < 0)
        {
            throw new ArgumentException("The profile timestamp must be positive and the flags non-negative.");
        }

        if (Family == EdlOnePlusProjectFamily.SoftwareProjectModel &&
            (!uint.TryParse(ProjectId, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)))
        {
            throw new ArgumentException("The software-project profile name must be a hexadecimal project code.");
        }
    }
}

/// <summary>
/// Resolves OnePlus verification constants for a project identifier from the reference tool's fixed table.
/// <para>按项目标识符从参考工具的固定表解析 OnePlus 校验常量。</para>
/// </summary>
/// <remarks>
/// <para>The table maps the project identifier a device reports to the command family, the project name written
/// into the token, and the production key rule. Identifiers absent from the table have no derivable profile here,
/// because their constants are not guessed. Matching is case-sensitive since some identifiers end in a
/// hexadecimal letter.</para>
/// <para>该表把设备上报的项目标识符映射到命令家族、写入令牌的项目名以及生产密钥规则。表内没有的标识符在此没有
/// 可推导的配置，因为其常量不被猜测。部分标识符以十六进制字母结尾，因此匹配区分大小写。</para>
/// </remarks>
public static class EdlOnePlusProjectCatalog
{
    private const string GuacamolesPostfix = "0iyFR00pPnoqjVNL";
    private const string GuacamolesVersion = "guacamoles_21_O.22_191107";
    private const string BilliePostfix = "c75oVnz8yUgLZObh";
    private const string BillieVersion = "billie8_14_E.01_201028";
    private static readonly string[] AltProductionKeyProjects = ["18825", "18801"];

    private static readonly (string Project, string Description, EdlOnePlusProjectFamily Family, string? TokenName)[]
        Entries =
        [
            ("16859", "OnePlus 5 (cheeseburger)", EdlOnePlusProjectFamily.ProjectModel, null),
            ("17801", "OnePlus 5T (dumpling)", EdlOnePlusProjectFamily.ProjectModel, null),
            ("17819", "OnePlus 6 (enchilada)", EdlOnePlusProjectFamily.ProjectModel, null),
            ("18801", "OnePlus 6T (fajita)", EdlOnePlusProjectFamily.ProjectModel, null),
            ("18811", "OnePlus 6T T-Mobile (fajitat)", EdlOnePlusProjectFamily.ProjectModel, null),
            ("18857", "OnePlus 7 (guacamoleb)", EdlOnePlusProjectFamily.ProjectModel, null),
            ("18821", "OnePlus 7 Pro (guacamole)", EdlOnePlusProjectFamily.ProjectModel, null),
            ("18825", "OnePlus 7 Pro 5G Sprint (guacamoles)", EdlOnePlusProjectFamily.ProjectModel, null),
            ("18827", "OnePlus 7 Pro 5G EE (guacamoleg)", EdlOnePlusProjectFamily.ProjectModel, null),
            ("18831", "OnePlus 7 Pro T-Mobile (guacamolet)", EdlOnePlusProjectFamily.ProjectModel, null),
            ("18865", "OnePlus 7T (hotdogb)", EdlOnePlusProjectFamily.ProjectModel, null),
            ("19801", "OnePlus 7T Pro (hotdog)", EdlOnePlusProjectFamily.ProjectModel, null),
            ("19861", "OnePlus 7T Pro 5G T-Mobile (hotdogg)", EdlOnePlusProjectFamily.ProjectModel, null),
            ("19863", "OnePlus 7T T-Mobile (hotdogt)", EdlOnePlusProjectFamily.ProjectModel, null),
            ("20818", "OnePlus Nord (dre8t)", EdlOnePlusProjectFamily.ProjectModel, null),
            ("2083C", "OnePlus Nord CE (dre8m)", EdlOnePlusProjectFamily.ProjectModel, null),
            ("2083D", "OnePlus Nord N9 (dre9)", EdlOnePlusProjectFamily.ProjectModel, null),
            ("19805", "OnePlus 8T (kebab)", EdlOnePlusProjectFamily.ProjectModel, "1a5ec176"),
            ("19811", "OnePlus 8 Pro (instantnoodlep)", EdlOnePlusProjectFamily.ProjectModel, "40217c07"),
            ("19815", "OnePlus 9 Pro (lemonadep)", EdlOnePlusProjectFamily.ProjectModel, "9c151c7f"),
            ("19821", "OnePlus 8 (instantnoodle)", EdlOnePlusProjectFamily.ProjectModel, "0cffee8a"),
            ("19825", "OnePlus 9 (lemonade)", EdlOnePlusProjectFamily.ProjectModel, "0898dcd6"),
            ("19855", "OnePlus 8 T-Mobile (instantnoodlet)", EdlOnePlusProjectFamily.ProjectModel, "6d9215b4"),
            ("19867", "OnePlus 8 Verizon (instantnoodlev)", EdlOnePlusProjectFamily.ProjectModel, "4107b2d4"),
            ("19868", "OnePlus 8 Visible (instantnoodlevis)", EdlOnePlusProjectFamily.ProjectModel, "178d8213"),
            ("20801", "OnePlus Nord (avicii)", EdlOnePlusProjectFamily.ProjectModel, "eacf50e7"),
            ("20809", "OnePlus 8T T-Mobile (kebabt)", EdlOnePlusProjectFamily.ProjectModel, "d6bc8c36"),
            ("20828", "OnePlus 9R (lemonades)", EdlOnePlusProjectFamily.ProjectModel, "f498b60f"),
            ("20838", "OnePlus 9R variant (lemonades)", EdlOnePlusProjectFamily.ProjectModel, "f498b60f"),
            ("20851", "OnePlus 9 variant (lemonade)", EdlOnePlusProjectFamily.ProjectModel, "0898dcd6"),
            ("20852", "OnePlus 9 variant (lemonade)", EdlOnePlusProjectFamily.ProjectModel, "0898dcd6"),
            ("20853", "OnePlus 9 variant (lemonade)", EdlOnePlusProjectFamily.ProjectModel, "0898dcd6"),
            ("20854", "OnePlus 9 T-Mobile (lemonadet)", EdlOnePlusProjectFamily.ProjectModel, "16225d4e"),
            ("20857", "OnePlus 9 Pro variant (lemonadep)", EdlOnePlusProjectFamily.ProjectModel, "9c151c7f"),
            ("20859", "OnePlus 9 Pro variant (lemonadep)", EdlOnePlusProjectFamily.ProjectModel, "9c151c7f"),
            ("2085A", "OnePlus 9 Pro T-Mobile (lemonadept)", EdlOnePlusProjectFamily.ProjectModel, "7f19519a"),
            ("20880", "OnePlus Nord 100 Metro (billie2t)", EdlOnePlusProjectFamily.SoftwareProjectModel, "6ccf5913"),
            ("20881", "OnePlus Nord 100 Global (billie2)", EdlOnePlusProjectFamily.SoftwareProjectModel, "fa9ff378"),
            ("20882", "OnePlus Nord 100 T-Mobile (billie2t)", EdlOnePlusProjectFamily.SoftwareProjectModel, "4ca1e84e"),
            ("20883", "OnePlus Nord 100 Europe (billie2)", EdlOnePlusProjectFamily.SoftwareProjectModel, "ad9dba4a"),
            ("20885", "OnePlus Nord 10 5G Metro (billie8t)", EdlOnePlusProjectFamily.SoftwareProjectModel, "3a403a71"),
            ("20886", "OnePlus Nord 10 5G Global (billie8)", EdlOnePlusProjectFamily.SoftwareProjectModel, "b8bd9e39"),
            ("20888", "OnePlus Nord 10 5G T-Mobile (billie8t)", EdlOnePlusProjectFamily.SoftwareProjectModel, "142f1bd7"),
            ("20889", "OnePlus Nord 10 5G Europe (billie8)", EdlOnePlusProjectFamily.SoftwareProjectModel, "f2056ae1"),
        ];

    /// <summary>Gets the project identifiers that the reference table can resolve.
    /// <para>获取参考表可解析的项目标识符。</para></summary>
    /// <value>The identifiers in sorted order. <para>按排序后的标识符。</para></value>
    public static IReadOnlyList<string> ProjectIds { get; } =
        [.. Entries.Select(entry => entry.Project).Order(StringComparer.Ordinal)];

    /// <summary>Resolves one profile from the reference table without probing the device.
    /// <para>按参考表解析一个配置，不探测设备。</para></summary>
    /// <param name="projectId">The project identifier reported by the device.
    /// <para>设备上报的项目标识符。</para></param>
    /// <param name="profile">The resolved profile, or <see langword="null"/> when unknown.
    /// <para>解析得到的配置；未知时为 <see langword="null"/>。</para></param>
    /// <returns><see langword="true"/> when the identifier is in the table.
    /// <para>标识符位于表内时返回 <see langword="true"/>。</para></returns>
    public static bool TryResolve(string projectId, out EdlOnePlusProjectProfile? profile)
    {
        var entry = Array.Find(Entries, candidate =>
            string.Equals(candidate.Project, projectId, StringComparison.Ordinal));
        if (entry.Project is null)
        {
            profile = null;
            return false;
        }

        var software = entry.Family == EdlOnePlusProjectFamily.SoftwareProjectModel;
        profile = new EdlOnePlusProjectProfile
        {
            ProjectId = entry.TokenName ?? entry.Project,
            ProductionKey = AltProductionKeyProjects.Contains(entry.Project, StringComparer.Ordinal)
                ? "b2fad511325185e5"
                : "7016147d58e8c038",
            RandomPostfix = software ? BilliePostfix : GuacamolesPostfix,
            VersionString = software ? BillieVersion : GuacamolesVersion,
            Family = entry.Family,
            Description = entry.Description,
        };
        return true;
    }

    /// <summary>Describes one catalogued project for user selection.
    /// <para>为界面选择描述一个目录内的项目。</para></summary>
    /// <param name="projectId">The project identifier to describe. <para>要描述的项目标识符。</para></param>
    /// <returns>The reference description, or an empty string when the identifier is unknown.
    /// <para>参考描述；标识符未知时为空字符串。</para></returns>
    public static string Describe(string projectId)
    {
        var entry = Array.Find(Entries, candidate =>
            string.Equals(candidate.Project, projectId, StringComparison.Ordinal));
        return entry.Description ?? string.Empty;
    }
}

/// <summary>
/// Builds OnePlus <c>setprojmodel</c> and <c>setswprojmodel</c> credentials from one explicit profile.
/// <para>由单个显式配置构造 OnePlus <c>setprojmodel</c> 与 <c>setswprojmodel</c> 凭据。</para>
/// </summary>
/// <remarks>
/// <para>Both families encrypt a comma-joined field list with AES-256-CBC under a key made of fixed reference
/// constants plus the exchange project key. The software-project family appends the little-endian device
/// timestamp reported by <c>setprocstart</c> to that key and pads the plaintext to 512 bytes, so its token is
/// twice as long. The project key is created once per provider instance because the device pairs the reported
/// <c>pk</c> with every later per-command token.</para>
/// <para>两个家族都用“固定参考常量 + 本次交换的项目密钥”作为 AES-256-CBC 密钥，加密逗号拼接的字段表。软件项目
/// 家族把 <c>setprocstart</c> 上报的小端设备时间戳追加到该密钥，并把明文填充到 512 字节，因此令牌长度翻倍。
/// 项目密钥在每个提供方实例内只生成一次，因为设备会把上报的 <c>pk</c> 与后续每个命令令牌配对校验。</para>
/// </remarks>
public sealed class EdlOnePlusProjectAuthProvider : IEdlProjectAuthenticationProvider, IEdlOnePlusCommandTokenProvider
{
    private const string KeyAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private static readonly byte[] ProjectKeyPrefix = [0x10, 0x45, 0x63, 0x87, 0xE3, 0x7E, 0x23, 0x71];
    private static readonly byte[] ProjectKeySuffix = [0xA2, 0xD4, 0xA0, 0x74, 0x0F, 0xD3, 0x28, 0x96];
    private static readonly byte[] ProjectInitializationVector =
        [0x9D, 0x61, 0x4A, 0x1E, 0xAC, 0x81, 0xC9, 0xB2, 0xD3, 0x76, 0xD7, 0x49, 0x31, 0x03, 0x63, 0x79];
    private static readonly byte[] SoftwareKeyPrefix = [0x46, 0xA5, 0x97, 0x30, 0xBB, 0x0D, 0x41, 0xE8];
    private static readonly byte[] SoftwareInitializationVector =
        [0xDC, 0x91, 0x0D, 0x88, 0xE3, 0xC6, 0xEE, 0x65, 0xF0, 0xC7, 0x44, 0xB4, 0x02, 0x30, 0xCE, 0x40];
    private const string ProjectSecretPrefix = "c4b95538c57df231";
    private const string ProjectSecretSuffix = "5b0217457e49381b";
    private const string SoftwareSecretSuffix = "8f7359c8a2951e8c";

    private readonly Lock _gate = new();
    private string? _projectKey;

    /// <summary>Creates a provider that generates one fresh project key per instance.
    /// <para>创建每个实例只新生成一个项目密钥的提供方。</para></summary>
    /// <param name="profile">The resolved project constants. <para>已解析的项目常量。</para></param>
    /// <exception cref="ArgumentException"><paramref name="profile"/> is incomplete or inconsistent.
    /// <para><paramref name="profile"/> 不完整或自相矛盾。</para></exception>
    public EdlOnePlusProjectAuthProvider(EdlOnePlusProjectProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        Profile = profile;
    }

    /// <summary>Creates a provider that reuses one explicit project key.
    /// <para>创建固定复用某一项目密钥的提供方。</para></summary>
    /// <param name="profile">The resolved project constants. <para>已解析的项目常量。</para></param>
    /// <param name="projectKey">Sixteen ASCII letters or digits, reused by every token of this instance.
    /// <para>十六个 ASCII 字母或数字，本实例的每个令牌都复用该值。</para></param>
    /// <exception cref="ArgumentException"><paramref name="profile"/> is incomplete, or
    /// <paramref name="projectKey"/> is not exactly sixteen ASCII letters or digits.
    /// <para><paramref name="profile"/> 不完整，或 <paramref name="projectKey"/> 不是恰好十六个 ASCII 字母或数字。
    /// </para></exception>
    public EdlOnePlusProjectAuthProvider(EdlOnePlusProjectProfile profile, string projectKey)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(projectKey);
        if (projectKey.Length != 16 || !projectKey.All(char.IsAsciiLetterOrDigit))
        {
            throw new ArgumentException("The fixed project key must be sixteen ASCII letters or digits.",
                nameof(projectKey));
        }

        Profile = profile;
        _projectKey = projectKey;
    }

    /// <summary>Gets the project constants used for every exchange.
    /// <para>获取每次使用的项目常量。</para></summary>
    /// <value>The validated profile. <para>已校验的配置。</para></value>
    public EdlOnePlusProjectProfile Profile { get; }

    /// <summary>
    /// Produces the <c>pk</c> and <c>token</c> pair for one project verification exchange.
    /// <para>为一次项目校验交换生成 <c>pk</c> 与 <c>token</c> 组合。</para>
    /// </summary>
    /// <param name="kind">The selected verification command family.
    /// <para>选定的校验命令家族。</para></param>
    /// <param name="projectId">The project name the session is authenticating.
    /// <para>会话正在校验的项目名。</para></param>
    /// <param name="serialNumber">The SoC serial reported by Sahara.
    /// <para>Sahara 上报的 SoC 序列号。</para></param>
    /// <param name="deviceTimestamp">Required by the software-project family, ignored otherwise.
    /// <para>软件项目家族必需，其他家族忽略。</para></param>
    /// <param name="cancellationToken">Cooperative cancellation token.
    /// <para>协作取消令牌。</para></param>
    /// <returns>ASCII bytes for <c>pk</c> and uppercase hexadecimal <c>token</c>.
    /// <para><c>pk</c> 的 ASCII 字节与大写十六进制的 <c>token</c>。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">The kind does not match the profile family, the project name
    /// differs from the profile, or the serial is zero.
    /// <para>家族与配置不符、项目名与配置不一致，或序列号为 0。</para></exception>
    /// <exception cref="ArgumentException">The plaintext exceeds the token payload capacity.
    /// <para>明文超出令牌载荷容量。</para></exception>
    public Task<IReadOnlyDictionary<string, byte[]>> CreateCredentialsAsync(EdlProjectAuthenticationKind kind,
        string projectId, ulong serialNumber, long? deviceTimestamp, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRequest(kind, projectId, serialNumber, deviceTimestamp);
        var key = ResolveProjectKey();
        var payload = string.Join(',', BuildFields(serialNumber));
        return Task.FromResult<IReadOnlyDictionary<string, byte[]>>(Credentials(key, payload, deviceTimestamp));
    }

    /// <summary>
    /// Produces the credentials for one <c>program</c> or <c>patch</c> command after project verification.
    /// <para>为项目校验之后的单个 <c>program</c> 或 <c>patch</c> 命令生成凭据。</para>
    /// </summary>
    /// <param name="kind">The authenticated verification command family.
    /// <para>已完成认证的项目家族。</para></param>
    /// <param name="projectId">The authenticated project name. <para>已认证的项目名。</para></param>
    /// <param name="serialNumber">The authenticated device serial. <para>已认证的设备序列号。</para></param>
    /// <param name="deviceTimestamp">The timestamp used by the authentication exchange, required for the
    /// software-project family. <para>认证交换使用的时间戳，软件项目家族必需。</para></param>
    /// <param name="command">Exactly <c>program</c> or <c>patch</c>.
    /// <para>必须为 <c>program</c> 或 <c>patch</c>。</para></param>
    /// <param name="cancellationToken">Cooperative cancellation token.
    /// <para>协作取消令牌。</para></param>
    /// <returns>The same <c>pk</c> as the exchange plus the command <c>token</c>.
    /// <para>与交换相同的 <c>pk</c> 与命令 <c>token</c>。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="command"/> is not a supported command name.
    /// <para><paramref name="command"/> 不是受支持的命令名。</para></exception>
    public Task<IReadOnlyDictionary<string, byte[]>> CreateCommandTokenAsync(EdlProjectAuthenticationKind kind,
        string projectId, ulong serialNumber, long? deviceTimestamp, string command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(command, "program", StringComparison.Ordinal) &&
            !string.Equals(command, "patch", StringComparison.Ordinal))
        {
            throw new ArgumentException("Only program and patch commands carry project tokens.", nameof(command));
        }

        ValidateRequest(kind, projectId, serialNumber, deviceTimestamp);
        var key = ResolveProjectKey();
        var payload = Profile.ReferenceTimestamp.ToString(CultureInfo.InvariantCulture) + ',' + ComputeHashes(serialNumber).Secret;
        return Task.FromResult<IReadOnlyDictionary<string, byte[]>>(Credentials(key, payload, deviceTimestamp));
    }

    private Dictionary<string, byte[]> Credentials(string projectKey, string payload, long? deviceTimestamp)
    {
        var bytes = Encoding.ASCII.GetBytes(payload);
        return new Dictionary<string, byte[]>
        {
            ["pk"] = Encoding.ASCII.GetBytes(projectKey),
            ["token"] = Encoding.ASCII.GetBytes(EncryptToken(bytes, projectKey, deviceTimestamp)),
        };
    }

    private void ValidateRequest(EdlProjectAuthenticationKind kind, string projectId, ulong serialNumber,
        long? deviceTimestamp)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        var expected = Profile.Family == EdlOnePlusProjectFamily.SoftwareProjectModel
            ? EdlProjectAuthenticationKind.OnePlusSoftwareProject
            : EdlProjectAuthenticationKind.OnePlusProject;
        if (kind != expected)
        {
            throw new ArgumentOutOfRangeException(nameof(kind),
                "The selected verification family does not match the profile.");
        }

        if (!string.Equals(projectId, Profile.ProjectId, StringComparison.Ordinal))
        {
            throw new ArgumentOutOfRangeException(nameof(projectId),
                "The session project name differs from the profile the provider was built from.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero((long)serialNumber);
        if (Profile.Family == EdlOnePlusProjectFamily.SoftwareProjectModel)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(deviceTimestamp ?? 0);
        }
    }

    private string ResolveProjectKey()
    {
        lock (_gate)
        {
            return _projectKey ??= GenerateProjectKey();
        }
    }

    private static string GenerateProjectKey()
    {
        var characters = new char[16];
        for (var index = 0; index < characters.Length; index++)
        {
            characters[index] = KeyAlphabet[RandomNumberGenerator.GetInt32(KeyAlphabet.Length)];
        }

        return new string(characters);
    }

    private (string ModelHash, string Secret) ComputeHashes(ulong serialNumber)
    {
        var serial = serialNumber.ToString(CultureInfo.InvariantCulture);
        var timestamp = Profile.ReferenceTimestamp.ToString(CultureInfo.InvariantCulture);
        var modelHash = UpperSha256(Profile.ProductionKey + Profile.ProjectId + Profile.RandomPostfix);
        var secretInput = Profile.Family == EdlOnePlusProjectFamily.SoftwareProjectModel
            ? Profile.ProductionKey + Profile.ProjectId + serial + Profile.VersionString + timestamp
                + modelHash + SoftwareSecretSuffix
            : ProjectSecretPrefix + Profile.ProjectId
                + Profile.CustomerFlag.ToString(CultureInfo.InvariantCulture) + serial
                + Profile.VersionString + timestamp + modelHash + ProjectSecretSuffix;
        return (modelHash, UpperSha256(secretInput));
    }

    private static string UpperSha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(value)));

    private string[] BuildFields(ulong serialNumber)
    {
        var (modelHash, secret) = ComputeHashes(serialNumber);
        var serial = serialNumber.ToString(CultureInfo.InvariantCulture);
        var timestamp = Profile.ReferenceTimestamp.ToString(CultureInfo.InvariantCulture);
        return Profile.Family == EdlOnePlusProjectFamily.SoftwareProjectModel
            ?
            [
                Profile.ProjectId,
                Profile.RandomPostfix,
                modelHash,
                Profile.AtoBuildState.ToString(CultureInfo.InvariantCulture),
                Profile.FlashMode.ToString(CultureInfo.InvariantCulture),
                Profile.VersionString,
                serial,
                Convert.ToUInt32(Profile.ProjectId, 16).ToString(CultureInfo.InvariantCulture),
                timestamp,
                secret,
            ]
            :
            [
                Profile.ProjectId,
                Profile.RandomPostfix,
                modelHash,
                Profile.VersionString,
                Profile.CustomerFlag.ToString(CultureInfo.InvariantCulture),
                serial,
                timestamp,
                secret,
            ];
    }

    private string EncryptToken(byte[] payload, string projectKey, long? deviceTimestamp)
    {
        var software = Profile.Family == EdlOnePlusProjectFamily.SoftwareProjectModel;
        var capacity = software ? 512 : 256;
        if (payload.Length > capacity)
        {
            throw new ArgumentException(
                "The profile fields exceed the verification token payload capacity.");
        }

        var block = new byte[capacity];
        payload.CopyTo(block, 0);
        using var aes = Aes.Create();
        aes.Key = BuildAesKey(projectKey, deviceTimestamp);
        aes.IV = software ? SoftwareInitializationVector : ProjectInitializationVector;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor();
        return Convert.ToHexString(encryptor.TransformFinalBlock(block, 0, block.Length));
    }

    private byte[] BuildAesKey(string projectKey, long? deviceTimestamp)
    {
        if (Profile.Family != EdlOnePlusProjectFamily.SoftwareProjectModel)
        {
            return [.. ProjectKeyPrefix, .. Encoding.ASCII.GetBytes(projectKey), .. ProjectKeySuffix];
        }

        var timestampBytes = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(timestampBytes,
            deviceTimestamp ?? throw new ArgumentOutOfRangeException(nameof(deviceTimestamp),
                "The software-project family requires the timestamp reported by setprocstart."));
        return [.. SoftwareKeyPrefix, .. Encoding.ASCII.GetBytes(projectKey), .. timestampBytes];
    }
}