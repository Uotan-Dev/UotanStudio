using System.Text.Json;

namespace UotanStudio.Projects;

/// <summary>
/// Persists workflow data without a node editor or node factory.
/// <para>在没有节点编辑器或节点工厂的情况下持久化工作流数据。</para>
/// </summary>
public static class WorkflowFile
{
    /// <summary>Saves an unchanged-format workflow through an atomic temporary file.
    /// <para>通过原子临时文件保存格式版本未变更的工作流。</para></summary>
    /// <param name="path">Destination workflow file; an empty or <see langword="null"/> value aborts the save without touching disk.
    /// <para>目标工作流文件；为 <see langword="null"/> 或空字符串时直接放弃保存，不产生任何磁盘操作。</para></param>
    /// <param name="snapshot">Workflow to serialize; it must pass <see cref="WorkflowSnapshot.EnsureSupportedVersion"/> before anything is written.
    /// <para>待序列化的工作流；写入任何内容之前必须先通过 <see cref="WorkflowSnapshot.EnsureSupportedVersion"/> 校验。</para></param>
    /// <param name="cancellationToken">Cancels staging or comparison I/O; the destination keeps its previous content.
    /// <para>取消暂存或比对阶段的 I/O；目标文件保持原有内容。</para></param>
    /// <returns><see langword="true"/> when the destination holds text equivalent to the snapshot, whether freshly written through the staged temporary file or already identical; <see langword="false"/> when the path is empty, the contract is unsupported, the serialized state exceeds 32 MiB, or any I/O error occurred.
    /// <para>当目标文件内容与快照等价时返回 <see langword="true"/>，无论是由暂存临时文件原子替换写入，还是文本本来就完全一致；当路径为空、契约不受支持、序列化结果超过 32 MiB 或发生任何 I/O 错误时返回 <see langword="false"/>。</para></returns>
    /// <exception cref="OperationCanceledException">Cancellation was requested before the destination was replaced; the temporary file is deleted and the previous content is kept.
    /// <para>在替换目标文件之前请求了取消；临时文件被删除，原有内容得以保留。</para></exception>
    public static async Task<bool> SaveAsync(string path, WorkflowSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            snapshot.EnsureSupportedVersion();
            await AtomicJsonFile.WriteIfChangedAsync(path, snapshot, WorkflowJsonContext.Default.WorkflowSnapshot, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ProjectDiagnostics.Write(ProjectLogLevel.Error, "Persistence", ex.Message);
            return false;
        }
    }

    /// <summary>Loads workflow data; missing or invalid files return null, cancellation propagates.
    /// <para>加载工作流数据；文件缺失或无效时返回 null，取消信号会继续抛出。</para></summary>
    /// <param name="path">Workflow file to read; <see langword="null"/> is returned when the path is empty or the file does not exist.
    /// <para>要读取的工作流文件；路径为空或文件不存在时返回 <see langword="null"/>。</para></param>
    /// <param name="cancellationToken">Cancels the streamed read; the exception propagates to the caller.
    /// <para>取消流式读取；异常会继续抛出给调用方。</para></param>
    /// <returns>The snapshot deserialized from a stream opened with shared read access, or <see langword="null"/> when the file is missing, unreadable, larger than 32 MiB, not valid workflow JSON, or rejected by <see cref="WorkflowSnapshot.EnsureSupportedVersion"/>. Failures are logged through <see cref="ProjectDiagnostics"/> and never surface as exceptions.
    /// <para>由共享读取文件流反序列化得到的快照；当文件缺失、不可读、超过 32 MiB、不是有效工作流 JSON，或被 <see cref="WorkflowSnapshot.EnsureSupportedVersion"/> 拒绝时返回 <see langword="null"/>。失败会经 <see cref="ProjectDiagnostics"/> 记录，不会以异常形式抛出。</para></returns>
    /// <exception cref="OperationCanceledException">Cancellation was requested while the file was being read or parsed.
    /// <para>在读取或解析文件期间请求了取消。</para></exception>
    public static async Task<WorkflowSnapshot?> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > 32 * 1024 * 1024) throw new InvalidDataException("Project state exceeds 32 MiB.");
            var snapshot = await JsonSerializer.DeserializeAsync(stream, WorkflowJsonContext.Default.WorkflowSnapshot, cancellationToken).ConfigureAwait(false);
            snapshot?.EnsureSupportedVersion();
            return snapshot;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ProjectDiagnostics.Write(ProjectLogLevel.Error, "Persistence", ex.Message);
            return null;
        }
    }
}