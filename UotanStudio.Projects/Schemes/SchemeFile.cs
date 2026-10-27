using System.Security.Cryptography;
using System.Text.Json;

namespace UotanStudio.Projects.Schemes;

/// <summary>
/// Persists scheme packages through a bounded read, an atomic temporary file and an optional detached signature.
/// <para>通过有界读取、原子临时文件与可选的分离签名持久化 Scheme 软件包。</para>
/// </summary>
/// <remarks>
/// A signed package keeps its signature inline and also publishes the same signature to a sibling
/// <c>.sig</c> file, so a host that strips unknown members can still verify the package. Loading prefers the inline
/// signature and falls back to the sidecar, keeping the lenient v1 read behavior for unsigned packages.
/// <para>已签名软件包同时以内嵌与旁挂 <c>.sig</c> 文件两种形式发布签名，使剥离未知成员的宿主仍能验签。
/// 加载时优先使用内嵌签名，缺失时回退到旁挂文件，并对未签名包保持 v1 宽容读取行为。</para>
/// </remarks>
public static class SchemeFile
{
    /// <summary>
    /// Maximum serialized scheme size, matching the project persistence bound.
    /// <para>序列化 Scheme 的最大尺寸，与工程持久化上限一致。</para>
    /// </summary>
    public const int MaximumBytes = 32 * 1024 * 1024;

    /// <summary>
    /// Maximum accepted detached signature file size.
    /// <para>允许的分离签名文件最大尺寸。</para>
    /// </summary>
    public const int MaximumSignatureBytes = 16 * 1024;

    /// <summary>
    /// Gets the detached signature path used for a scheme file.
    /// <para>获取 Scheme 文件使用的分离签名路径。</para>
    /// </summary>
    /// <param name="path">Scheme file path. <para>Scheme 文件路径。</para></param>
    /// <returns>The scheme path with an appended <c>.sig</c> suffix.
    /// <para>追加 <c>.sig</c> 后缀的 Scheme 路径。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.
    /// <para><paramref name="path"/> 为 <see langword="null"/>。</para></exception>
    public static string GetSignaturePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path + ".sig";
    }

    /// <summary>
    /// Saves a scheme package, rewriting the file only when its text changes.
    /// <para>保存 Scheme 软件包，仅在文本发生变化时才重写文件。</para>
    /// </summary>
    /// <param name="path">Destination scheme file; an empty value aborts the save without touching disk.
    /// <para>目标 Scheme 文件；为空时直接放弃保存，不产生任何磁盘操作。</para></param>
    /// <param name="package">Package to serialize; its format version must be readable by this implementation,
    /// and an embedded signature must verify against the current content.
    /// <para>待序列化的软件包；其格式版本必须能被本实现读取，且内嵌签名必须与当前内容一致。</para></param>
    /// <param name="cancellationToken">Cancels staging or comparison I/O; the destination keeps its previous content.
    /// <para>取消暂存或比对阶段的 I/O；目标文件保持原有内容。</para></param>
    /// <returns><see langword="true"/> when the destination holds text equivalent to the package; <see langword="false"/> when the path is empty, the version is unsupported, the signature does not match, the serialized state exceeds 32 MiB, or any I/O error occurred.
    /// <para>目标文件内容与软件包等价时返回 <see langword="true"/>；路径为空、版本不受支持、签名已不匹配、序列化结果超过 32 MiB 或发生任何 I/O 错误时返回 <see langword="false"/>。</para></returns>
    /// <exception cref="OperationCanceledException">Cancellation was requested before the destination was replaced.
    /// <para>在替换目标文件之前请求了取消。</para></exception>
    public static async Task<bool> SaveAsync(string path, SchemePackage package, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }
        try
        {
            package.EnsureSupportedVersion();
            package.Normalize();
            if (package.Signature is { Algorithm.Length: > 0, Value.Length: > 0 })
            {
                var verification = SchemeSigner.Verify(package);
                if (verification.IsSigned && !verification.IsValid)
                {
                    ProjectDiagnostics.Write(ProjectLogLevel.Error, "Persistence",
                        "Refusing to save a scheme whose embedded signature no longer matches: " + verification.Error);
                    return false;
                }
            }
            await AtomicJsonFile.WriteIfChangedAsync(path, package, SchemeJsonContext.Default.SchemePackage, cancellationToken).ConfigureAwait(false);
            try
            {
                await WriteSignatureAsync(path, package, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                TryDeleteSignature(path);
                throw;
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ProjectDiagnostics.Write(ProjectLogLevel.Error, "Persistence", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Loads a scheme package leniently: unknown members are ignored, missing optional members are repaired and a
    /// missing inline signature falls back to the sibling <c>.sig</c> file.
    /// <para>宽容读取 Scheme 软件包：忽略未知成员、修复缺失的可选成员，并在内嵌签名缺失时回退到旁挂 <c>.sig</c> 文件。</para>
    /// </summary>
    /// <param name="path">Scheme file to read; <see langword="null"/> is returned when the path is empty or the file does not exist.
    /// <para>要读取的 Scheme 文件；路径为空或文件不存在时返回 <see langword="null"/>。</para></param>
    /// <param name="cancellationToken">Cancels the streamed read; the exception propagates to the caller.
    /// <para>取消流式读取；异常会继续抛出给调用方。</para></param>
    /// <returns>The normalized package, or <see langword="null"/> when the file is missing, larger than 32 MiB, not valid scheme JSON, or stored with an unsupported format version. A malformed sidecar is ignored and the package reads as unsigned.
    /// <para>规范化后的软件包；文件缺失、超过 32 MiB、不是有效的 Scheme JSON，或存储了不受支持的格式版本时返回 <see langword="null"/>。格式错误的旁挂签名会被忽略，软件包按未签名读取。</para></returns>
    /// <exception cref="OperationCanceledException">Cancellation was requested while the file was being read or parsed.
    /// <para>在读取或解析文件期间请求了取消。</para></exception>
    public static async Task<SchemePackage?> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > MaximumBytes)
            {
                throw new InvalidDataException("Scheme package exceeds 32 MiB.");
            }
            var package = await JsonSerializer.DeserializeAsync(stream, SchemeJsonContext.Default.SchemePackage, cancellationToken).ConfigureAwait(false);
            if (package is null)
            {
                return null;
            }
            package.EnsureSupportedVersion();
            package.Normalize();
            if (package.Signature is not { Algorithm.Length: > 0, Value.Length: > 0 })
            {
                package.Signature = await ReadSignatureAsync(path, cancellationToken).ConfigureAwait(false);
            }
            return package;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ProjectDiagnostics.Write(ProjectLogLevel.Error, "Persistence", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Loads a scheme package and rejects one whose embedded or detached signature does not verify.
    /// <para>加载 Scheme 软件包，并拒绝内嵌或分离签名无法通过校验的软件包。</para>
    /// </summary>
    /// <param name="path">Scheme file to read. <para>要读取的 Scheme 文件。</para></param>
    /// <param name="cancellationToken">Cancels the bounded read. <para>取消有界读取。</para></param>
    /// <returns>The normalized package when it is unsigned or its signature verifies; otherwise <see langword="null"/>.
    /// <para>软件包未签名或签名通过校验时返回规范化软件包；否则返回 <see langword="null"/>。</para></returns>
    /// <exception cref="OperationCanceledException">Cancellation was requested while reading or verifying.
    /// <para>在读取或校验期间请求了取消。</para></exception>
    public static async Task<SchemePackage?> LoadVerifiedAsync(string path, CancellationToken cancellationToken = default)
    {
        var package = await LoadAsync(path, cancellationToken).ConfigureAwait(false);
        if (package is null)
        {
            return null;
        }
        var verification = SchemeSigner.Verify(package);
        if (verification.IsSigned && !verification.IsValid)
        {
            ProjectDiagnostics.Write(ProjectLogLevel.Error, "Persistence",
                "Scheme signature rejected: " + verification.Error);
            return null;
        }
        return package;
    }

    /// <summary>
    /// Verifies the signature of a scheme file, using its inline signature or the sibling <c>.sig</c> file.
    /// <para>校验 Scheme 文件的签名，使用内嵌签名或旁挂 <c>.sig</c> 文件。</para>
    /// </summary>
    /// <param name="path">Scheme file to read and verify. <para>要读取并校验的 Scheme 文件。</para></param>
    /// <param name="cancellationToken">Cancels the bounded read and verification. <para>取消有界读取与校验。</para></param>
    /// <returns>The verification outcome; an unreadable file reports an invalid unsigned result.
    /// <para>校验结果；文件不可读时返回无效的未签名结果。</para></returns>
    /// <exception cref="OperationCanceledException">Cancellation was requested while reading or verifying.
    /// <para>在读取或校验期间请求了取消。</para></exception>
    public static async Task<PluginSignatureVerification> VerifyAsync(string path, CancellationToken cancellationToken = default)
    {
        var package = await LoadAsync(path, cancellationToken).ConfigureAwait(false);
        return package is null
            ? new PluginSignatureVerification(false, false, "Scheme package cannot be read.")
            : SchemeSigner.Verify(package);
    }

    /// <summary>
    /// Signs the scheme file with a publisher private key and persists the embedded and detached signature.
    /// <para>使用发布者私钥为 Scheme 文件签名，并持久化内嵌与分离签名。</para>
    /// </summary>
    /// <param name="path">Scheme file to sign. <para>要签名的 Scheme 文件。</para></param>
    /// <param name="privateKey">RSA 2048 to 4096 bits or NIST P-256 ECDSA private key.
    /// <para>2048 到 4096 位的 RSA 或 NIST P-256 ECDSA 私钥。</para></param>
    /// <param name="publisherId">Stable publisher identifier. <para>稳定的发布者标识。</para></param>
    /// <param name="publisherName">Display name of 1 to 128 characters. <para>1 到 128 个字符的显示名称。</para></param>
    /// <param name="cancellationToken">Cancels the read, staging and replacement. <para>取消读取、暂存与替换。</para></param>
    /// <returns><see langword="true"/> when the signed package was persisted; <see langword="false"/> when the file is missing, the package is unreadable, or the publisher identity or key is rejected.
    /// <para>已签名软件包持久化成功时返回 <see langword="true"/>；文件缺失、软件包不可读，或发布者身份/密钥被拒绝时返回 <see langword="false"/>。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="privateKey"/> is <see langword="null"/>.
    /// <para><paramref name="privateKey"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested while reading, signing or saving.
    /// <para>在读取、签名或保存期间请求了取消。</para></exception>
    public static async Task<bool> SignAsync(string path, AsymmetricAlgorithm privateKey, string publisherId,
        string publisherName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }
        var package = await LoadAsync(path, cancellationToken).ConfigureAwait(false);
        if (package is null)
        {
            return false;
        }
        try
        {
            SchemeSigner.Sign(package, privateKey, publisherId, publisherName);
        }
        catch (ArgumentException ex)
        {
            ProjectDiagnostics.Write(ProjectLogLevel.Error, "Persistence", ex.Message);
            return false;
        }
        return await SaveAsync(path, package, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteSignatureAsync(string path, SchemePackage package,
        CancellationToken cancellationToken)
    {
        var signaturePath = GetSignaturePath(path);
        if (package.Signature is { Algorithm.Length: > 0, Value.Length: > 0 } signature)
        {
            await AtomicJsonFile.WriteIfChangedAsync(signaturePath, signature,
                SchemeSignatureJsonContext.Default.VisualPluginSignature, cancellationToken).ConfigureAwait(false);
            return;
        }
        TryDeleteSignature(path);
    }

    private static void TryDeleteSignature(string path)
    {
        var signaturePath = GetSignaturePath(path);
        try
        {
            if (File.Exists(signaturePath))
            {
                File.Delete(signaturePath);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ProjectDiagnostics.Write(ProjectLogLevel.Warn, "Persistence",
                "Scheme signature sidecar cannot be removed: " + error.Message);
        }
    }

    private static async Task<VisualPluginSignature?> ReadSignatureAsync(string path,
        CancellationToken cancellationToken)
    {
        var signaturePath = GetSignaturePath(path);
        try
        {
            if (!File.Exists(signaturePath))
            {
                return null;
            }
            await using var stream = new FileStream(signaturePath, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length is 0 or > MaximumSignatureBytes)
            {
                return null;
            }
            var signature = await JsonSerializer.DeserializeAsync(stream,
                SchemeSignatureJsonContext.Default.VisualPluginSignature, cancellationToken).ConfigureAwait(false);
            return signature is { Algorithm.Length: > 0, Value.Length: > 0 } ? signature : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or InvalidDataException)
        {
            ProjectDiagnostics.Write(ProjectLogLevel.Warn, "Persistence",
                "Scheme signature sidecar cannot be read; treating the package as unsigned. " + ex.Message);
            return null;
        }
    }
}