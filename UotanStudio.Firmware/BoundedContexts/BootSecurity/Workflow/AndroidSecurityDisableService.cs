using System.Text;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb.Signing;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.Workflow;

/// <summary>
/// Explicit inputs for a combined fstab and vbmeta verification-disable transaction.
/// <para>fstab 与 vbmeta 校验禁用组合事务的显式输入。</para>
/// </summary>
public sealed record AndroidSecurityDisableRequest
{
    /// <summary>
    /// fstab files to patch, each resolved to a full path before reading.
    /// <para>待修补的 fstab 文件，读取前逐个解析为完整路径。</para>
    /// </summary>
    /// <value>
    /// Defaults to an empty list, meaning only the vbmeta step can run.
    /// <para>默认为空列表，即仅能执行 vbmeta 步骤。</para>
    /// </value>
    public IReadOnlyList<string> FstabPaths { get; init; } = [];
    /// <summary>
    /// VBMeta image consumed when verification disabling is enabled.
    /// <para>启用校验禁用时所消费的 VBMeta 镜像。</para>
    /// </summary>
    /// <value>
    /// Required when <see cref="DisableVbmetaVerification"/> is <see langword="true"/>;
    /// otherwise ignored, <c>null</c> by default.
    /// <para>当 <see cref="DisableVbmetaVerification"/> 为 <see langword="true"/> 时必填；否则被忽略，默认 <c>null</c>。</para>
    /// </value>
    public string? VbmetaPath { get; init; }
    /// <summary>
    /// Directory that receives the published outputs; created when missing.
    /// <para>接收已发布输出的目录；不存在时会被创建。</para>
    /// </summary>
    /// <value>Required, non-whitespace; relative paths are resolved against the process working directory.
    /// <para>必填且非空白；相对路径按进程工作目录解析。</para></value>
    public required string OutputDirectory { get; init; }
    /// <summary>
    /// Enables verity flag removal in the fstab patch step.
    /// <para>启用 fstab 修补步骤中的 verity 标志移除。</para>
    /// </summary>
    /// <value>Defaults to <see langword="true"/>. <para>默认为 <see langword="true"/>。</para></value>
    public bool DisableVerity { get; init; } = true;
    /// <summary>
    /// Enables force-encryption option removal in the fstab patch step.
    /// <para>启用 fstab 修补步骤中的强制加密选项移除。</para>
    /// </summary>
    /// <value>Defaults to <see langword="true"/>. <para>默认为 <see langword="true"/>。</para></value>
    public bool DisableForceEncryption { get; init; } = true;
    /// <summary>Enables the VBMeta verification-disable step. <para>启用 VBMeta 校验禁用步骤。</para></summary>
    /// <value>
    /// Defaults to <see langword="true"/>; requires <see cref="VbmetaPath"/>.
    /// <para>默认为 <see langword="true"/>；需要 <see cref="VbmetaPath"/>。</para>
    /// </value>
    public bool DisableVbmetaVerification { get; init; } = true;
}

/// <summary>
/// Outputs published by <see cref="AndroidSecurityDisableService"/>.
/// <para>由 <see cref="AndroidSecurityDisableService"/> 发布的输出。</para>
/// </summary>
/// <param name="OutputPaths">
/// Absolute paths of every staged-and-published output file.
/// <para>每个经暂存并发布的输出文件绝对路径。</para>
/// </param>
/// <param name="Changes">Per-file descriptions of the changes actually applied. <para>对实际已应用变更的逐文件描述。</para></param>
public sealed record AndroidSecurityDisableResult(IReadOnlyList<string> OutputPaths, IReadOnlyList<string> Changes);

/// <summary>
/// Stages and publishes a combined fstab/vbmeta verification-disable operation as one transaction.
/// <para>以单一事务方式暂存并发布 fstab/vbmeta 校验禁用组合操作。</para>
/// </summary>
public static class AndroidSecurityDisableService
{
    private const int MaximumFstabBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Patches each fstab, optionally disables VBMeta verification, and atomically publishes all results
    /// from a staging directory, restoring prior outputs when any publication step fails.
    /// <para>逐个修补 fstab，可选地禁用 VBMeta 校验，并从暂存目录原子化发布全部结果；任一发布步骤失败时恢复原有输出。</para>
    /// </summary>
    /// <param name="request">
    /// Inputs describing the fstab files, optional vbmeta image, output directory and enabled steps.
    /// <para>描述 fstab 文件、可选 vbmeta 镜像、输出目录与启用步骤的输入。</para>
    /// </param>
    /// <param name="cancellationToken">Checked before each file and before each publish move; a canceled run never
    /// overwrites outputs that were not already rolled back.
    /// <para>在每个文件处理前与每次发布移动前检查；被取消的运行不会留下未回滚的覆盖。</para></param>
    /// <returns>Absolute output paths in request order plus change descriptions; unchanged fstab files add no entry.
    /// <para>按请求顺序返回的输出绝对路径及变更描述；内容未变的 fstab 文件不产生条目。</para></returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="request"/> is <see langword="null"/>.
    /// <para><paramref name="request"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="ArgumentException">The output directory is blank, no security change is requested, or
    /// verification disabling is enabled without a vbmeta path.
    /// <para>输出目录为空白、未请求任何安全变更，或启用校验禁用却未提供 vbmeta 路径。</para></exception>
    /// <exception cref="FileNotFoundException">
    /// An fstab or vbmeta input file does not exist.
    /// <para>某个 fstab 或 vbmeta 输入文件不存在。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">An fstab exceeds 4 MiB, output file names collide, or the VBMeta
    /// rewrite reported failure.
    /// <para>某个 fstab 超过 4 MiB、输出文件名冲突，或 VBMeta 重写报告失败。</para></exception>
    /// <exception cref="OperationCanceledException">The token was canceled. <para>令牌被取消。</para></exception>
    /// <exception cref="AggregateException">Publication failed and rolling back already-published files also failed.
    /// <para>发布失败且回滚已发布文件同样失败。</para></exception>
    public static async Task<AndroidSecurityDisableResult> ApplyAsync(
        AndroidSecurityDisableRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputDirectory);
        if (!request.DisableVerity && !request.DisableForceEncryption && !request.DisableVbmetaVerification)
        {
            throw new ArgumentException("At least one security change must be requested.", nameof(request));
        }

        var outputRoot = Path.GetFullPath(request.OutputDirectory);
        Directory.CreateDirectory(outputRoot);
        var transactionId = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(outputRoot, $".security-disable-{transactionId}.staging");
        var backup = Path.Combine(outputRoot, $".security-disable-{transactionId}.backup");
        Directory.CreateDirectory(staging);
        var staged = new List<(string Staged, string Target)>();
        var changes = new List<string>();
        var names = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        try
        {
            foreach (var fstabPath in request.FstabPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = Path.GetFullPath(fstabPath);
                if (!File.Exists(source))
                {
                    throw new FileNotFoundException("Fstab input was not found.", source);
                }
                var file = new FileInfo(source);
                if (file.Length > MaximumFstabBytes)
                {
                    throw new InvalidDataException($"Fstab input exceeds {MaximumFstabBytes} bytes: '{source}'.");
                }
                var name = Path.GetFileName(source);
                if (!names.Add(name))
                {
                    throw new InvalidDataException($"Duplicate output filename '{name}'.");
                }
                var text = await File.ReadAllTextAsync(source, cancellationToken).ConfigureAwait(false);
                var updated = FstabVerificationEditor.Patch(text, request.DisableVerity, request.DisableForceEncryption);
                var stagedPath = Path.Combine(staging, name);
                await File.WriteAllTextAsync(stagedPath, updated, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                staged.Add((stagedPath, Path.Combine(outputRoot, name)));
                if (!string.Equals(text, updated, StringComparison.Ordinal))
                {
                    changes.Add($"Patched {name}.");
                }
            }

            if (request.DisableVbmetaVerification)
            {
                if (string.IsNullOrWhiteSpace(request.VbmetaPath))
                {
                    throw new ArgumentException("A vbmeta image is required when verification disabling is enabled.", nameof(request));
                }
                var source = Path.GetFullPath(request.VbmetaPath);
                if (!File.Exists(source))
                {
                    throw new FileNotFoundException("VBMeta input was not found.", source);
                }
                var name = Path.GetFileName(source);
                if (!names.Add(name))
                {
                    throw new InvalidDataException($"Duplicate output filename '{name}'.");
                }
                var stagedPath = Path.Combine(staging, name);
                var result = await AvbSigner.DisableVbmetaVerificationAsync(source, stagedPath, cancellationToken)
                    .ConfigureAwait(false);
                if (!result.Success)
                {
                    throw new InvalidDataException(result.ErrorMessage ?? "Failed to disable VBMeta verification.");
                }
                staged.Add((stagedPath, Path.Combine(outputRoot, name)));
                changes.Add($"Disabled verification in {name}.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            Publish(staged, backup, cancellationToken);
            return new(staged.Select(item => item.Target).ToArray(), changes);
        }
        finally
        {
            TryDeleteDirectory(staging);
            TryDeleteEmptyDirectory(backup);
        }
    }

    private static void Publish(IReadOnlyList<(string Staged, string Target)> files, string backupDirectory,
        CancellationToken cancellationToken)
    {
        var published = new List<(string Target, string? Backup)>();
        try
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? saved = null;
                if (File.Exists(file.Target))
                {
                    Directory.CreateDirectory(backupDirectory);
                    saved = Path.Combine(backupDirectory, Path.GetFileName(file.Target));
                    File.Move(file.Target, saved, overwrite: false);
                }
                try
                {
                    File.Move(file.Staged, file.Target, overwrite: false);
                }
                catch
                {
                    if (saved is not null)
                    {
                        File.Move(saved, file.Target, overwrite: false);
                    }
                    throw;
                }
                published.Add((file.Target, saved));
            }
            TryDeleteDirectory(backupDirectory);
        }
        catch (Exception publicationError)
        {
            List<Exception>? rollbackErrors = null;
            for (var i = published.Count - 1; i >= 0; i--)
            {
                try
                {
                    File.Delete(published[i].Target);
                    if (published[i].Backup is { } saved)
                    {
                        File.Move(saved, published[i].Target, overwrite: false);
                    }
                }
                catch (Exception rollbackError)
                {
                    rollbackErrors ??= [];
                    rollbackErrors.Add(rollbackError);
                }
            }
            if (rollbackErrors is not null)
            {
                rollbackErrors.Insert(0, publicationError);
                throw new AggregateException("Security outputs failed to publish and rollback was incomplete.", rollbackErrors);
            }
            throw;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch
        {
        }
    }
}