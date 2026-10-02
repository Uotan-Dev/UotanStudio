using System.Security.Cryptography;

namespace UotanStudio.Comm.Protocols.Adb.Pairing;

/// <summary>
/// Generates the one-time credentials encoded in a wireless-debugging QR pairing
/// code: a random mDNS instance name and a random pairing password, plus the QR
/// payload text in the Wi-Fi provisioning format Android expects
/// (<c>WIFI:T:ADB;S:name;P:password;;</c>). New credentials must be generated for
/// every pairing session so each displayed QR code is unique.
/// <para>生成无线调试二维码配对的一次性凭据：随机的 mDNS 实例名与随机配对密码，
/// 以及 Android 期望的 Wi-Fi 供应格式二维码文本
/// （<c>WIFI:T:ADB;S:name;P:password;;</c>）。每次配会话都必须重新生成，
/// 以保证展示的每个二维码都不同。</para>
/// </summary>
public static class AdbQrPairing
{
    /// <summary>
    /// Length of the first random segment of the mDNS instance name, matching the
    /// layout used by the adb-qr reference implementation (ADB_WIFI_xxxxxxxxxxxxxx-yyyyyy).
    /// <para>mDNS 实例名第一段随机串的长度，对齐 adb-qr 参考实现的布局
    /// （ADB_WIFI_xxxxxxxxxxxxxx-yyyyyy）。</para>
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

    private static string RandomString(int length)
    {
        var bytes = RandomNumberGenerator.GetBytes(length);
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = Alphabet[bytes[i] % Alphabet.Length];
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
/// The mDNS service instance name, e.g. <c>ADB_WIFI_a1B2c3D4e5F6g7H-xy9Zwq</c>.
/// <para>mDNS 服务实例名，例如 <c>ADB_WIFI_a1B2c3D4e5F6g7H-xy9Zwq</c>。</para>
/// </param>
/// <param name="Password">
/// The pairing password (SPAKE2 shared secret input).
/// <para>配对密码（SPAKE2 共享密钥输入）。</para>
/// </param>
public sealed record AdbQrPairingCredentials(string ServiceName, string Password);