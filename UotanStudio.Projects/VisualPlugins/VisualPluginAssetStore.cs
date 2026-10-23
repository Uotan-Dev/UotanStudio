using System.Globalization;
using System.Text;

namespace UotanStudio.Projects;

/// <summary>
/// Copies portable patch resources into the open project and anchors them to a project-relative path.
/// <para>把可移植补丁资源复制进当前工程，并锚定到工程相对路径。</para>
/// </summary>
/// <remarks>Copies are bounded by <see cref="VisualPluginBundle.MaximumAssetSize"/>, stream through SHA-256,
/// publish through a sibling temporary file, and never overwrite an existing file whose content differs.
/// <para>复制受 <see cref="VisualPluginBundle.MaximumAssetSize"/> 约束，经 SHA-256 流式处理，通过同级临时文件发布，
/// 且绝不覆盖内容不同的既有文件。</para></remarks>
public static class VisualPluginAssetStore
{
    /// <summary>Project-relative directory holding copied plugin resources, using <c>/</c> separators.
    /// <para>存放已复制插件资源的工程相对目录，使用 <c>/</c> 分隔。</para>
    /// </summary>
    public const string RelativeDirectory = ".uotan/plugin-assets";

    private const int BufferSize = 131072;
    private const int MaximumFileNameLength = 255;
    private const int MaximumPublishAttempts = 128;

    /// <summary>Returns the project directory that stores one plugin's resources.
    /// <para>返回存放单个插件资源的工程目录。</para>
    /// </summary>
    /// <param name="projectRoot">Project root directory; it is resolved to a full path.
    /// <para>工程根目录；会被解析为完整路径。</para></param>
    /// <param name="pluginId">Package identifier used as the directory segment; lowercase ASCII letters, digits,
    /// dots, underscores and hyphens are accepted.
    /// <para>用作目录名的包标识；仅接受小写 ASCII 字母、数字、点、下划线和连字符。</para></param>
    /// <returns>The full path <c>&lt;projectRoot&gt;/.uotan/plugin-assets/&lt;pluginId&gt;</c>.
    /// <para>完整路径 <c>&lt;projectRoot&gt;/.uotan/plugin-assets/&lt;pluginId&gt;</c>。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="projectRoot"/> or <paramref name="pluginId"/> is empty,
    /// or <paramref name="pluginId"/> is not a safe lowercase directory segment.
    /// <para><paramref name="projectRoot"/> 或 <paramref name="pluginId"/> 为空，或 <paramref name="pluginId"/>
    /// 不是安全的小写目录段。</para></exception>
    public static string ResolveDirectory(string projectRoot, string pluginId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        if (!IsSafePluginId(pluginId))
        {
            throw new ArgumentException(
                "Plugin id must use lowercase ASCII letters, digits, dots, underscores or hyphens.", nameof(pluginId));
        }
        return Path.Combine(Path.GetFullPath(projectRoot), ".uotan", "plugin-assets", pluginId);
    }

    /// <summary>Copies one resource into the project, computes its SHA-256, and anchors it to the copy.
    /// <para>把一个资源复制进工程、计算其 SHA-256，并锚定到该副本。</para>
    /// </summary>
    /// <param name="id">Portable resource identifier stored on the returned asset as-is.
    /// <para>存入返回资源的可移植资源标识符，原样保存。</para></param>
    /// <param name="sourcePath">File to copy; it is opened read-shared and streamed with bounded memory.
    /// <para>要复制的文件；以共享读打开并使用有界内存流式处理。</para></param>
    /// <param name="projectRoot">Project root directory receiving the copy.
    /// <para>接收副本的工程根目录。</para></param>
    /// <param name="pluginId">Package identifier selecting the resource directory.
    /// <para>选择资源目录的包标识。</para></param>
    /// <param name="cancellationToken">Cancels the copy and publication; a cancelled or failed copy leaves no
    /// partial destination and removes its temporary file.
    /// <para>取消复制与发布；取消或失败不会留下部分目标文件，并会删除其临时文件。</para></param>
    /// <returns>The copied resource with <see cref="VisualPluginAsset.SourcePath"/> set to the project copy and
    /// <see cref="VisualPluginAsset.RelativePath"/> set to its root-relative <c>/</c>-separated anchor. When an existing
    /// file with the same name already holds identical bytes, that file is reused instead of overwritten.
    /// <para>已复制的资源：<see cref="VisualPluginAsset.SourcePath"/> 指向工程副本，
    /// <see cref="VisualPluginAsset.RelativePath"/> 为其相对根目录、以 <c>/</c> 分隔的锚点。若同名既有文件已保存完全相同的
    /// 字节，则复用该文件而不覆盖。</para></returns>
    /// <exception cref="ArgumentException">An argument is empty, the resource or plugin id is unsafe, the source has
    /// no usable file name, or the published copy would not lie inside the project root.
    /// <para>参数为空、资源或插件标识不安全、源文件没有可用文件名，或发布后的副本不在工程根目录之内。</para></exception>
    /// <exception cref="InvalidDataException">The source exceeds the per-resource size limit or changed while copying.
    /// <para>源超过单资源大小上限，或在复制过程中发生变化。</para></exception>
    /// <exception cref="IOException">The source cannot be read, the temporary file cannot be written, or no unique
    /// destination name could be allocated.
    /// <para>源无法读取、临时文件无法写入，或无法分配唯一的目标文件名。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    public static async Task<VisualPluginAsset> CopyIntoProjectAsync(string id, string sourcePath,
        string projectRoot, string pluginId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        if (!IsSafeResourceId(id))
        {
            throw new ArgumentException(
                "Resource id must start with an ASCII letter or digit and contain at most 128 letters, digits, dots, underscores or hyphens.",
                nameof(id));
        }
        var sourceFullPath = Path.GetFullPath(sourcePath);
        var directory = ResolveDirectory(projectRoot, pluginId);
        Directory.CreateDirectory(directory);
        var storedName = CreateStoredFileName(sourceFullPath);
        var copy = await CopyToTemporaryAsync(sourceFullPath, directory, cancellationToken).ConfigureAwait(false);
        string destination;
        try
        {
            destination = await PublishAsync(directory, storedName, copy.Sha256, copy.Temporary, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            TryDelete(copy.Temporary);
            throw;
        }

        var identity = new VisualPluginAsset
        {
            Id = id,
            Name = storedName,
            Length = copy.Length,
            Sha256 = copy.Sha256
        };
        return VisualPluginAsset.WithProjectLocation(identity, destination, projectRoot);
    }

    private static async Task<CopyResult> CopyToTemporaryAsync(string sourcePath, string directory,
        CancellationToken cancellationToken)
    {
        var temporary = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (source.Length > VisualPluginBundle.MaximumAssetSize)
            {
                throw new InvalidDataException("Patch resource exceeds the per-resource size limit.");
            }
            var length = source.Length;
            using var hash = Hash.Digests.Sha256.CreateAccumulator();
            var buffer = new byte[BufferSize];
            var remaining = length;
            await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, BufferSize, FileOptions.Asynchronous))
            {
                while (remaining > 0)
                {
                    var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                        cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        throw new InvalidDataException("Patch resource changed while copying it.");
                    }
                    hash.Append(buffer.AsSpan(0, read));
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    remaining -= read;
                }
                if (await source.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0)
                {
                    throw new InvalidDataException("Patch resource changed while copying it.");
                }
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
            }
            return new CopyResult(length, Convert.ToHexStringLower(hash.GetHashAndReset()), temporary);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static async Task<string> PublishAsync(string directory, string fileName, string sha256,
        string temporary, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaximumPublishAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = Path.Combine(directory, CandidateName(fileName, sha256, attempt));
            if (File.Exists(candidate))
            {
                if (string.Equals(await ComputeHashAsync(candidate, cancellationToken).ConfigureAwait(false), sha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(temporary);
                    return candidate;
                }
                continue;
            }
            try
            {
                File.Move(temporary, candidate, overwrite: false);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
                continue;
            }
        }
        throw new IOException("Could not allocate a unique file name for the project resource.");
    }

    private static string CandidateName(string fileName, string sha256, int attempt)
    {
        if (attempt == 0)
        {
            return fileName;
        }
        var suffix = "-" + sha256[..8];
        if (attempt > 1)
        {
            suffix += "-" + (attempt - 1).ToString(CultureInfo.InvariantCulture);
        }
        var extension = Path.GetExtension(fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        return (stem.Length == 0 ? fileName : stem) + suffix + extension;
    }

    private static async Task<string?> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > VisualPluginBundle.MaximumAssetSize)
            {
                return null;
            }
            using var hash = Hash.Digests.Sha256.CreateAccumulator();
            var buffer = new byte[BufferSize];
            var remaining = stream.Length;
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return null;
                }
                hash.Append(buffer.AsSpan(0, read));
                remaining -= read;
            }
            if (await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0)
            {
                return null;
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string CreateStoredFileName(string sourcePath)
    {
        var name = Path.GetFileName(sourcePath);
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
        {
            throw new ArgumentException("The source path has no usable file name.", nameof(sourcePath));
        }
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            builder.Append(Array.IndexOf(invalid, character) >= 0 || character is '/' or '\\' or ':'
                ? '_'
                : character);
        }
        var stored = builder.ToString();
        if (stored is "." or "..")
        {
            stored = "_" + stored;
        }
        if (stored.Length <= MaximumFileNameLength)
        {
            return stored;
        }
        var extension = Path.GetExtension(stored);
        if (extension.Length > 32)
        {
            extension = string.Empty;
        }
        var stemLength = Math.Max(1, MaximumFileNameLength - extension.Length);
        return stored[..Math.Min(stemLength, stored.Length)] + extension;
    }

    private static bool IsSafeResourceId(string value)
    {
        if (value.Length is < 1 or > 128 || !char.IsAsciiLetterOrDigit(value[0]))
        {
            return false;
        }
        foreach (var character in value)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsSafePluginId(string value)
    {
        if (value.Length is < 3 or > 128
            || !(char.IsAsciiLetterLower(value[0]) || char.IsAsciiDigit(value[0])))
        {
            return false;
        }
        foreach (var character in value)
        {
            if (!(char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character)
                  || character is '.' or '_' or '-'))
            {
                return false;
            }
        }
        return true;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private readonly record struct CopyResult(long Length, string Sha256, string Temporary);
}