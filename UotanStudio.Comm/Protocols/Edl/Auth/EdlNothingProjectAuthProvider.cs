using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace UotanStudio.Comm.Protocols.Edl.Auth;

/// <summary>
/// Carries the per-project constants that a Nothing <c>ntprojectverify</c> token is built from.
/// <para>保存构造 Nothing <c>ntprojectverify</c> 令牌所需的逐项目常量。</para>
/// </summary>
/// <remarks>
/// <para>Each project identifier keeps one verification digest instead of deriving
/// it from the target, so a profile is only as correct as the catalog entry or operator input it came from.
/// The digest is the constant the device re-computes with, not a per-device secret.</para>
/// <para>每个项目标识符保存一个校验摘要，而不是从目标推导，因此配置的正确性完全取决于它来自
/// 目录条目还是操作员输入。该摘要是设备重新计算时使用的常量，并非设备专属机密。</para>
/// </remarks>
public sealed record EdlNothingProjectProfile
{
    /// <summary>Gets the project identifier written into the token digest input.
    /// <para>获取写入令牌摘要输入的项目标识符。</para></summary>
    /// <value>At most 64 ASCII letters or digits, such as <c>22111</c> for Nothing Phone (2).
    /// <para>最多 64 个 ASCII 字母或数字，例如 Nothing Phone (2) 的 <c>22111</c>。</para></value>
    public required string ProjectId { get; init; }

    /// <summary>Gets the project verification digest echoed as <c>token3</c> and mixed into <c>token2</c>.
    /// <para>获取作为 <c>token3</c> 回送并混入 <c>token2</c> 的项目校验摘要。</para></summary>
    /// <value>Exactly 64 lowercase ASCII hexadecimal characters.
    /// <para>恰好 64 个小写 ASCII 十六进制字符。</para></value>
    public required string HashVerify { get; init; }

    /// <summary>Gets the short device description that this profile was resolved from.
    /// <para>获取解析该配置时所依据的简短设备描述。</para></summary>
    /// <value>The catalog description, or an empty string for a hand-built profile.
    /// <para>目录描述；手工构造的配置为空字符串。</para></value>
    public string Description { get; init; } = string.Empty;

    /// <summary>Validates the field shapes.
    /// <para>校验字段格式。</para></summary>
    /// <exception cref="ArgumentException">A field has the wrong length or character class.
    /// <para>某个字段的长度或字符类别不正确。</para></exception>
    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ProjectId);
        if (ProjectId.Length > 64 || !ProjectId.All(char.IsAsciiLetterOrDigit))
        {
            throw new ArgumentException("The profile project name is not at most 64 ASCII letters or digits.");
        }

        if (HashVerify.Length != 64 || !HashVerify.All(char.IsAsciiHexDigitLower))
        {
            throw new ArgumentException("The profile verification digest is not 64 lowercase hexadecimal characters.");
        }
    }
}

/// <summary>
/// Resolves Nothing verification constants for a project identifier from the built-in fixed table.
/// <para>按项目标识符从内置固定表解析 Nothing 校验常量。</para>
/// </summary>
/// <remarks>
/// <para>Identifiers absent from the table have no derivable profile here, because
/// their digests are not guessed.
/// <see cref="EdlNothingProjectProfile.HashVerify"/> may alternatively be supplied by the operator when the
/// digest has been authorized for a project outside this table.</para>
/// <para>表内没有的标识符在此没有可推导的配置，因为其摘要不被猜测。当摘要已针对表外项目获得授权时，
/// 也可以由操作员直接提供 <see cref="EdlNothingProjectProfile.HashVerify"/>。</para>
/// </remarks>
public static class EdlNothingProjectCatalog
{
    private static readonly (string Project, string Description, string HashVerify)[] Entries =
    [
        ("22111", "Nothing Phone (2)",
            "16386b4035411a770b12507b2e30297c0c5471230b213e6a1e1e701c6a425150"),
    ];

    /// <summary>Gets the project identifiers that the built-in table can resolve.
    /// <para>获取内置表可解析的项目标识符。</para></summary>
    /// <value>The identifiers in sorted order. <para>按排序后的标识符。</para></value>
    public static IReadOnlyList<string> ProjectIds { get; } =
        [.. Entries.Select(entry => entry.Project).Order(StringComparer.Ordinal)];

    /// <summary>Resolves one profile from the built-in table without probing the device.
    /// <para>按内置表解析一个配置，不探测设备。</para></summary>
    /// <param name="projectId">The project identifier reported by the device.
    /// <para>设备上报的项目标识符。</para></param>
    /// <param name="profile">The resolved profile, or <see langword="null"/> when unknown.
    /// <para>解析得到的配置；未知时为 <see langword="null"/>。</para></param>
    /// <returns><see langword="true"/> when the identifier is in the table.
    /// <para>标识符位于表内时返回 <see langword="true"/>。</para></returns>
    public static bool TryResolve(string projectId, out EdlNothingProjectProfile? profile)
    {
        var entry = Array.Find(Entries, candidate =>
            string.Equals(candidate.Project, projectId, StringComparison.Ordinal));
        if (entry.Project is null)
        {
            profile = null;
            return false;
        }

        profile = new EdlNothingProjectProfile
        {
            ProjectId = entry.Project,
            HashVerify = entry.HashVerify,
            Description = entry.Description,
        };
        return true;
    }

    /// <summary>Describes one catalogued project for user selection.
    /// <para>为界面选择描述一个目录内的项目。</para></summary>
    /// <param name="projectId">The project identifier to describe. <para>要描述的项目标识符。</para></param>
    /// <returns>The catalogued description, or an empty string when the identifier is unknown.
    /// <para>目录描述；标识符未知时为空字符串。</para></returns>
    public static string Describe(string projectId)
    {
        var entry = Array.Find(Entries, candidate =>
            string.Equals(candidate.Project, projectId, StringComparison.Ordinal));
        return entry.Description ?? string.Empty;
    }
}

/// <summary>
/// Builds Nothing <c>ntprojectverify</c> credentials from one explicit profile.
/// <para>由单个显式配置构造 Nothing <c>ntprojectverify</c> 凭据。</para>
/// </summary>
/// <remarks>
/// <para>The three token attributes are derived as
/// <c>token2 = lowercase hex of SHA-256 over the ASCII bytes of token1 + project id + serial as lowercase
/// hexadecimal + token3</c> and <c>token3 = the project verification digest</c>, where the serial is formatted
/// as unprefixed lowercase hexadecimal without padding. The token1
/// nonce carries no per-device secret; freshly generated exchanges emit 32 lowercase hexadecimal characters,
/// which is the exact width the session credential gate accepts for <c>token1</c>
/// (FirehoseProtocol.ProjectAuthentication.cs, lines 129 to 151), while the fixed-token1 constructor keeps any
/// caller-supplied width so known test vectors can be reproduced byte for byte.</para>
/// <para>三个令牌属性的推导方式：<c>token2 = 对 ASCII 字节 token1 + 项目标识 + 小写十六进制序列号
/// + token3 的 SHA-256 小写十六进制摘要</c>，<c>token3 = 项目校验摘要</c>；序列号按无前导零也无前缀的
/// 小写十六进制书写。token1 随机数不含设备专属机密：新生成的令牌使用 32 个小写十六进制
/// 字符，正好等于会话凭据门对 <c>token1</c> 接受的宽度（FirehoseProtocol.ProjectAuthentication.cs 第 129 至 151 行）；
/// 固定 token1 构造函数则保留调用方给定的宽度，以便逐字节复现已知测试向量。</para>
/// </remarks>
public sealed class EdlNothingProjectAuthProvider : IEdlProjectAuthenticationProvider
{
    private const int ReferenceToken1Length = 32;
    private const int MaxFixedToken1Length = 128;

    /// <summary>Creates a provider that generates one fresh nonce per verification exchange.
    /// <para>创建每次校验交换都新生成随机数的提供方。</para></summary>
    /// <param name="profile">The resolved project constants. <para>已解析的项目常量。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is <see langword="null"/>.
    /// <para><paramref name="profile"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="profile"/> is incomplete or inconsistent.
    /// <para><paramref name="profile"/> 不完整或自相矛盾。</para></exception>
    public EdlNothingProjectAuthProvider(EdlNothingProjectProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        Profile = profile;
    }

    /// <summary>Creates a provider that reuses one explicit nonce, for reproducible exchanges and known
    /// test vectors.
    /// <para>创建固定复用某一随机数的提供方，用于可复现的交换与已知测试向量。</para></summary>
    /// <param name="profile">The resolved project constants. <para>已解析的项目常量。</para></param>
    /// <param name="fixedToken1">Between one and 128 lowercase ASCII hexadecimal characters, reused by every
    /// token of this instance. <para>1 至 128 个小写 ASCII 十六进制字符，本实例的每个令牌都复用该值。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is <see langword="null"/>.
    /// <para><paramref name="profile"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="profile"/> is incomplete, or
    /// <paramref name="fixedToken1"/> is not lowercase ASCII hexadecimal within the bound.
    /// <para><paramref name="profile"/> 不完整，或 <paramref name="fixedToken1"/> 超出界限或不是小写 ASCII
    /// 十六进制字符。</para></exception>
    public EdlNothingProjectAuthProvider(EdlNothingProjectProfile profile, string fixedToken1)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(fixedToken1);
        if (fixedToken1.Length is < 1 or > MaxFixedToken1Length ||
            (fixedToken1.Length % 2) != 0 || !fixedToken1.All(char.IsAsciiHexDigitLower))
        {
            throw new ArgumentException(
                "The fixed nonce must be one to 128 lowercase ASCII hexadecimal characters with even length.",
                nameof(fixedToken1));
        }

        Profile = profile;
        FixedToken1 = fixedToken1;
    }

    /// <summary>Gets the project constants used for every exchange.
    /// <para>获取每次使用的项目常量。</para></summary>
    /// <value>The validated profile. <para>已校验的配置。</para></value>
    public EdlNothingProjectProfile Profile { get; }

    /// <summary>Gets the explicit nonce, or <see langword="null"/> when a fresh nonce is generated per exchange.
    /// <para>获取显式随机数；每次交换新生成随机数时为 <see langword="null"/>。</para></summary>
    /// <value>The nonce string, or <see langword="null"/>. <para>随机数字符串，或 <see langword="null"/>。</para></value>
    public string? FixedToken1 { get; }

    /// <summary>
    /// Produces the <c>token1</c>, <c>token2</c> and <c>token3</c> triple for one project verification exchange.
    /// <para>为一次项目校验交换生成 <c>token1</c>、<c>token2</c> 与 <c>token3</c> 三元组。</para>
    /// </summary>
    /// <param name="kind">Must be <see cref="EdlProjectAuthenticationKind.NothingProject"/>.
    /// <para>必须为 <see cref="EdlProjectAuthenticationKind.NothingProject"/>。</para></param>
    /// <param name="projectId">The project name the session is authenticating.
    /// <para>会话正在校验的项目名。</para></param>
    /// <param name="serialNumber">The SoC serial reported by Sahara.
    /// <para>Sahara 上报的 SoC 序列号。</para></param>
    /// <param name="deviceTimestamp">Ignored; the Nothing exchange carries no timestamp.
    /// <para>忽略；Nothing 交换不携带时间戳。</para></param>
    /// <param name="cancellationToken">Cooperative cancellation token.
    /// <para>协作取消令牌。</para></param>
    /// <returns>Lowercase ASCII hexadecimal bytes for <c>token1</c>, <c>token2</c> and <c>token3</c>.
    /// <para>三者的小写 ASCII 十六进制字节。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="projectId"/> is <see langword="null"/>.
    /// <para><paramref name="projectId"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The kind is not the Nothing project family, the project
    /// name differs from the profile, or the serial is zero.
    /// <para>家族不是 Nothing 项目、项目名与配置不一致，或序列号为 0。</para></exception>
    public Task<IReadOnlyDictionary<string, byte[]>> CreateCredentialsAsync(EdlProjectAuthenticationKind kind,
        string projectId, ulong serialNumber, long? deviceTimestamp, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRequest(kind, projectId, serialNumber);
        var token1 = FixedToken1 ?? GenerateToken1();
        var token2 = Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(
            token1 + Profile.ProjectId + serialNumber.ToString("x", CultureInfo.InvariantCulture)
            + Profile.HashVerify)));
        return Task.FromResult<IReadOnlyDictionary<string, byte[]>>(
            new Dictionary<string, byte[]>
            {
                ["token1"] = Encoding.ASCII.GetBytes(token1),
                ["token2"] = Encoding.ASCII.GetBytes(token2),
                ["token3"] = Encoding.ASCII.GetBytes(Profile.HashVerify),
            });
    }

    private void ValidateRequest(EdlProjectAuthenticationKind kind, string projectId, ulong serialNumber)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        if (kind != EdlProjectAuthenticationKind.NothingProject)
        {
            throw new ArgumentOutOfRangeException(nameof(kind),
                "The selected verification family is not the Nothing project family.");
        }

        if (!string.Equals(projectId, Profile.ProjectId, StringComparison.Ordinal))
        {
            throw new ArgumentOutOfRangeException(nameof(projectId),
                "The session project name differs from the profile the provider was built from.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero((long)serialNumber);
    }

    private static string GenerateToken1()
    {
        return Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(ReferenceToken1Length / 2));
    }
}