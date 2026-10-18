namespace UotanStudio.Comm.Protocols.Mtk;

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
/// File-directory key store following the mtkclient keys-directory convention: RSA private keys named <c>sla_ hwcode .pem</c> (optionally <c>sla_ hwcode _ subcode .pem</c>, the more specific file wins) and DAA resources named <c>daa_cert_ hwcode .bin</c> and <c>daa_auth_ hwcode .bin</c>. Identifiers match case-insensitively; nothing is downloaded, generated or inferred.
/// <para>遵循 mtkclient 密钥目录约定的文件目录密钥库：RSA 私钥命名为 <c>sla_ hwcode .pem</c>（可选 <c>sla_ hwcode _ subcode .pem</c>，更具体的文件优先），DAA 资源命名为 <c>daa_cert_ hwcode .bin</c> 与 <c>daa_auth_ hwcode .bin</c>。标识符大小写不敏感；不下载、不生成、不推断。</para>
/// </summary>
public sealed class MtkDirectoryKeyStore : IMtkDaaResourceProvider, IMtkBootResourceProvider
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

    /// <summary>Opens a copy of the keys-directory resource for one explicitly selected kind, so the same directory that answers a Boot ROM SLA request can also supply the E0 certificate or the E2 authentication file.
    /// <para>为一个显式选定的资源种类打开 keys 目录中资源的副本，使同一个既能应答 Boot ROM SLA 请求的目录也能提供 E0 根证书或 E2 认证文件。</para></summary>
    /// <param name="device">Identity whose hardware code selects the resource file.
    /// <para>以其硬件代码选择资源文件的身份。</para></param>
    /// <param name="kind">The explicitly selected resource kind.<para>显式选定的资源种类。</para></param>
    /// <param name="cancellationToken">Cancellation for the file read.<para>用于文件读取的取消令牌。</para></param>
    /// <returns>An owned resource wrapping a private copy of the file bytes.
    /// <para>包装文件字节私有副本的持有资源。</para></returns>
    /// <exception cref="InvalidOperationException">The file is absent or unreadable for the requested kind.
    /// <para>所请求种类的文件缺失或不可读。</para></exception>
    public async Task<MtkBootResource> OpenAsync(MtkDeviceInfo device, MtkBootResourceKind kind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        var payload = kind switch
        {
            MtkBootResourceKind.RootCertificate => await ResolveRootCertificateAsync(device, cancellationToken).ConfigureAwait(false),
            MtkBootResourceKind.Authentication => await ResolveAuthenticationAsync(device, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        if (payload is not { } bytes)
        {
            throw new InvalidOperationException(
                $"The keys directory has no {kind} resource for hardware code 0x{device.HardwareCode:X4}.");
        }

        return new MtkBootResource(new MemoryStream(bytes.ToArray(), writable: false), bytes.Length);
    }

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