namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>Identifies an explicitly selected vendor project verification exchange.
/// <para>标识显式选定的厂商项目校验交换。</para></summary>
public enum EdlProjectAuthenticationKind
{
    /// <summary>Uses the OnePlus demacia command and verify_res result.
    /// <para>使用 OnePlus demacia 命令及 verify_res 结果。</para></summary>
    OnePlusDemacia,
    /// <summary>Uses the OnePlus setprojmodel command.
    /// <para>使用 OnePlus setprojmodel 命令。</para></summary>
    OnePlusProject,
    /// <summary>Obtains a device timestamp before the OnePlus setswprojmodel command.
    /// <para>在 OnePlus setswprojmodel 命令前取得设备时间戳。</para></summary>
    OnePlusSoftwareProject,
    /// <summary>Checks Nothing features before the ntprojectverify command.
    /// <para>在 ntprojectverify 命令前检查 Nothing 功能。</para></summary>
    NothingProject
}

/// <summary>Supplies authorized project credentials without built-in token generation or project guessing.
/// <para>提供授权的项目凭据，不内置令牌生成或项目猜测。</para></summary>
public interface IEdlProjectAuthenticationProvider
{
    /// <summary>Creates exactly one response for the selected device, project and protocol.
    /// Implementations must not reenter the session.
    /// <para>为所选设备、项目和协议生成一次响应，实现不得重入会话。</para></summary>
    /// <param name="kind">The selected verification command family.
    /// <para>选定的校验命令家族。</para></param>
    /// <param name="projectId">The caller-selected project identifier.
    /// <para>调用方选定的项目标识符。</para></param>
    /// <param name="serialNumber">The selected device serial, not cryptographic proof.
    /// <para>选定的设备序列号，并非密码学证明。</para></param>
    /// <param name="deviceTimestamp">The positive device timestamp for software verification, otherwise null.
    /// <para>软件校验使用的正数设备时间戳，其他情况为 null。</para></param>
    /// <param name="cancellationToken">Cooperative cancellation including the exchange deadline.
    /// <para>包含交换截止时间的协作式取消令牌。</para></param>
    /// <returns>Newly allocated ASCII credential buffers keyed by pk/token for OnePlus or token1/token2/token3
    /// for Nothing. Ownership of all returned buffers transfers to the session, which clears them on exit.
    /// The dictionary and buffers must not be mutated after return.
    /// <para>新分配的 ASCII 凭据缓冲区，OnePlus 使用 pk/token 键，Nothing 使用 token1/token2/token3 键。
    /// 所有缓冲区的所有权转移给会话并在退出时清除，返回后不得修改字典或缓冲区。</para></returns>
    Task<IReadOnlyDictionary<string, byte[]>> CreateCredentialsAsync(EdlProjectAuthenticationKind kind,
        string projectId, ulong serialNumber, long? deviceTimestamp, CancellationToken cancellationToken);
}

/// <summary>Supplies a fresh authorized OnePlus token for each program or patch command.
/// <para>为每个 program 或 patch 命令提供新的授权 OnePlus 令牌。</para></summary>
public interface IEdlOnePlusCommandTokenProvider
{
    /// <summary>Creates command credentials bound to the completed project authentication.
    /// Implementations must not reenter the session.
    /// <para>生成绑定已完成项目认证的命令凭据，实现不得重入会话。</para></summary>
    /// <param name="kind">The authenticated OnePlus project protocol.
    /// <para>已认证的 OnePlus 项目协议。</para></param>
    /// <param name="projectId">The authenticated project identifier.
    /// <para>已认证的项目标识符。</para></param>
    /// <param name="serialNumber">The selected device serial.
    /// <para>选定的设备序列号。</para></param>
    /// <param name="deviceTimestamp">The timestamp obtained during software authentication, otherwise null.
    /// <para>软件认证期间取得的时间戳，其他情况为 null。</para></param>
    /// <param name="command">Exactly program or patch.
    /// <para>精确为 program 或 patch。</para></param>
    /// <param name="cancellationToken">Cooperative cancellation including the token acquisition deadline.
    /// <para>包含令牌取得截止时间的协作式取消令牌。</para></param>
    /// <returns>New ASCII buffers keyed by pk and token. Ownership transfers to the session for clearing.
    /// <para>以 pk 和 token 为键的新 ASCII 缓冲区，所有权转移给会话并由会话清除。</para></returns>
    Task<IReadOnlyDictionary<string, byte[]>> CreateCommandTokenAsync(EdlProjectAuthenticationKind kind,
        string projectId, ulong serialNumber, long? deviceTimestamp, string command, CancellationToken cancellationToken);
}

/// <summary>Defines one explicit project authentication profile without fallback candidates.
/// <para>定义一个显式项目认证配置，不包含回退候选项。</para></summary>
public sealed record EdlProjectAuthenticationOptions
{
    /// <summary>Gets the selected protocol variant.
    /// <para>获取选定的协议变体。</para></summary>
    /// <value>The command family to use once.
    /// <para>单次使用的命令家族。</para></value>
    public required EdlProjectAuthenticationKind Kind { get; init; }

    /// <summary>Gets the caller-selected project identifier.
    /// <para>获取调用方选定的项目标识符。</para></summary>
    /// <value>A nonblank identifier of at most 64 ASCII letters or digits.
    /// <para>最多 64 个 ASCII 字母或数字组成的非空标识符。</para></value>
    public required string ProjectId { get; init; }

    /// <summary>Gets the explicitly selected device serial.
    /// <para>获取显式选定的设备序列号。</para></summary>
    /// <value>A nonzero serial, required to match a Sahara identity when one is available.
    /// <para>非零序列号，在存在 Sahara 身份时必须与其匹配。</para></value>
    public required ulong SerialNumber { get; init; }

    /// <summary>Gets the authorized credential provider.
    /// <para>获取授权的凭据提供方。</para></summary>
    /// <value>The provider invoked once per exchange.
    /// <para>每次交换调用一次的提供方。</para></value>
    public required IEdlProjectAuthenticationProvider Provider { get; init; }

    /// <summary>Gets the per-command provider for OnePlus project or software-project authentication.
    /// <para>获取 OnePlus 项目或软件项目认证使用的逐命令提供方。</para></summary>
    /// <value>The provider, or null to reject subsequent program and patch commands for those profiles.
    /// <para>提供方；为 null 时拒绝这些配置下的后续 program 和 patch 命令。</para></value>
    public IEdlOnePlusCommandTokenProvider? CommandTokenProvider { get; init; }

    /// <summary>Gets the cooperative deadline for acquiring each command token.
    /// <para>获取取得每个命令令牌的协作式截止时间。</para></summary>
    /// <value>The budget in milliseconds, from 1 through 86400000.
    /// <para>以毫秒计的预算，范围为 1 至 86400000。</para></value>
    public int CommandTokenTimeoutMs { get; init; } = 60000;

    internal void Validate()
    {
        if (!Enum.IsDefined(Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(Kind));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(ProjectId);
        if (ProjectId.Length > 64 || !ProjectId.All(char.IsAsciiLetterOrDigit))
        {
            throw new ArgumentException("The selected project identifier is invalid.", nameof(ProjectId));
        }
        ArgumentOutOfRangeException.ThrowIfZero(SerialNumber);
        ArgumentNullException.ThrowIfNull(Provider);
        FirehoseProtocol.ValidateAuthenticationTimeout(CommandTokenTimeoutMs);
        if (CommandTokenProvider is not null && Kind is not (EdlProjectAuthenticationKind.OnePlusProject
            or EdlProjectAuthenticationKind.OnePlusSoftwareProject))
        {
            throw new ArgumentException("Per-command tokens require a OnePlus project authentication profile.");
        }
    }
}