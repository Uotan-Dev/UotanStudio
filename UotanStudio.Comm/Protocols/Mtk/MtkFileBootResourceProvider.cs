namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>
/// Boot resource provider over explicitly selected files, following the upstream
/// <c>--auth</c> convention: the authentication file is sent verbatim with boot command E2
/// (and, when selected, the root certificate with E0) without any parsing, padding or derivation.
/// <para>基于显式选定文件的启动资源提供方，遵循上游 <c>--auth</c> 约定：认证文件按原样随启动命令
/// E2 发送（如选定根证书则随 E0 发送），不做任何解析、填充或派生。</para>
/// </summary>
public sealed class MtkFileBootResourceProvider : IMtkBootResourceProvider
{
    private readonly string? _authenticationPath;
    private readonly string? _rootCertificatePath;

    /// <summary>Creates a provider over explicit file paths; at least one path must be given.
    /// <para>基于显式文件路径创建提供方；至少需要给出一个路径。</para></summary>
    /// <param name="authenticationPath">Optional path of the authentication resource sent with E2.
    /// <para>可选的、随 E2 发送的认证资源路径。</para></param>
    /// <param name="rootCertificatePath">Optional path of the root certificate sent with E0.
    /// <para>可选的、随 E0 发送的根证书路径。</para></param>
    /// <exception cref="ArgumentException">Both paths are null, empty, or white space.
    /// <para>两个路径均为 null、空或仅含空白。</para></exception>
    public MtkFileBootResourceProvider(string? authenticationPath = null, string? rootCertificatePath = null)
    {
        if (string.IsNullOrWhiteSpace(authenticationPath) && string.IsNullOrWhiteSpace(rootCertificatePath))
        {
            throw new ArgumentException("At least one boot resource file path must be selected.");
        }
        _authenticationPath = string.IsNullOrWhiteSpace(authenticationPath) ? null : authenticationPath;
        _rootCertificatePath = string.IsNullOrWhiteSpace(rootCertificatePath) ? null : rootCertificatePath;
    }

    /// <summary>Opens the explicitly selected file for the requested kind as an owned resource.
    /// <para>按所请求种类打开显式选定的文件作为持有资源。</para></summary>
    /// <param name="device">Identity queried from the target; the file selection is independent of it.
    /// <para>从目标查询到的身份；文件选择与其无关。</para></param>
    /// <param name="kind">The requested resource kind. <para>所请求的资源种类。</para></param>
    /// <param name="cancellationToken">Cancellation for the file open.
    /// <para>用于文件打开的取消令牌。</para></param>
    /// <returns>An owned read-only stream positioned at the first byte.
    /// <para>定位在首字节的只读持有流。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is undefined.
    /// <para><paramref name="kind"/> 未定义。</para></exception>
    /// <exception cref="InvalidOperationException">No file was selected for the requested kind, the
    /// file is absent or unreadable, or it exceeds the addressable byte count.
    /// <para>所请求种类未选定文件，文件缺失或不可读，或超出可寻址字节数。</para></exception>
    public Task<MtkBootResource> OpenAsync(MtkDeviceInfo device, MtkBootResourceKind kind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        var path = kind switch
        {
            MtkBootResourceKind.Authentication => _authenticationPath,
            MtkBootResourceKind.RootCertificate => _rootCertificatePath,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException($"No {kind} file was selected.");
        }

        FileStream stream;
        try
        {
            stream = File.OpenRead(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"The selected {kind} file is absent or unreadable.", ex);
        }
        try
        {
            if (stream.Length > int.MaxValue)
            {
                throw new InvalidOperationException("The selected boot resource file is too large.");
            }
            var resource = new MtkBootResource(stream, (int)stream.Length);
            return Task.FromResult(resource);
        }
        catch (Exception)
        {
            stream.Dispose();
            throw;
        }
    }
}