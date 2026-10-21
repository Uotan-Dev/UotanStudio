using System.Text.Json;

namespace UotanStudio.Services.Projects.Schemes;

/// <summary>
/// Persists scheme packages through a bounded read and an atomic temporary file.
/// <para>通过有界读取与原子临时文件持久化 Scheme 软件包。</para>
/// </summary>
public static class SchemeFile
{
    /// <summary>
    /// Maximum serialized scheme size, matching the project persistence bound.
    /// <para>序列化 Scheme 的最大尺寸，与工程持久化上限一致。</para>
    /// </summary>
    public const int MaximumBytes = 32 * 1024 * 1024;

    /// <summary>
    /// Saves a scheme package, rewriting the file only when its text changes.
    /// <para>保存 Scheme 软件包，仅在文本发生变化时才重写文件。</para>
    /// </summary>
    /// <param name="path">Destination scheme file; an empty value aborts the save without touching disk.
    /// <para>目标 Scheme 文件；为空时直接放弃保存，不产生任何磁盘操作。</para></param>
    /// <param name="package">Package to serialize; its format version must be readable by this implementation.
    /// <para>待序列化的软件包；其格式版本必须能被本实现读取。</para></param>
    /// <param name="cancellationToken">Cancels staging or comparison I/O; the destination keeps its previous content.
    /// <para>取消暂存或比对阶段的 I/O；目标文件保持原有内容。</para></param>
    /// <returns><see langword="true"/> when the destination holds text equivalent to the package; <see langword="false"/> when the path is empty, the version is unsupported, the serialized state exceeds 32 MiB, or any I/O error occurred.
    /// <para>目标文件内容与软件包等价时返回 <see langword="true"/>；路径为空、版本不受支持、序列化结果超过 32 MiB 或发生任何 I/O 错误时返回 <see langword="false"/>。</para></returns>
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
            await AtomicJsonFile.WriteIfChangedAsync(path, package, SchemeJsonContext.Default.SchemePackage, cancellationToken).ConfigureAwait(false);
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
    /// Loads a scheme package leniently: unknown members are ignored and missing optional members are repaired.
    /// <para>宽容读取 Scheme 软件包：忽略未知成员并修复缺失的可选成员。</para>
    /// </summary>
    /// <param name="path">Scheme file to read; <see langword="null"/> is returned when the path is empty or the file does not exist.
    /// <para>要读取的 Scheme 文件；路径为空或文件不存在时返回 <see langword="null"/>。</para></param>
    /// <param name="cancellationToken">Cancels the streamed read; the exception propagates to the caller.
    /// <para>取消流式读取；异常会继续抛出给调用方。</para></param>
    /// <returns>The normalized package, or <see langword="null"/> when the file is missing, larger than 32 MiB, not valid scheme JSON, or stored with an unsupported format version.
    /// <para>规范化后的软件包；文件缺失、超过 32 MiB、不是有效的 Scheme JSON，或存储了不受支持的格式版本时返回 <see langword="null"/>。</para></returns>
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
}