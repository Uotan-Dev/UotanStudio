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
    /// 获取能应答该设备 SLA 挑战的响应器，无则返回 null。</summary>
    IMtkBootRomSlaResponder? ResolveSlaResponder(MtkDeviceInfo device);
}

/// <summary>
/// Resolves the vendor-issued DAA resources (root certificate for 0xE0 / authentication file
/// for 0xE2) for a target from user-supplied material.
/// <para>按用户提供材料解析目标的厂商 DAA 资源（0xE0 根证书 / 0xE2 认证文件）。</para>
/// </summary>
public interface IMtkDaaResourceProvider
{
    /// <summary>Gets the root certificate bytes for the device, or null. 设备的根证书字节，无则 null。</summary>
    Task<ReadOnlyMemory<byte>?> ResolveRootCertificateAsync(MtkDeviceInfo device, CancellationToken cancellationToken);

    /// <summary>Gets the authentication resource bytes for the device, or null. 设备的认证资源字节，无则 null。</summary>
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

    /// <summary>Creates a store over an existing user-supplied directory. 在已存在的用户目录上创建密钥库。</summary>
    public MtkDirectoryKeyStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
    }

    /// <inheritdoc />
    public IMtkBootRomSlaResponder? ResolveSlaResponder(MtkDeviceInfo device)
    {
        // Prefer the chip/subcode-specific key, then the plain chip key.
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

    /// <inheritdoc />
    public Task<ReadOnlyMemory<byte>?> ResolveRootCertificateAsync(MtkDeviceInfo device, CancellationToken cancellationToken)
        => ResolveDaaResourceAsync($"daa_cert_{device.HardwareCode:x4}.bin", cancellationToken);

    /// <inheritdoc />
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