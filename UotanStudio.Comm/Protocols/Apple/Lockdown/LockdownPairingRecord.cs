using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace UotanStudio.Comm.Protocols.Apple.Lockdown;

/// <summary>
/// A lockdownd pair record: the host/device certificate chain plus the identifiers exchanged during
/// pairing. The record is serialized as a plist dictionary whose PEM fields are plain strings and
/// whose <c>EscrowBag</c> is a data blob.
/// <para>lockdownd 配对记录：配对时交换的主机/设备证书链与标识。记录序列化为属性列表字典，
/// PEM 字段为纯字符串，<c>EscrowBag</c> 为数据块。</para>
/// </summary>
public sealed class LockdownPairingRecord
{
    /// <summary>Gets or sets the host identifier handed to lockdownd (32 uppercase hex characters). <para>获取或设置交给 lockdownd 的主机标识（32 位大写十六进制）。</para></summary>
    public required string HostId { get; set; }

    /// <summary>Gets or sets the host system build identifier. <para>获取或设置主机系统构建标识。</para></summary>
    public required string SystemBuid { get; set; }

    /// <summary>Gets or sets the device certificate issued at provisioning (PEM). <para>获取或设置设备出厂证书（PEM）。</para></summary>
    public required string DeviceCertificatePem { get; set; }

    /// <summary>Gets or sets the host leaf certificate (PEM). <para>获取或设置主机叶子证书（PEM）。</para></summary>
    public required string HostCertificatePem { get; set; }

    /// <summary>Gets or sets the host leaf private key (PKCS#8 PEM). <para>获取或设置主机叶子私钥（PKCS#8 PEM）。</para></summary>
    public required string HostPrivateKeyPem { get; set; }

    /// <summary>Gets or sets the host root CA certificate (PEM). <para>获取或设置主机根 CA 证书（PEM）。</para></summary>
    public required string RootCertificatePem { get; set; }

    /// <summary>Gets or sets the host root CA private key (PKCS#8 PEM). <para>获取或设置主机根 CA 私钥（PKCS#8 PEM）。</para></summary>
    public required string RootPrivateKeyPem { get; set; }

    /// <summary>Gets or sets the escrow bag returned by the device, enabling unattended service unlock. <para>获取或设置设备返回的 escrow bag，用于免交互解锁服务。</para></summary>
    public byte[]? EscrowBag { get; set; }

    /// <summary>Gets or sets the device Wi-Fi MAC address reported during pairing. <para>获取或设置配对期间上报的设备 Wi-Fi MAC 地址。</para></summary>
    public string? WifiMacAddress { get; set; }

    /// <summary>
    /// Creates a pending record by generating the host certificate chain around the device public key.
    /// <para>围绕设备公钥生成主机证书链，创建待配对记录。</para>
    /// </summary>
    /// <param name="hostId">The host identifier to embed. <para>要嵌入的主机标识。</para></param>
    /// <param name="systemBuid">The system build identifier to embed. <para>要嵌入的系统构建标识。</para></param>
    /// <param name="devicePublicKeyPem">The device public key as reported by lockdownd (PKCS#1 or SPKI PEM). <para>lockdownd 上报的设备公钥（PKCS#1 或 SPKI PEM）。</para></param>
    /// <param name="wifiMacAddress">Optional device Wi-Fi MAC address. <para>可选的设备 Wi-Fi MAC 地址。</para></param>
    /// <returns>The pending pairing record. <para>待配对记录。</para></returns>
    /// <exception cref="IOException">The device public key cannot be parsed. <para>设备公钥无法解析时抛出。</para></exception>
    public static LockdownPairingRecord CreatePending(string hostId, string systemBuid,
        byte[] devicePublicKeyPem, string? wifiMacAddress = null)
    {
        try
        {
            using var rootKey = RSA.Create(2048);
            using var hostKey = RSA.Create(2048);
            using var devicePublicRsa = RSA.Create();
            devicePublicRsa.ImportFromPem(Encoding.ASCII.GetString(devicePublicKeyPem));

            var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
            var notAfter = notBefore.AddYears(10);
            using var rootCertificate = CreateCertificate(rootKey, issuer: null, serialNumber: 1, ca: true, notBefore, notAfter);
            using var hostCertificate = CreateCertificate(hostKey, rootCertificate, serialNumber: 2, ca: false, notBefore, notAfter);
            using var deviceCertificate = CreateCertificate(devicePublicRsa, rootCertificate, serialNumber: 3, ca: false, notBefore, notAfter);

            return new LockdownPairingRecord
            {
                HostId = hostId,
                SystemBuid = systemBuid,
                DeviceCertificatePem = ExportCertificatePem(deviceCertificate),
                HostCertificatePem = ExportCertificatePem(hostCertificate),
                HostPrivateKeyPem = ExportPrivateKeyPem(hostKey),
                RootCertificatePem = ExportCertificatePem(rootCertificate),
                RootPrivateKeyPem = ExportPrivateKeyPem(rootKey),
                WifiMacAddress = wifiMacAddress,
            };
        }
        catch (CryptographicException exception)
        {
            throw new IOException("The device public key reported by lockdownd cannot be parsed.", exception);
        }
        catch (AsnContentException exception)
        {
            throw new IOException("The device public key reported by lockdownd cannot be parsed.", exception);
        }
    }

    private static readonly byte[] EmptyDistinguishedName = [0x30, 0x00];

    // One validity window is shared by the root and its children: DER encodes NotAfter with second precision, so a
    // child minted a moment later would report a NotAfter one second past its issuer and be rejected outright.
    private static X509Certificate2 CreateCertificate(RSA subjectKey, X509Certificate2? issuer, int serialNumber,
        bool ca, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        var request = new CertificateRequest(new X500DistinguishedName(EmptyDistinguishedName), subjectKey,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(ca, false, 0, critical: true));
        if (!ca)
        {
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(new PublicKey(subjectKey), critical: false));
        }

        return issuer is null
            ? request.CreateSelfSigned(notBefore, notAfter)
            : request.Create(issuer, notBefore, notAfter, [(byte)serialNumber]);
    }

    private static string ExportCertificatePem(X509Certificate2 certificate) =>
        new string(PemEncoding.Write("CERTIFICATE", certificate.RawData));

    private static string ExportPrivateKeyPem(RSA key) =>
        new string(PemEncoding.Write("PRIVATE KEY", key.ExportPkcs8PrivateKey()));

    /// <summary>
    /// Builds the TLS client identity used to authenticate to lockdown services.
    /// <para>构建用于 lockdown 服务认证的 TLS 客户端身份。</para>
    /// </summary>
    /// <returns>The host certificate carrying the host private key. <para>携带主机私钥的主机证书。</para></returns>
    public X509Certificate2 BuildTlsClientCertificate()
    {
        using var created = X509Certificate2.CreateFromPem(HostCertificatePem, HostPrivateKeyPem);
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12), null);
    }

    /// <summary>
    /// Serializes the record into the plist dictionary lockdownd and usbmuxd exchange.
    /// <para>将记录序列化为 lockdownd 与 usbmuxd 交换的属性列表字典。</para>
    /// </summary>
    /// <returns>The plist representation. <para>属性列表表示。</para></returns>
    public Dictionary<string, object> ToPlist()
    {
        var record = new Dictionary<string, object>
        {
            ["DeviceCertificate"] = DeviceCertificatePem,
            ["HostCertificate"] = HostCertificatePem,
            ["HostID"] = HostId,
            ["HostPrivateKey"] = HostPrivateKeyPem,
            ["RootCertificate"] = RootCertificatePem,
            ["RootPrivateKey"] = RootPrivateKeyPem,
            ["SystemBUID"] = SystemBuid,
        };
        if (EscrowBag is { Length: > 0 })
        {
            record["EscrowBag"] = EscrowBag;
        }

        if (WifiMacAddress is { Length: > 0 })
        {
            record["WiFiMACAddress"] = WifiMacAddress;
        }

        return record;
    }

    /// <summary>
    /// Rebuilds a record from its plist representation; accepts both PEM strings (modern pairing) and
    /// DER data blobs (native Apple records).
    /// <para>从属性列表表示重建记录；同时接受 PEM 字符串（现代配对）与 DER 数据块（Apple 原生记录）。</para>
    /// </summary>
    /// <param name="plist">The plist dictionary. <para>属性列表字典。</para></param>
    /// <returns>The rebuilt record. <para>重建的记录。</para></returns>
    /// <exception cref="InvalidDataException">Required fields are missing or malformed. <para>必需字段缺失或畸形时抛出。</para></exception>
    public static LockdownPairingRecord FromPlist(Dictionary<string, object> plist)
    {
        string RequireCertificate(string key) =>
            plist.TryGetValue(key, out var value)
                ? NormalizeCertificate(value, key)
                : throw new InvalidDataException($"The pair record is missing the '{key}' field.");

        string RequirePrivateKey(string key) =>
            plist.TryGetValue(key, out var value)
                ? NormalizePrivateKey(value, key)
                : throw new InvalidDataException($"The pair record is missing the '{key}' field.");

        byte[]? escrow = plist.TryGetValue("EscrowBag", out var escrowValue) && escrowValue is byte[] bag
            ? bag
            : null;
        return new LockdownPairingRecord
        {
            HostId = plist.TryGetValue("HostID", out var hostId) && hostId is string hostIdText ? hostIdText
                : throw new InvalidDataException("The pair record is missing the 'HostID' field."),
            SystemBuid = plist.TryGetValue("SystemBUID", out var buid) && buid is string buidText ? buidText : string.Empty,
            DeviceCertificatePem = RequireCertificate("DeviceCertificate"),
            HostCertificatePem = RequireCertificate("HostCertificate"),
            HostPrivateKeyPem = RequirePrivateKey("HostPrivateKey"),
            RootCertificatePem = RequireCertificate("RootCertificate"),
            RootPrivateKeyPem = RequirePrivateKey("RootPrivateKey"),
            EscrowBag = escrow,
            WifiMacAddress = plist.TryGetValue("WiFiMACAddress", out var wifi) && wifi is string wifiText ? wifiText : null,
        };
    }

    private static string? DecodePemText(byte[] value)
    {
        if (value.Length < 11)
        {
            return null;
        }

        var text = Encoding.ASCII.GetString(value);
        return text.Contains("-----BEGIN", StringComparison.Ordinal) ? text : null;
    }

    private static string NormalizeCertificate(object value, string field)
    {
        if (value is string pem && pem.Contains("-----BEGIN", StringComparison.Ordinal))
        {
            return pem;
        }

        if (value is byte[] der && der.Length > 0)
        {
            return DecodePemText(der) ?? new string(PemEncoding.Write("CERTIFICATE", der));
        }

        throw new InvalidDataException($"The pair record field '{field}' is not a certificate.");
    }

    private static string NormalizePrivateKey(object value, string field)
    {
        if (value is string pem && pem.Contains("-----BEGIN", StringComparison.Ordinal))
        {
            return pem;
        }

        if (value is byte[] der && der.Length > 0)
        {
            var embedded = DecodePemText(der);
            if (embedded is not null)
            {
                return embedded;
            }

            foreach (var label in (ReadOnlySpan<string>)["PRIVATE KEY", "RSA PRIVATE KEY"])
            {
                try
                {
                    using var probe = RSA.Create();
                    var candidate = new string(PemEncoding.Write(label, der));
                    probe.ImportFromPem(candidate);
                    return candidate;
                }
                catch (CryptographicException)
                {
                }
                catch (AsnContentException)
                {
                }
            }
        }

        throw new InvalidDataException($"The pair record field '{field}' is not a private key.");
    }
}