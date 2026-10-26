using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace UotanStudio.Comm.Transports.Usb.Drivers;

/// <summary>
/// Owns the local code-signing certificate used for WinUSB catalog signing: the PFX is persisted
/// under <c>%LOCALAPPDATA%\UotanStudio\DriverSigning</c> and reused across installs, and the
/// public certificate is installed into the LocalMachine Root and TrustedPublisher stores so the
/// self-signed catalog chains to a trusted publisher.
/// <para>持有 WinUSB 目录签名使用的本机代码签名证书：PFX 持久化在
/// <c>%LOCALAPPDATA%\UotanStudio\DriverSigning</c> 下并在多次安装间复用，公钥证书被装入
/// LocalMachine 的 Root 与 TrustedPublisher 存储，使自签目录能够链到受信任的发布者。</para>
/// </summary>
internal static class WinUsbDriverCertStore
{
    /// <summary>The X.500 subject of the self-signed code-signing certificate.
    /// <para>自签代码签名证书的 X.500 主题。</para></summary>
    internal const string CertificateSubject = "CN=UotanStudio Driver Signing";

    private const string CodeSigningOid = "1.3.6.1.5.5.7.3.3";

    /// <summary>The machine stores that must trust the self-signed publisher certificate.
    /// <para>必须信任该自签发布者证书的机器存储。</para></summary>
    private static readonly StoreName[] MachineStoreNames = [StoreName.Root, StoreName.TrustedPublisher];

    /// <summary>
    /// Loads the persisted signing certificate, or creates and persists a fresh self-signed
    /// certificate when the file is missing, unreadable, or expired.
    /// <para>加载持久化的签名证书；文件缺失、不可读或已过期时创建并持久化新的自签证书。</para>
    /// </summary>
    /// <returns>
    /// The signing certificate with a usable private key, or <see langword="null"/> after
    /// <paramref name="error"/> has been set to the failure reason.
    /// <para>带可用私钥的签名证书；失败时为 <see langword="null"/> 且 <paramref name="error"/> 携带原因。</para>
    /// </returns>
    internal static X509Certificate2? TryLoadOrCreate([NotNullWhen(false)] out string? error)
    {
        var pfxPath = GetPfxPath();
        if (File.Exists(pfxPath) && TryLoadPfx(pfxPath, out var loaded) && loaded is not null)
        {
            error = null;
            return loaded;
        }

        X509Certificate2 created;
        try
        {
            created = CreateSelfSigned();
        }
        catch (CryptographicException ex)
        {
            error = $"Unable to create the self-signed signing certificate: {ex.Message}";
            return null;
        }

        try
        {
            PersistPfx(pfxPath, created);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            error = $"Unable to persist the signing certificate to '{pfxPath}': {ex.Message}";
            return null;
        }

        if (!TryLoadPfx(pfxPath, out var reloaded) || reloaded is null)
        {
            error = $"The signing certificate written to '{pfxPath}' cannot be loaded back.";
            return null;
        }

        error = null;
        return reloaded;
    }

    /// <summary>
    /// Adds the public certificate to the LocalMachine Root and TrustedPublisher stores, skipping
    /// stores that already contain the fingerprint and tolerating a duplicate-add race.
    /// <para>把公钥证书加入 LocalMachine 的 Root 与 TrustedPublisher 存储；已包含该指纹时跳过，
    /// 并容忍并发重复添加。</para>
    /// </summary>
    /// <param name="certificate">The certificate to install. <para>要安装的证书。</para></param>
    /// <param name="error">Receives the first failure reason when the method returns <see langword="false"/>. <para>返回 <see langword="false"/> 时接收首个失败原因。</para></param>
    /// <returns>
    /// <see langword="true"/> when both stores contain the certificate afterwards; otherwise
    /// <paramref name="error"/> carries the first failure reason.
    /// <para>两个存储都包含该证书时返回 <see langword="true"/>；否则 <paramref name="error"/> 携带首个失败原因。</para>
    /// </returns>
    internal static bool TryInstallToMachineStores(X509Certificate2 certificate, [NotNullWhen(false)] out string? error)
    {
        foreach (var storeName in MachineStoreNames)
        {
            try
            {
                using var store = new X509Store(storeName, StoreLocation.LocalMachine);
                store.Open(OpenFlags.ReadWrite);
                if (ContainsThumbprint(store, certificate.Thumbprint))
                {
                    continue;
                }

                try
                {
                    store.Add(certificate);
                }
                catch (CryptographicException)
                {
                    if (!ContainsThumbprint(store, certificate.Thumbprint))
                    {
                        throw;
                    }
                }
            }
            catch (Exception ex) when (ex is CryptographicException or UnauthorizedAccessException or InvalidOperationException)
            {
                error = $"Unable to add the signing certificate to the LocalMachine '{storeName}' store: {ex.Message}";
                return false;
            }
        }

        error = null;
        return true;
    }

    private static string GetPfxPath()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UotanStudio", "DriverSigning", "usb-driver-signing.pfx");

    private static bool TryLoadPfx(string pfxPath, out X509Certificate2? certificate)
    {
        try
        {
            using var loaded = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, null,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
            if (!loaded.HasPrivateKey || (DateTime.UtcNow >= loaded.NotAfter))
            {
                certificate = null;
                return false;
            }

            certificate = new X509Certificate2(loaded);
            return true;
        }
        catch (CryptographicException)
        {
            certificate = null;
            return false;
        }
    }

    private static X509Certificate2 CreateSelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(CertificateSubject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        var enhancedKeyUsage = new X509EnhancedKeyUsageExtension(
            new OidCollection { new(CodeSigningOid) }, critical: true);
        request.CertificateExtensions.Add(enhancedKeyUsage);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = notBefore.AddYears(10);
        return request.CreateSelfSigned(notBefore, notAfter);
    }

    private static void PersistPfx(string pfxPath, X509Certificate2 certificate)
    {
        var directory = Path.GetDirectoryName(pfxPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var pfx = certificate.Export(X509ContentType.Pfx);
        var temporaryPath = pfxPath + ".tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, pfx);
            File.Move(temporaryPath, pfxPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    private static bool ContainsThumbprint(X509Store store, string thumbprint)
        => store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false).Count > 0;
}