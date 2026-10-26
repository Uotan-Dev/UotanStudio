using System.Security.Cryptography.X509Certificates;
using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Tcp;

namespace UotanStudio.Comm.Protocols.Adb;

/// <summary>
/// Opens ADB sessions to adbd's Android 11+ secure service (<c>_adb-tls-connect</c>) over this host's identity
/// chain, the same way a direct network endpoint is opened: the certificate derived from the user key is presented
/// in the handshake and the device authenticates that key, which pairing registered.
/// <para>以本主机身份链打开到 adbd Android 11+ 安全服务（<c>_adb-tls-connect</c>）的 ADB 会话，与打开直连网络端点
/// 的方式一致：握手时出示由用户密钥派生的证书，由设备校验该密钥——即配对阶段登记的那一个。</para>
/// </summary>
internal static class AdbSecureSessionFactory
{
    /// <summary>
    /// Default plaintext connect deadline of the transport.
    /// <para>传输的默认明文连接期限。</para>
    /// </summary>
    internal const int DefaultConnectTimeoutMs = 5000;

    /// <summary>
    /// Opens an unconnected session to <paramref name="host"/>:<paramref name="port"/>; the caller connects and owns
    /// the session. The session takes over every loaded identity and the derived certificate.
    /// <para>打开到 <paramref name="host"/>:<paramref name="port"/> 的未连接会话；调用方负责连接并拥有该会话。会话接管
    /// 已加载的全部身份与派生出的证书。</para>
    /// </summary>
    /// <param name="host">Device host name or IP literal. <para>设备主机名或 IP 字面量。</para></param>
    /// <param name="port">Secure adbd port, 1 through 65535. <para>adbd 安全端口，1 到 65535。</para></param>
    /// <param name="connectTimeoutMs">Plaintext connect deadline in milliseconds.
    /// <para>明文连接期限（毫秒）。</para></param>
    /// <returns>An unconnected secure session. <para>尚未连接的安全会话。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="host"/> is blank.
    /// <para><paramref name="host"/> 为空。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="port"/> or
    /// <paramref name="connectTimeoutMs"/> is out of range.
    /// <para><paramref name="port"/> 或 <paramref name="connectTimeoutMs"/> 超出范围。</para></exception>
    /// <exception cref="IOException">The plaintext connection could not be opened.
    /// <para>无法建立明文连接。</para></exception>
    internal static AdbSession Open(string host, int port,
        int connectTimeoutMs = DefaultConnectTimeoutMs)
    {
        var identities = AdbAuthentication.LoadIdentityChain();
        X509Certificate2? certificate = null;
        ICommTransport? transport = null;
        try
        {
            certificate = AdbHostCertificate.Create(identities[0].RsaKey);
            transport = new AdbTlsTransport(host, port, certificate, connectTimeoutMs);
            return new AdbSession(transport, identities);
        }
        catch
        {
            transport?.Dispose();
            certificate?.Dispose();
            foreach (var identity in identities)
            {
                identity.Dispose();
            }

            throw;
        }
    }
}