using System.Text.Json;
using System.Text.Json.Serialization;

namespace UotanStudio.Projects;

/// <summary>
/// AOT-safe and bounded persistence for portable visual-plugin JSON files.
/// <para>面向可移植可视化插件 JSON 文件的 AOT 安全、有界持久化入口。</para>
/// </summary>
public static class VisualPluginFile
{
    /// <summary>Maximum accepted detached signature file size. <para>允许的分离签名文件最大字节数。</para></summary>
    public const long MaximumDetachedSignatureSize = 8 * 1024;

    /// <summary>Synchronously loads one small startup package with the same bounds and validation as the async path.
    /// <para>同步加载一个小型启动期包，边界与校验规则和异步路径一致。</para></summary>
    /// <param name="path">File to read. A file whose first four bytes are the ZIP signature is routed to <see cref="VisualPluginBundle.Load(string)"/>; any other file is parsed as JSON and must not declare assets. A sibling <c>.sig</c> file supplies the signature when the package itself is unsigned.
    /// <para>要读取的文件。前四个字节为 ZIP 签名的文件交给 <see cref="VisualPluginBundle.Load(string)"/>；其余文件按 JSON 解析，且不得声明资源。包本身未签名时由同名的 <c>.sig</c> 旁挂文件提供签名。</para></param>
    /// <returns>The validated package. The returned stream is closed before this method returns; the package is fully materialized in memory.
    /// <para>校验后的包。返回前文件流已关闭，包内容已完整载入内存。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null, empty, or whitespace.
    /// <para><paramref name="path"/> 为 null、空白或全空格。</para></exception>
    /// <exception cref="InvalidDataException">The file exceeds <see cref="VisualPluginValidation.MaximumFileSize"/>, the JSON document is empty or structurally invalid, validation reports errors, a JSON (non-bundle) package declares assets, or the detached signature is malformed or does not match.
    /// <para>文件超过 <see cref="VisualPluginValidation.MaximumFileSize"/>、JSON 文档为空或结构非法、校验报错、非 bundle 的 JSON 包声明了资源，或分离签名格式非法或与载荷不符。</para></exception>
    public static VisualPluginPackage Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var isBundle = IsBundle(path);
        var package = isBundle ? VisualPluginBundle.Load(path) : LoadJson(path);
        ApplyDetachedSignature(package, path);
        EnsureValid(package, allowBundledAssets: isBundle);
        return package;
    }

    /// <summary>Saves a structurally valid package through the existing atomic JSON publication path.
    /// <para>通过既有的原子 JSON 发布路径保存结构合法的包。</para></summary>
    /// <param name="path">Destination file. A path ending in <c>.uotanplugin</c> (case-insensitive) is written as a binary bundle through <c>VisualPluginBundle.SaveAsync</c>; any other path is written as indented JSON.
    /// <para>目标文件。以 <c>.uotanplugin</c> 结尾（不区分大小写）的路径经 <c>VisualPluginBundle.SaveAsync</c> 写为二进制 bundle；其余路径写为缩进 JSON。</para></param>
    /// <param name="package">Package to persist; it must pass <see cref="VisualPluginValidation.Validate"/> before anything is written. A signed package also publishes a sibling <c>.sig</c> file; saving an unsigned package removes a stale one.
    /// <para>要持久化的包；写入前必须先通过 <see cref="VisualPluginValidation.Validate"/>。已签名包会同时发布旁挂 <c>.sig</c> 文件；保存未签名包会移除残留的旧文件。</para></param>
    /// <param name="cancellationToken">Cancels the publication; a cancelled save leaves the previous target content in place.
    /// <para>取消发布操作；被取消的保存会保留目标的原有内容。</para></param>
    /// <returns>A task that completes once the package has been fully written and published to <paramref name="path"/>.
    /// <para>在包完整写入并发布到 <paramref name="path"/> 后完成的任务。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null, empty, or whitespace.
    /// <para><paramref name="path"/> 为 null、空白或全空格。</para></exception>
    /// <exception cref="ArgumentNullException"><paramref name="package"/> is null.
    /// <para><paramref name="package"/> 为 null。</para></exception>
    /// <exception cref="InvalidDataException">Validation reports errors (messages joined by the current line separator), or a package with assets is saved to a non-bundle path.
    /// <para>校验报错（消息按当前行分隔符拼接），或含资源的包被保存到非 bundle 路径。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before publication finished.
    /// <para>发布完成前 <paramref name="cancellationToken"/> 被取消。</para></exception>
    public static async Task SaveAsync(string path, VisualPluginPackage package,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(package);
        var errors = VisualPluginValidation.Validate(package);
        if (errors.Count > 0)
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));

        if (path.EndsWith(".uotanplugin", StringComparison.OrdinalIgnoreCase))
        {
            await VisualPluginBundle.SaveAsync(path, package, cancellationToken).ConfigureAwait(false);
            await SaveDetachedSignatureAsync(path, package, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (VisualPluginBundle.EnumerateScopes(package).Any(scope => scope.Assets.Count > 0))
        {
            throw new InvalidDataException("Packages with resources must be saved as .uotanplugin bundles.");
        }

        await AtomicJsonFile.WriteIfChangedAsync(path, package,
            VisualPluginJsonContext.Default.VisualPluginPackage, cancellationToken).ConfigureAwait(false);
        await SaveDetachedSignatureAsync(path, package, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Loads and validates one untrusted package without reading more than the package limit.
    /// <para>加载并校验一个不可信包，读取量不超过包大小上限。</para></summary>
    /// <param name="path">File to read. A file whose first four bytes are the ZIP signature is routed to <see cref="VisualPluginBundle.Load(string)"/> (synchronously, with cancellation checked around it); any other file is parsed as JSON. A sibling <c>.sig</c> file supplies the signature when the package itself is unsigned.
    /// <para>要读取的文件。前四个字节为 ZIP 签名的文件交给 <see cref="VisualPluginBundle.Load(string)"/>（同步执行，前后各检查一次取消）；其余文件按 JSON 解析。包本身未签名时由同名的 <c>.sig</c> 旁挂文件提供签名。</para></param>
    /// <param name="cancellationToken">Cancels the read; the request is checked before opening the file and again after a bundle load completes.
    /// <para>取消读取；打开文件前检查一次，bundle 加载完成后再次检查。</para></param>
    /// <returns>The validated package, fully materialized in memory with the stream closed before completion.
    /// <para>校验后的包；完成前流已关闭，内容已完整载入内存。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null, empty, or whitespace.
    /// <para><paramref name="path"/> 为 null、空白或全空格。</para></exception>
    /// <exception cref="InvalidDataException">The file exceeds <see cref="VisualPluginValidation.MaximumFileSize"/>, the JSON document is empty or structurally invalid, validation reports errors, a JSON (non-bundle) package declares assets, or the detached signature is malformed or does not match.
    /// <para>文件超过 <see cref="VisualPluginValidation.MaximumFileSize"/>、JSON 文档为空或结构非法、校验报错、非 bundle 的 JSON 包声明了资源，或分离签名格式非法或与载荷不符。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before or during the read.
    /// <para>读取前或读取过程中 <paramref name="cancellationToken"/> 被取消。</para></exception>
    public static async Task<VisualPluginPackage> LoadAsync(string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        if (IsBundle(path))
        {
            var bundle = VisualPluginBundle.Load(path);
            cancellationToken.ThrowIfCancellationRequested();
            ApplyDetachedSignature(bundle, path);
            EnsureValid(bundle, allowBundledAssets: true);
            return bundle;
        }
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        EnsureSize(stream.Length);

        VisualPluginPackage package;
        try
        {
            package = await JsonSerializer.DeserializeAsync(stream,
                VisualPluginJsonContext.Default.VisualPluginPackage, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Visual-plugin package is empty.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Visual-plugin package has an invalid or unsupported document structure.", error);
        }
        ApplyDetachedSignature(package, path);
        EnsureValid(package);
        return package;
    }

    /// <summary>
    /// Returns the detached signature path used next to a package file.
    /// <para>返回包文件旁挂分离签名所使用的路径。</para>
    /// </summary>
    /// <param name="packagePath">Package file path. <para>包文件路径。</para></param>
    /// <returns>The same path with a <c>.sig</c> suffix. <para>追加 <c>.sig</c> 后缀的同一路径。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="packagePath"/> is null, empty, or whitespace.
    /// <para><paramref name="packagePath"/> 为 null、空白或全空格。</para></exception>
    public static string DetachedSignaturePath(string packagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagePath);
        return packagePath + ".sig";
    }

    private static VisualPluginPackage LoadJson(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            65536, FileOptions.SequentialScan);
        EnsureSize(stream.Length);
        try
        {
            return JsonSerializer.Deserialize(stream, VisualPluginJsonContext.Default.VisualPluginPackage)
                ?? throw new InvalidDataException("Visual-plugin package is empty.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Visual-plugin package has an invalid or unsupported document structure.", error);
        }
    }

    private static void ApplyDetachedSignature(VisualPluginPackage package, string path)
    {
        if (package.IsSigned || package.Manifest is null)
        {
            return;
        }
        var sidecar = DetachedSignaturePath(path);
        if (!File.Exists(sidecar))
        {
            return;
        }
        var length = new FileInfo(sidecar).Length;
        if (length is <= 0 or > MaximumDetachedSignatureSize)
        {
            throw new InvalidDataException("Detached signature is empty or exceeds its size limit.");
        }
        VisualPluginSignature? signature;
        try
        {
            signature = JsonSerializer.Deserialize(File.ReadAllBytes(sidecar),
                VisualPluginJsonContext.Default.VisualPluginSignature);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Detached signature has an invalid document structure.", error);
        }
        if (signature is null || string.IsNullOrEmpty(signature.Algorithm) || string.IsNullOrEmpty(signature.Value))
        {
            throw new InvalidDataException("Detached signature is empty.");
        }
        package.Manifest.Signature = signature;
    }

    private static async Task SaveDetachedSignatureAsync(string path, VisualPluginPackage package,
        CancellationToken cancellationToken)
    {
        var sidecar = DetachedSignaturePath(path);
        if (package.Manifest?.Signature is { } signature && !string.IsNullOrEmpty(signature.Value))
        {
            await AtomicJsonFile.WriteIfChangedAsync(sidecar, signature,
                VisualPluginJsonContext.Default.VisualPluginSignature, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (File.Exists(sidecar))
        {
            File.Delete(sidecar);
        }
    }

    private static void EnsureSize(long length)
    {
        if (length > VisualPluginValidation.MaximumFileSize)
            throw new InvalidDataException($"Visual-plugin package exceeds {VisualPluginValidation.MaximumFileSize} bytes.");
    }

    private static void EnsureValid(VisualPluginPackage package, bool allowBundledAssets = false)
    {
        var errors = VisualPluginValidation.Validate(package);
        if (errors.Count > 0)
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        if (!allowBundledAssets
            && VisualPluginBundle.EnumerateScopes(package).Any(scope => scope.Assets.Count > 0))
        {
            throw new InvalidDataException("JSON packages cannot provide bundled resources.");
        }
    }

    private static bool IsBundle(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> signature = stackalloc byte[4];
        return stream.Read(signature) == signature.Length && signature.SequenceEqual("PK\u0003\u0004"u8);
    }
}

[JsonSerializable(typeof(VisualPluginPackage))]
[JsonSerializable(typeof(VisualPluginManifest))]
[JsonSerializable(typeof(VisualPluginPortDefinition))]
[JsonSerializable(typeof(VisualPluginSignature))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class VisualPluginJsonContext : JsonSerializerContext;