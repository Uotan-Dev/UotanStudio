using System.Security.Cryptography;

namespace UotanStudio.Comm.Protocols.Adb.Pairing;

/// <summary>
/// Generates the one-time credentials of a wireless-debugging QR pairing code and
/// builds the matching <c>WIFI:T:ADB</c> payload text. Each pairing session needs
/// fresh values.
/// <para>生成无线调试二维码配对的一次性凭据，并构建与之匹配的 <c>WIFI:T:ADB</c>
/// 负载文本。每次配对会话都需要新的值。</para>
/// </summary>
public static class AdbQrPairing
{
    /// <summary>
    /// Length of the first random segment of the mDNS instance name, whose layout is
    /// <c>ADB_WIFI_xxxxxxxxxxxxxx-yyyyyy</c>.
    /// <para>mDNS 实例名第一段随机串的长度，实例名布局为
    /// <c>ADB_WIFI_xxxxxxxxxxxxxx-yyyyyy</c>。</para>
    /// </summary>
    public const int NamePrimaryLength = 14;

    /// <summary>
    /// Length of the second random segment of the mDNS instance name.
    /// <para>mDNS 实例名第二段随机串的长度。</para>
    /// </summary>
    public const int NameSecondaryLength = 6;

    /// <summary>
    /// Length of the random pairing password.
    /// <para>随机配对密码的长度。</para>
    /// </summary>
    public const int PasswordLength = 21;

    private const string NamePrefix = "ADB_WIFI_";
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>
    /// Generates fresh random pairing credentials. The instance name follows the
    /// <c>ADB_WIFI_&lt;14&gt;-&lt;6&gt;</c> layout and the password is 21 alphanumeric
    /// characters; both use a cryptographic random source.
    /// <para>生成全新的随机配对凭据。实例名遵循
    /// <c>ADB_WIFI_&lt;14&gt;-&lt;6&gt;</c> 布局，密码为 21 位字母数字；
    /// 两者均使用密码学随机源。</para>
    /// </summary>
    /// <returns>The generated credentials. <para>生成的凭据。</para></returns>
    public static AdbQrPairingCredentials GenerateCredentials()
    {
        var name = $"{NamePrefix}{RandomString(NamePrimaryLength)}-{RandomString(NameSecondaryLength)}";
        return new AdbQrPairingCredentials(name, RandomString(PasswordLength));
    }

    /// <summary>
    /// Builds the QR payload text for the supplied credentials:
    /// <c>WIFI:T:ADB;S:{name};P:{password};;</c>.
    /// <para>为给定凭据构建二维码负载文本：
    /// <c>WIFI:T:ADB;S:{name};P:{password};;</c>。</para>
    /// </summary>
    /// <param name="credentials">The credentials to encode. <para>待编码的凭据。</para></param>
    /// <returns>The QR payload text. <para>二维码负载文本。</para></returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="credentials"/> is <c>null</c>.
    /// <para><paramref name="credentials"/> 为 <c>null</c>。</para>
    /// </exception>
    public static string BuildPayload(AdbQrPairingCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        return $"WIFI:T:ADB;S:{credentials.ServiceName};P:{credentials.Password};;";
    }

    /// <summary>
    /// Draws unbiased alphanumeric characters from a cryptographic random source.
    /// <para>从密码学随机源取得无偏的字母数字字符。</para>
    /// </summary>
    /// <param name="length">Number of characters to draw. <para>需要取得的字符数。</para></param>
    /// <returns>The drawn character string. <para>取得的字符串。</para></returns>
    private static string RandomString(int length)
    {
        const int MaxUnbiased = 248;
        var bytes = RandomNumberGenerator.GetBytes(length * 2);
        var chars = new char[length];
        var byteIndex = 0;
        for (var i = 0; i < length; i++)
        {
            byte sample;
            do
            {
                if (byteIndex >= bytes.Length)
                {
                    bytes = RandomNumberGenerator.GetBytes(length * 2);
                    byteIndex = 0;
                }
            }
            while ((sample = bytes[byteIndex++]) >= MaxUnbiased);
            chars[i] = Alphabet[sample % Alphabet.Length];
        }

        return new string(chars);
    }
}

/// <summary>
/// One-time wireless-debugging QR pairing credentials: the mDNS instance name the
/// phone advertises after scanning (<c>_adb-tls-pairing._tcp</c>) and the pairing
/// password shared through the QR code.
/// <para>无线调试二维码配对的一次性凭据：手机扫码后广播的 mDNS 实例名
/// （<c>_adb-tls-pairing._tcp</c>）与通过二维码共享的配对密码。</para>
/// </summary>
/// <param name="ServiceName">
/// The mDNS service instance name, in the shape <c>ADB_WIFI_a1B2c3D4e5F6g7H-xy9Zwq</c>.
/// <para>mDNS 服务实例名，形如 <c>ADB_WIFI_a1B2c3D4e5F6g7H-xy9Zwq</c>。</para>
/// </param>
/// <param name="Password">
/// The pairing password (SPAKE2 shared secret input).
/// <para>配对密码（SPAKE2 共享密钥输入）。</para>
/// </param>
public sealed record AdbQrPairingCredentials(string ServiceName, string Password);