using System.Security.Cryptography;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>
/// Resolves the BootROM SLA responder for a target from user-supplied key material. The
/// library never bundles keys; a provider returns null when it has nothing for the chip.
/// <para>按用户提供密钥材料解析目标芯片的 BootROM SLA 响应器。库不捆绑任何密钥；
/// 无对应芯片材料时返回 null。</para>
/// </summary>
public interface IMtkSlaKeyProvider
{
    /// <summary>Gets a responder able to answer the SLA challenge of this device, or null.
    /// <para>获取能应答该设备 SLA 挑战的响应器，无则返回 null。</para></summary>
    /// <param name="device">Identity whose hardware code and subcode select the key material.
    /// <para>以其硬件代码与子代码选择密钥材料的身份。</para></param>
    /// <returns>A responder for this device, or <see langword="null"/> when no key material matches.
    /// <para>该设备的响应器；无匹配密钥材料时返回 <see langword="null"/>。</para></returns>
    IMtkBootRomSlaResponder? ResolveSlaResponder(MtkDeviceInfo device);
}

/// <summary>
/// Resolves the vendor-issued DAA resources (root certificate for 0xE0 / authentication file
/// for 0xE2) for a target from user-supplied material.
/// <para>按用户提供材料解析目标的厂商 DAA 资源（0xE0 根证书 / 0xE2 认证文件）。</para>
/// </summary>
public interface IMtkDaaResourceProvider
{
    /// <summary>Gets the root certificate bytes for the device, or null.
    /// <para>获取设备的根证书字节，无则 null。</para></summary>
    /// <param name="device">Identity whose hardware code selects the certificate resource.
    /// <para>以其硬件代码选择证书资源的身份。</para></param>
    /// <param name="cancellationToken">Cooperative cancellation of the resolution.
    /// <para>对解析过程的协作式取消。</para></param>
    /// <returns>The root certificate bytes, or <see langword="null"/> when none is available.
    /// <para>根证书字节；不可用时返回 <see langword="null"/>。</para></returns>
    Task<ReadOnlyMemory<byte>?> ResolveRootCertificateAsync(MtkDeviceInfo device, CancellationToken cancellationToken);

    /// <summary>Gets the authentication resource bytes for the device, or null.
    /// <para>获取设备的认证资源字节，无则 null。</para></summary>
    /// <param name="device">Identity whose hardware code selects the authentication resource.
    /// <para>以其硬件代码选择认证资源的身份。</para></param>
    /// <param name="cancellationToken">Cooperative cancellation of the resolution.
    /// <para>对解析过程的协作式取消。</para></param>
    /// <returns>The authentication resource bytes, or <see langword="null"/> when none is available.
    /// <para>认证资源字节；不可用时返回 <see langword="null"/>。</para></returns>
    Task<ReadOnlyMemory<byte>?> ResolveAuthenticationAsync(MtkDeviceInfo device, CancellationToken cancellationToken);
}

/// <summary>
/// File-directory key store following the mtkclient keys-directory convention: RSA private
/// keys named <c>sla_&lt;hwcode&gt;.pem</c> (optionally <c>sla_&lt;hwcode&gt;_&lt;subcode&gt;.pem</c>,
/// the more specific file wins) and DAA resources named <c>daa_cert_&lt;hwcode&gt;.bin</c> /
/// <c>daa_auth_&lt;hwcode&gt;.bin</c>. Identifiers are matched case-insensitively; nothing is
/// downloaded, generated or inferred beyond the file naming.
/// <para>按 mtkclient keys 目录习惯的文件目录密钥库：RSA 私钥命名为
/// <c>sla_&lt;hwcode&gt;.pem</c>（可选更具体的 <c>sla_&lt;hwcode&gt;_&lt;subcode&gt;.pem</c>，
/// 以更具体者为准），DAA 资源命名为 <c>daa_cert_&lt;hwcode&gt;.bin</c> /
/// <c>daa_auth_&lt;hwcode&gt;.bin</c>。标识不区分大小写；除文件命名外不做任何下载、生成或推断。</para>
/// </summary>
public sealed class MtkDirectoryKeyStore : IMtkSlaKeyProvider, IMtkDaaResourceProvider
{
    private readonly string _directory;

    /// <summary>Creates a store over an existing user-supplied directory.
    /// <para>在已存在的用户目录上创建密钥库。</para></summary>
    /// <param name="directory">Path to the keys directory; it is not created or validated for existence.
    /// <para>keys 目录路径；不会创建该目录，也不校验其是否存在。</para></param>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is null, empty, or white space.
    /// <para><paramref name="directory"/> 为 null、空或仅含空白。</para></exception>
    public MtkDirectoryKeyStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
    }

    /// <summary>Resolves an SLA responder from a matching <c>sla_*.pem</c> key, loading it into a fresh RSA instance.
    /// <para>从匹配的 <c>sla_*.pem</c> 密钥解析 SLA 响应器，并将其加载到新的 RSA 实例。</para></summary>
    /// <param name="device">Identity whose hardware code and subcode select the key file.
    /// <para>以其硬件代码与子代码选择密钥文件的身份。</para></param>
    /// <returns>A responder backed by the imported key, or <see langword="null"/> when no file matches or the
    /// key cannot be read or imported.
    /// <para>由导入密钥支撑的响应器；无匹配文件或密钥无法读取/导入时返回 <see langword="null"/>。</para></returns>
    public IMtkBootRomSlaResponder? ResolveSlaResponder(MtkDeviceInfo device)
    {
        var specific = KeyPath($"sla_{device.HardwareCode:x4}_{device.HardwareSubCode:x4}.pem");
        var generic = KeyPath($"sla_{device.HardwareCode:x4}.pem");
        var path = specific ?? generic;
        if (path is null)
        {
            return null;
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(File.ReadAllText(path));
            return new MtkBootRomRsaSlaResponder(rsa, device);
        }
        catch (Exception ex) when (ex is IOException or FormatException or CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Reads <c>daa_cert_&lt;hwcode&gt;.bin</c> from the keys directory for the device.
    /// <para>从 keys 目录读取设备的 <c>daa_cert_&lt;hwcode&gt;.bin</c>。</para></summary>
    /// <param name="device">Identity whose hardware code selects the certificate file.
    /// <para>以其硬件代码选择证书文件的身份。</para></param>
    /// <param name="cancellationToken">Cooperative cancellation of the file read.
    /// <para>对文件读取的协作式取消。</para></param>
    /// <returns>The certificate bytes, or <see langword="null"/> when the file is absent or unreadable.
    /// <para>证书字节；文件缺失或不可读时返回 <see langword="null"/>。</para></returns>
    public Task<ReadOnlyMemory<byte>?> ResolveRootCertificateAsync(MtkDeviceInfo device, CancellationToken cancellationToken)
        => ResolveDaaResourceAsync($"daa_cert_{device.HardwareCode:x4}.bin", cancellationToken);

    /// <summary>Reads <c>daa_auth_&lt;hwcode&gt;.bin</c> from the keys directory for the device.
    /// <para>从 keys 目录读取设备的 <c>daa_auth_&lt;hwcode&gt;.bin</c>。</para></summary>
    /// <param name="device">Identity whose hardware code selects the authentication file.
    /// <para>以其硬件代码选择认证文件的身份。</para></param>
    /// <param name="cancellationToken">Cooperative cancellation of the file read.
    /// <para>对文件读取的协作式取消。</para></param>
    /// <returns>The authentication bytes, or <see langword="null"/> when the file is absent or unreadable.
    /// <para>认证字节；文件缺失或不可读时返回 <see langword="null"/>。</para></returns>
    public Task<ReadOnlyMemory<byte>?> ResolveAuthenticationAsync(MtkDeviceInfo device, CancellationToken cancellationToken)
        => ResolveDaaResourceAsync($"daa_auth_{device.HardwareCode:x4}.bin", cancellationToken);

    private async Task<ReadOnlyMemory<byte>?> ResolveDaaResourceAsync(string fileName, CancellationToken cancellationToken)
    {
        var path = KeyPath(fileName);
        if (path is null)
        {
            return null;
        }

        try
        {
            return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private string? KeyPath(string fileName)
    {
        try
        {
            var exact = Path.Combine(_directory, fileName);
            return File.Exists(exact) ? exact
                : Directory.EnumerateFiles(_directory, fileName, new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}