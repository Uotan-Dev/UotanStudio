namespace UotanStudio.Projects;

/// <summary>
/// Filesystem operations shared by project hosts; format detection and UI/history updates remain with the caller.
/// <para>项目宿主间共享的文件系统操作；格式检测与界面/历史更新由调用方负责。</para>
/// </summary>
public static class ProjectFiles
{
    /// <summary>Finds a filename using the desktop's existing underscore suffix convention.
    /// <para>按桌面端既有的下划线后缀约定查找可用文件名。</para></summary>
    /// <param name="directory">Directory checked for existing entries; it is not required to exist.
    /// <para>用于检查同名条目的目录；该目录无需存在。</para></param>
    /// <param name="fileName">Bare file name to test; it must not contain a directory component and must not be blank.
    /// <para>待测试的纯文件名；不得包含目录部分，且不能为空白。</para></param>
    /// <returns>The original name when free in the directory; otherwise the base name with <c>_N</c> inserted before the extension for the smallest <c>N</c> starting at <c>1</c> whose name matches no existing file or directory.
    /// <para>目录中未被占用时返回原名；否则对扩展名前的基名从 <c>1</c> 起依次追加 <c>_N</c>， 返回与任何现有文件或目录都不冲突的名称。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="fileName"/> includes a directory component or is blank. <paramref name="directory"/> is not validated.
    /// <para><paramref name="fileName"/> 含有目录部分或为空白；<paramref name="directory"/> 不做校验。</para></exception>
    public static string GetUniqueFileName(string directory, string fileName)
    {
        if (Path.GetFileName(fileName) != fileName || string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("A single filename is required.", nameof(fileName));
        var candidate = fileName;
        var index = 1;
        while (File.Exists(Path.Combine(directory, candidate)) || Directory.Exists(Path.Combine(directory, candidate)))
            candidate = $"{Path.GetFileNameWithoutExtension(fileName)}_{index++}{Path.GetExtension(fileName)}";
        return candidate;
    }

    /// <summary>Copies through a temporary file; cancellation or failure leaves the destination untouched.
    /// <para>经由临时文件拷贝；取消或失败时目标文件保持不变。</para></summary>
    /// <param name="sourcePath">Existing source file, opened for reading with <c>FileShare.Read</c> and asynchronous sequential access.
    /// <para>已存在的源文件，以 <c>FileShare.Read</c> 和异步顺序访问方式打开读取。</para></param>
    /// <param name="destinationPath">Target path, resolved to its full path; it is replaced only after the staged copy is complete.
    /// <para>目标路径，会解析为完整路径；仅在暂存拷贝完成后才被替换。</para></param>
    /// <param name="overwrite">Allows the final move to replace an existing destination; with <see langword="false"/> and an existing destination the move throws.
    /// <para>允许最终移动替换已存在的目标；为 <see langword="false"/> 且目标已存在时移动将抛出异常。</para></param>
    /// <param name="cancellationToken">Cancelled during the copy or before the final move; a cancelled copy never touches the destination.
    /// <para>可在拷贝期间或最终移动之前取消；被取消的拷贝绝不触及目标文件。</para></param>
    /// <param name="progress">Optional percentage sink; reports <c>0</c> at the start, throttled to at most once per 100 ms during the copy, and <c>100</c> after the move.
    /// <para>可选的百分比回调；开始时报告 <c>0</c>，拷贝期间至多每 100 毫秒一次，移动完成后报告 <c>100</c>。</para></param>
    /// <returns>Completes when the destination holds the full copy; the <c>.copy-</c> temporary file is deleted in all cases.
    /// <para>目标文件包含完整拷贝时完成；<c>.copy-</c> 临时文件在任何情况下都会被删除。</para></returns>
    /// <exception cref="FileNotFoundException">The source file does not exist.
    /// <para>源文件不存在。</para></exception>
    /// <exception cref="EndOfStreamException">The source shrank while it was being copied.
    /// <para>拷贝期间源文件被截断。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.
    /// <para>请求了取消。</para></exception>
    /// <exception cref="IOException">The destination exists while <paramref name="overwrite"/> is <see langword="false"/>, or the move fails.
    /// <para><paramref name="overwrite"/> 为 <see langword="false"/> 时目标已存在，或移动失败。</para></exception>
    public static async Task CopyAsync(string sourcePath, string destinationPath, bool overwrite = false,
        CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        var destination = Path.GetFullPath(destinationPath);
        var temporary = destination + ".copy-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                var length = source.Length;
                var buffer = new byte[65536];
                long copied = 0;
                var lastReport = Environment.TickCount64;
                progress?.Report(0);
                while (copied < length)
                {
                    var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - copied)), cancellationToken).ConfigureAwait(false);
                    if (read == 0) throw new EndOfStreamException("Source was truncated during copy.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    copied += read;
                    if (Environment.TickCount64 - lastReport >= 100)
                    {
                        progress?.Report(copied * 100.0 / length);
                        lastReport = Environment.TickCount64;
                    }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite);
            progress?.Report(100);
        }
        finally { File.Delete(temporary); }
    }

    /// <summary>Moves a file without overwriting another identity. A completed move is the commit point.
    /// <para>移动文件且不覆盖其他标识。移动完成即为提交点。</para></summary>
    /// <param name="sourcePath">File to move.<para>待移动的文件。</para></param>
    /// <param name="destinationPath">Target path that must not already exist.
    /// <para>必须尚不存在的目标路径。</para></param>
    /// <param name="cancellationToken">Checked when the thread-pool work item starts; a cancelled token prevents the move.
    /// <para>在线程池工作项启动时检查；已取消的令牌将阻止移动。</para></param>
    /// <returns>Completes when the file has been moved on a thread-pool thread.
    /// <para>文件已在线程池线程上完成移动时结束。</para></returns>
    /// <exception cref="FileNotFoundException">The source file does not exist.
    /// <para>源文件不存在。</para></exception>
    /// <exception cref="IOException">The destination path is already taken or the move fails.
    /// <para>目标路径已被占用或移动失败。</para></exception>
    /// <exception cref="OperationCanceledException">The token was already cancelled.
    /// <para>令牌已被取消。</para></exception>
    public static Task MoveAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(sourcePath, destinationPath, overwrite: false);
        }, cancellationToken);
}