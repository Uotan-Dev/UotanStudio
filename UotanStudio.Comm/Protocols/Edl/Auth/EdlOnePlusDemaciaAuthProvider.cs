using System.Security.Cryptography;
using System.Text;

namespace UotanStudio.Comm.Protocols.Edl.Auth;

/// <summary>
/// Builds OnePlus <c>demacia</c> credentials from the documented fixed AES key material and the device
/// serial, without embedding any per-device secret.
/// <para>由公开的固定 AES 常量与设备序列号构造 OnePlus <c>demacia</c> 凭据，不内置任何设备专属机密。</para>
/// </summary>
/// <remarks>
/// <para>The token is the uppercase hex encoding of one AES-256-CBC block over
/// <c>"907heavyworkload"</c> followed by the SHA-256 digest of
/// <c>"2e7006834dafe8ad" + serial + "a6674c6b039707ff"</c>, zero-padded to 256 bytes. The serial is written as
/// decimal digits and left-padded with zeros to ten characters.</para>
/// <para>令牌为一段 AES-256-CBC 密文的大写十六进制编码，明文是 <c>"907heavyworkload"</c> 加上
/// <c>"2e7006834dafe8ad" + 序列号 + "a6674c6b039707ff"</c> 的 SHA-256 摘要，并零填充到 256 字节。序列号按十进制
/// 书写并在左侧补零到十位。</para>
/// </remarks>
public sealed class EdlOnePlusDemaciaAuthProvider : IEdlProjectAuthenticationProvider
{
    private const string KeyAlphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private static readonly byte[] DemaciaKeyPrefix = [0x01, 0x63, 0xA0, 0xD1, 0xFD, 0xE2, 0x67, 0x11];
    private static readonly byte[] DemaciaKeySuffix = [0x48, 0x27, 0xC2, 0x08, 0xFB, 0xB0, 0xE6, 0xF0];
    private static readonly byte[] DemaciaInitializationVector =
        [0x96, 0xE0, 0x79, 0x0C, 0xAE, 0x2B, 0xB4, 0xAF, 0x68, 0x4C, 0x36, 0xCB, 0x0B, 0xEC, 0x49, 0xCE];
    private static readonly byte[] DemaciaMarker = "907heavyworkload"u8.ToArray();
    private const string SerialPrefix = "2e7006834dafe8ad";
    private const string SerialSuffix = "a6674c6b039707ff";

    /// <summary>Creates a provider that generates a fresh project key for every exchange.
    /// <para>创建每次交换都新生成项目密钥的提供方。</para></summary>
    public EdlOnePlusDemaciaAuthProvider()
    {
    }

    /// <summary>
    /// Creates a provider that reuses one explicit project key, for reproducible exchanges and diagnostics.
    /// <para>创建固定复用某一显式项目密钥的提供方，用于可复现的交换与诊断。</para>
    /// </summary>
    /// <param name="projectKey">
    /// Sixteen ASCII letters or digits used as the middle of the AES key.
    /// <para>作为 AES 密钥中段的十六个 ASCII 字母或数字。</para>
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="projectKey"/> is not exactly sixteen ASCII letters or digits.
    /// <para><paramref name="projectKey"/> 不是恰好十六个 ASCII 字母或数字。</para>
    /// </exception>
    public EdlOnePlusDemaciaAuthProvider(string projectKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectKey);
        if (projectKey.Length != 16 || !projectKey.All(char.IsAsciiLetterOrDigit))
        {
            throw new ArgumentException("The fixed project key must be sixteen ASCII letters or digits.", nameof(projectKey));
        }

        FixedProjectKey = projectKey;
    }

    /// <summary>Gets the explicit project key, or <see langword="null"/> when a fresh key is generated per exchange.
    /// <para>获取显式项目密钥；每次交换新生成密钥时为 <see langword="null"/>。</para></summary>
    /// <value>The sixteen-character key, or <see langword="null"/>.
    /// <para>十六字符密钥，或 <see langword="null"/>。</para></value>
    public string? FixedProjectKey { get; }

    /// <summary>
    /// Produces the <c>pk</c> and <c>token</c> pair for one demacia exchange.
    /// <para>为一次 demacia 交换生成 <c>pk</c> 与 <c>token</c> 组合。</para>
    /// </summary>
    /// <param name="kind">Must be <see cref="EdlProjectAuthenticationKind.OnePlusDemacia"/>.
    /// <para>必须为 <see cref="EdlProjectAuthenticationKind.OnePlusDemacia"/>。</para></param>
    /// <param name="projectId">Ignored by demacia; the command carries only the key and token.
    /// <para>demacia 不使用该值；命令只携带密钥与令牌。</para></param>
    /// <param name="serialNumber">The SoC serial reported by Sahara.
    /// <para>Sahara 上报的 SoC 序列号。</para></param>
    /// <param name="deviceTimestamp">Ignored; only the software-project family needs a timestamp.
    /// <para>忽略；仅软件项目家族需要时间戳。</para></param>
    /// <param name="cancellationToken">Cooperative cancellation token.
    /// <para>协作取消令牌。</para></param>
    /// <returns>
    /// ASCII bytes for <c>pk</c> (sixteen letters or digits) and <c>token</c> (512 uppercase hex characters).
    /// <para><c>pk</c>（十六个字母或数字）与 <c>token</c>（512 个大写十六进制字符）的 ASCII 字节。</para>
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="kind"/> selects a family other than demacia.
    /// <para><paramref name="kind"/> 选择了 demacia 之外的家族。</para>
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="serialNumber"/> is zero, which no reported device serial can be.
    /// <para><paramref name="serialNumber"/> 为 0，任何设备上报序列号都不可能为 0。</para>
    /// </exception>
    public Task<IReadOnlyDictionary<string, byte[]>> CreateCredentialsAsync(EdlProjectAuthenticationKind kind,
        string projectId, ulong serialNumber, long? deviceTimestamp, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        cancellationToken.ThrowIfCancellationRequested();
        if (kind != EdlProjectAuthenticationKind.OnePlusDemacia)
        {
            throw new ArgumentOutOfRangeException(nameof(kind),
                "This provider implements demacia only; the project and software-project families need their own credential derivation.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero((long)serialNumber);

        var key = FixedProjectKey ?? GenerateProjectKey();
        var token = ComputeToken(key, serialNumber);
        return Task.FromResult<IReadOnlyDictionary<string, byte[]>>(new Dictionary<string, byte[]>
        {
            ["pk"] = Encoding.ASCII.GetBytes(key),
            ["token"] = Encoding.ASCII.GetBytes(token)
        });
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

    private static string ComputeToken(string key, ulong serialNumber)
    {
        var serial = serialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (serial.Length < 10)
        {
            serial = serial.PadLeft(10, '0');
        }

        var digest = SHA256.HashData(Encoding.ASCII.GetBytes(SerialPrefix + serial + SerialSuffix));
        var block = new byte[256];
        DemaciaMarker.AsSpan().CopyTo(block);
        digest.AsSpan().CopyTo(block.AsSpan(DemaciaMarker.Length));
        using var aes = Aes.Create();
        aes.Key = [.. DemaciaKeyPrefix, .. Encoding.ASCII.GetBytes(key), .. DemaciaKeySuffix];
        aes.IV = DemaciaInitializationVector;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor();
        return Convert.ToHexString(encryptor.TransformFinalBlock(block, 0, block.Length));
    }
}