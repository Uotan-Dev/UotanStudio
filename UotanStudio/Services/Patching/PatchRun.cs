using UotanStudio.Hash.Digests;

namespace UotanStudio.Services.Patching;

/// <summary>Shared staging for a graph and its composites. Files publish only after the root graph succeeds.<para>为节点图及其复合体提供共用暂存。只有根节点图成功后文件才发布。</para></summary>
/// <param name="preview">When true, staged data is discarded instead of published.<para>为 true 时暂存数据被丢弃而不发布。</para></param>
public sealed partial class PatchRun(bool preview = false) : IAsyncDisposable
{
    private static readonly SemaphoreSlim PublicationGate = new(1, 1);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, PendingFile> _files = new(PatchPath.Comparer);
    private readonly Dictionary<string, HashSet<int>> _stagingInvocations = new(PatchPath.Comparer);
    private int _stagingAttributions;
    private readonly Dictionary<string, FileIdentity> _reads = new(PatchPath.Comparer);
    private readonly List<HashSet<string>> _expectedChanges = [];
    private readonly string _staging = Path.Combine(Path.GetTempPath(), "uotan-patch-" + Guid.NewGuid().ToString("N"));
    private bool _finished;
    private bool _disposed;
    private bool _hadConflict;

    /// <summary>Whether the root run must discard staged data instead of publishing it.<para>根执行是否必须丢弃暂存数据而不发布。</para></summary>
    /// <value>True for a preview-only run.<para>仅预览的运行为 true。</para></value>
    public bool Preview { get; } = preview;

    /// <summary>Current publication outcome.<para>当前的发布结果。</para></summary>
    /// <value>The outcome recorded for this execution.<para>为该执行记录的结果。</para></value>
    public PatchRunOutcome Outcome { get; private set; }

    /// <summary>Finalized file effects, captured once before staging cleanup.<para>已定稿的文件效果，在暂存清理前一次性捕获。</para></summary>
    /// <value>The captured change list.<para>捕获到的变更列表。</para></value>
    public IReadOnlyList<PatchChange> Changes { get; private set; } = [];

    /// <summary>Creates a directory input tied to this execution.<para>创建绑定到该执行的目录输入。</para></summary>
    /// <param name="root">The directory selected for the workspace.<para>为工作区选择的目录。</para></param>
    /// <returns>The confined workspace for this execution.<para>该执行的受限工作区。</para></returns>
    public PatchWorkspace OpenWorkspace(string root)
    {
        CheckOpen();
        return new PatchWorkspace(this, root);
    }

    /// <summary>Checks the staged view, including newly added and deleted files.<para>检查暂存视图，包括新添加与已删除的文件。</para></summary>
    /// <param name="path">The confined path to check.<para>要检查的受限路径。</para></param>
    /// <param name="token">The cancellation token.<para>取消令牌。</para></param>
    /// <returns>True when the staged view reports the path as an existing file.<para>暂存视图报告该路径为已存在的文件时为 true。</para></returns>
    public async Task<bool> ExistsAsync(string path, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            CheckOpen();
            path = NormalizeDirectory(path);
            if (ObserveDirectory(path) == PathKind.Directory)
            {
                return false;
            }
            return Current(path, await OriginalAsync(path, token).ConfigureAwait(false)).Exists;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reads original or staged text, recording the original dependency for conflict checks.<para>读取原始或暂存文本，并记录原始依赖用于冲突检查。</para></summary>
    /// <param name="path">The confined path to read.<para>要读取的受限路径。</para></param>
    /// <param name="token">The cancellation token.<para>取消令牌。</para></param>
    /// <returns>The decoded text document with its observed hash.<para>带观测哈希的解码文本文档。</para></returns>
    public async Task<PatchTextDocument> ReadTextAsync(string path, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            CheckOpen();
            path = Normalize(path);
            var readPath = await ResolveReadAsync(path, token).ConfigureAwait(false);
            var document = await PatchFileService.ReadAsync(readPath, token).ConfigureAwait(false);
            if (!_files.ContainsKey(path) && _reads[path].Sha256 != document.Sha256)
            {
                throw new PatchConflictException(path);
            }
            return document;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Hashes a file without buffering it and includes it in the read dependency set.<para>不对文件整体缓冲地计算哈希，并将其纳入读取依赖集合。</para></summary>
    /// <param name="path">The confined path to hash.<para>要计算哈希的受限路径。</para></param>
    /// <param name="token">The cancellation token.<para>取消令牌。</para></param>
    /// <returns>The SHA-256 hexadecimal digest of the file.<para>该文件的 SHA-256 十六进制摘要。</para></returns>
    public async Task<string> HashAsync(string path, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            CheckOpen();
            path = Normalize(path);
            var readPath = await ResolveReadAsync(path, token).ConfigureAwait(false);
            return (await IdentifyAsync(readPath, token).ConfigureAwait(false)).Sha256
                ?? throw new FileNotFoundException("Patch input is missing.", path);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Stages encoded configuration, retaining a bounded before/after preview.<para>暂存编码后的配置，并保留有界的变更前后可视内容。</para></summary>
    /// <param name="path">The confined target path.<para>受限的目标路径。</para></param>
    /// <param name="text">The configuration text to encode.<para>要编码的配置文本。</para></param>
    /// <param name="encoding">The encoding name for the text.<para>文本的编码名称。</para></param>
    /// <param name="mode">The required file-mode precondition.<para>要求的文件模式前置条件。</para></param>
    /// <param name="token">The cancellation token.<para>取消令牌。</para></param>
    public async Task StageTextAsync(string path, string text, string encoding, PatchFileMode mode, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            CheckOpen();
            path = Normalize(path);
            var original = await OriginalAsync(path, token).ConfigureAwait(false);
            var current = Current(path, original);
            ValidateMode(path, mode, current);
            var temporary = NewStagingPath();
            await PatchFileService.WriteNewAsync(temporary, text, encoding, token).ConfigureAwait(false);
            var after = await IdentifyAsync(temporary, token).ConfigureAwait(false);
            string? beforePreview = null;
            if (original.Exists && original.Length <= 16 * 1024 * 1024)
            {
                try
                {
                    var before = await PatchFileService.ReadAsync(path, token).ConfigureAwait(false);
                    if (before.Sha256 != original.Sha256) { throw new PatchConflictException(path); }
                    beforePreview = PreviewText(before.Text);
                }
                catch (Exception error) when (error is System.Text.DecoderFallbackException or InvalidDataException) { }
            }
            SetPending(new PendingFile(path, original, after, temporary, beforePreview, PreviewText(text)));
        }
        finally { _gate.Release(); }
    }

    /// <summary>Stages a bounded-stream copy from an original or staged source.<para>从原始或已暂存的源暂存有界流拷贝。</para></summary>
    /// <param name="source">The confined source path to read.<para>要读取的受限源路径。</para></param>
    /// <param name="target">The confined destination path.<para>受限的目标路径。</para></param>
    /// <param name="mode">The required file-mode precondition.<para>要求的文件模式前置条件。</para></param>
    /// <param name="expectedSourceHash">The SHA-256 guard for the source bytes.<para>源字节的 SHA-256 守卫。</para></param>
    /// <param name="token">The cancellation token.<para>取消令牌。</para></param>
    public async Task StageCopyAsync(string source, string target, PatchFileMode mode, string expectedSourceHash, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            CheckOpen();
            source = Normalize(source);
            target = Normalize(target);
            var readPath = await ResolveReadAsync(source, token).ConfigureAwait(false);
            var sourceIdentity = await IdentifyAsync(readPath, token).ConfigureAwait(false);
            ValidateHash(sourceIdentity.Sha256, expectedSourceHash, source);
            var original = await OriginalAsync(target, token).ConfigureAwait(false);
            ValidateMode(target, mode, Current(target, original));
            var temporary = NewStagingPath();
            await CopyExactAsync(readPath, temporary, sourceIdentity.Length, token).ConfigureAwait(false);
            var copied = await IdentifyAsync(temporary, token).ConfigureAwait(false);
            if (copied.Sha256 != sourceIdentity.Sha256) { throw new PatchConflictException(source); }
            SetPending(new PendingFile(target, original, copied, temporary));
        }
        finally { _gate.Release(); }
    }

    /// <summary>Stages removal. Missing files are already applied unless an old hash is required.<para>暂存删除操作。除非要求旧哈希校验，缺失文件视为已应用。</para></summary>
    /// <param name="path">The confined path to delete.<para>要删除的受限路径。</para></param>
    /// <param name="expectedHash">The SHA-256 guard for the current bytes, or empty to skip it.<para>当前字节的 SHA-256 守卫；为空则跳过校验。</para></param>
    /// <param name="token">The cancellation token.<para>取消令牌。</para></param>
    public async Task StageDeleteAsync(string path, string expectedHash, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            CheckOpen();
            path = Normalize(path);
            var original = await OriginalAsync(path, token).ConfigureAwait(false);
            ValidateHash(Current(path, original).Sha256, expectedHash, path);
            SetPending(new PendingFile(path, original, FileIdentity.Missing, null));
        }
        finally { _gate.Release(); }
    }

    /// <summary>Validates that the graph changed only the explicitly listed relative paths.<para>校验节点图只改动了显式列出的相对路径。</para></summary>
    /// <param name="workspace">The workspace whose root confines the listed paths.<para>其根目录约束所列路径的工作区。</para></param>
    /// <param name="expectedPaths">The relative paths allowed to change.<para>允许发生改动的相对路径。</para></param>
    /// <param name="token">The cancellation token.<para>取消令牌。</para></param>
    public async Task VerifyChangesAsync(PatchWorkspace workspace, IReadOnlyCollection<string> expectedPaths, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            CheckWorkspace(workspace);
            var expected = expectedPaths.Select(workspace.Resolve).ToHashSet(PatchPath.Comparer);
            var actual = _files.Values.Where(file => file.Before.Sha256 != file.After.Sha256)
                .Select(file => file.Path).ToHashSet(PatchPath.Comparer);
            if (!actual.SetEquals(expected))
            {
                throw new InvalidDataException("Staged changes do not match the expected file set.");
            }
            _expectedChanges.Add(expected);
        }
        finally { _gate.Release(); }
    }

    internal void CheckWorkspace(PatchWorkspace workspace)
    {
        CheckOpen();
        if (!ReferenceEquals(workspace.Run, this))
        {
            throw new InvalidOperationException("Patch workspace belongs to another execution.");
        }
    }

    internal void RecordFailure(Exception error)
    {
        if (error is PatchConflictException or PatchApplicabilityConflictException)
        {
            Volatile.Write(ref _hadConflict, true);
            Outcome = PatchRunOutcome.Conflict;
        }
    }

    internal async Task CompleteAsync(bool success, CancellationToken token)
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            CheckOpen();
            _finished = true;
            RefreshChanges();
            if (!success)
            {
                if (Volatile.Read(ref _hadConflict))
                {
                    Outcome = PatchRunOutcome.Conflict;
                }
                else
                {
                    Outcome = _files.Count == 0 ? PatchRunOutcome.None : PatchRunOutcome.Discarded;
                }
                return;
            }
            await PublicationGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var changed = Changes.Where(change => change.Kind != PatchChangeKind.Unchanged)
                    .Select(change => change.Path).ToHashSet(PatchPath.Comparer);
                if (_expectedChanges.Any(expected => !expected.SetEquals(changed)))
                {
                    throw new InvalidDataException("The graph changed additional files after verifying its change set.");
                }
                foreach (var (path, identity) in _reads)
                {
                    if (await IdentifyAsync(path, token).ConfigureAwait(false) != identity)
                    {
                        Outcome = PatchRunOutcome.Conflict;
                        throw new PatchConflictException(path);
                    }
                }
                ValidateQueries(token);
                if (Preview)
                {
                    Outcome = PatchRunOutcome.Previewed;
                    return;
                }
                await PublishAsync(token).ConfigureAwait(false);
            }
            finally { PublicationGate.Release(); }
        }
        catch
        {
            if (Outcome is PatchRunOutcome.None or PatchRunOutcome.Staged)
            {
                Outcome = PatchRunOutcome.Discarded;
            }
            throw;
        }
        finally { _gate.Release(); }
    }

    private async Task PublishAsync(CancellationToken token)
    {
        var changes = _files.Values.Where(file => file.Before.Sha256 != file.After.Sha256).ToArray();
        List<Publication> prepared = [];
        List<Publication> applied = [];
        List<string> directories = [];
        var retainRecovery = false;
        try
        {
            foreach (var file in changes)
            {
                token.ThrowIfCancellationRequested();
                PatchPath.RejectLinks(file.Path);
                EnsureParent(Path.GetDirectoryName(file.Path)!, directories);
                var sibling = file.Path + ".uotan-" + Guid.NewGuid().ToString("N");
                var publication = new Publication(file, sibling, sibling + ".backup");
                prepared.Add(publication);
                if (file.StagedPath is not null)
                {
                    await CopyExactAsync(file.StagedPath, sibling, file.After.Length, token).ConfigureAwait(false);
                    if ((await IdentifyAsync(sibling, token).ConfigureAwait(false)).Sha256 != file.After.Sha256)
                    {
                        throw new PatchConflictException(file.Path);
                    }
                    if (file.Before.Exists)
                    {
                        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(sibling, File.GetUnixFileMode(file.Path)); }
                        File.SetAttributes(sibling, File.GetAttributes(file.Path));
                    }
                }
            }
            foreach (var publication in prepared)
            {
                var file = publication.File;
                token.ThrowIfCancellationRequested();
                if (await IdentifyAsync(file.Path, token).ConfigureAwait(false) != file.Before)
                {
                    throw new PatchConflictException(file.Path);
                }
                if (!file.After.Exists)
                {
                    File.Move(file.Path, publication.Backup);
                }
                else if (file.Before.Exists)
                {
                    File.Replace(publication.Temporary, file.Path, publication.Backup);
                }
                else
                {
                    File.Move(publication.Temporary, file.Path, false);
                }
                applied.Add(publication);
            }
            Outcome = changes.Length > 0 ? PatchRunOutcome.Applied
                : UnchangedOutcome();
        }
        catch (Exception error)
        {
            List<Exception> errors = [error];
            foreach (var publication in applied.AsEnumerable().Reverse())
            {
                try
                {
                    if (File.Exists(publication.File.Path))
                    {
                        File.SetAttributes(publication.File.Path, File.GetAttributes(publication.File.Path) & ~FileAttributes.ReadOnly);
                    }
                    if (publication.File.Before.Exists)
                    {
                        File.Move(publication.Backup, publication.File.Path, true);
                    }
                    else { File.Delete(publication.File.Path); }
                }
                catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException)
                {
                    errors.Add(rollbackError);
                }
            }
            retainRecovery = errors.Count > 1;
            Outcome = retainRecovery ? PatchRunOutcome.RecoveryRequired
                : error is PatchConflictException ? PatchRunOutcome.Conflict : PatchRunOutcome.RolledBack;
            if (retainRecovery)
            {
                throw new AggregateException("Patch rollback failed; sibling .backup files were retained for recovery.", errors);
            }
            throw;
        }
        finally
        {
            foreach (var publication in prepared)
            {
                TryDelete(publication.Temporary);
                if (!retainRecovery) { TryDelete(publication.Backup); }
            }
            foreach (var directory in directories.AsEnumerable().Reverse())
            {
                try { Directory.Delete(directory, false); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private async Task<FileIdentity> OriginalAsync(string path, CancellationToken token)
    {
        if (_reads.TryGetValue(path, out var identity)) { return identity; }
        if (_reads.Count >= 10_000) { throw new InvalidDataException("Patch exceeds 10000 file dependencies."); }
        identity = await IdentifyAsync(path, token).ConfigureAwait(false);
        _reads.Add(path, identity);
        return identity;
    }

    private async Task<string> ResolveReadAsync(string path, CancellationToken token)
    {
        if (_files.TryGetValue(path, out var pending))
        {
            return pending.StagedPath ?? throw new FileNotFoundException("File is staged for deletion.", path);
        }
        if (!(await OriginalAsync(path, token).ConfigureAwait(false)).Exists)
        {
            throw new FileNotFoundException("Patch input was not found.", path);
        }
        return path;
    }

    private FileIdentity Current(string path, FileIdentity original) => _files.TryGetValue(path, out var file) ? file.After : original;

    private static void ValidateMode(string path, PatchFileMode mode, FileIdentity current)
    {
        if (!Enum.IsDefined(mode)) { throw new ArgumentOutOfRangeException(nameof(mode)); }
        if (mode == PatchFileMode.Add && current.Exists) { throw new IOException($"Patch output already exists: {path}"); }
        if (mode == PatchFileMode.Replace && !current.Exists) { throw new FileNotFoundException("Patch replacement target was not found.", path); }
    }

    private static void ValidateHash(string? actual, string expected, string path)
    {
        if (string.IsNullOrWhiteSpace(expected)) { return; }
        if (expected.Length != 64 || !expected.All(char.IsAsciiHexDigit)) { throw new InvalidDataException("Expected hash must be a SHA-256 hexadecimal value."); }
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) { throw new PatchConflictException(path); }
    }

    private static async Task<FileIdentity> IdentifyAsync(string path, CancellationToken token)
    {
        PatchPath.RejectLinks(path);
        if (Directory.Exists(path)) { throw new IOException("Patch file operation cannot target a directory."); }
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131_072, true);
            var length = stream.Length;
            var hash = Convert.ToHexString(await Sha256.ComputeHashAsync(stream, token).ConfigureAwait(false));
            if (stream.Position != length || stream.Length != length) { throw new PatchConflictException(path); }
            return new FileIdentity(true, length, hash, File.GetLastWriteTimeUtc(path));
        }
        catch (FileNotFoundException) { return FileIdentity.Missing; }
        catch (DirectoryNotFoundException) { return FileIdentity.Missing; }
    }

    private static async Task CopyExactAsync(string source, string target, long length, CancellationToken token)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 131_072, true);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131_072, true);
        var buffer = new byte[131_072];
        var remaining = length;
        while (remaining > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token).ConfigureAwait(false);
            if (read == 0) { throw new EndOfStreamException("Patch input was truncated."); }
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            remaining -= read;
        }
        if (await input.ReadAsync(buffer.AsMemory(0, 1), token).ConfigureAwait(false) != 0) { throw new PatchConflictException(source); }
        await output.FlushAsync(token).ConfigureAwait(false);
        output.Flush(true);
    }

    private static string Normalize(string path)
    {
        return PatchPath.NormalizeFile(path);
    }

    private string NewStagingPath()
    {
        Directory.CreateDirectory(_staging);
        return Path.Combine(_staging, Guid.NewGuid().ToString("N"));
    }

    private void RefreshChanges()
    {
        Changes = [.. _files.Values.Select(file => new PatchChange(file.Path,
            file.Before.Sha256 == file.After.Sha256 ? PatchChangeKind.Unchanged
            : !file.Before.Exists ? PatchChangeKind.Added
            : !file.After.Exists ? PatchChangeKind.Deleted : PatchChangeKind.Modified,
            file.Before.Length, file.After.Length, file.Before.Sha256, file.After.Sha256, file.BeforePreview, file.AfterPreview)
        {
            StagingInvocations = [.. _stagingInvocations[file.Path].Order()],
            BinaryPreviews = _binaryPreviews.TryGetValue(file.Path, out var previews) ? previews.ToArray() : [],
            BinaryPreviewTruncated = _truncatedBinaryPreviews.Contains(file.Path)
        })];
        if (!_finished && Outcome != PatchRunOutcome.Conflict) { Outcome = PatchRunOutcome.Staged; }
    }

    private void SetPending(PendingFile pending)
    {
        var invocation = _currentComposite.Value ?? 0;
        var owners = _stagingInvocations.GetValueOrDefault(pending.Path);
        if (owners is null || !owners.Contains(invocation))
        {
            if (_stagingAttributions >= 100000)
            {
                throw new InvalidDataException(LanguageService.Instance.GetString("Node_Patch_AttributionLimit"));
            }
            owners ??= [];
            owners.Add(invocation);
            _stagingInvocations[pending.Path] = owners;
            _stagingAttributions++;
        }
        if (_files.TryGetValue(pending.Path, out var previous) && previous.StagedPath is not null)
        {
            TryDelete(previous.StagedPath);
        }
        _files[pending.Path] = pending;
        if (!Volatile.Read(ref _hadConflict)) { Outcome = PatchRunOutcome.Staged; }
    }

    private static string PreviewText(string text) => text.Length <= 2048 ? text : text[..2048] + "…";

    private static void EnsureParent(string path, List<string> created)
    {
        var pending = new Stack<string>();
        for (var current = path; !Directory.Exists(current); current = Path.GetDirectoryName(current)
            ?? throw new DirectoryNotFoundException(path))
        {
            pending.Push(current);
        }
        while (pending.TryPop(out var directory))
        {
            Directory.CreateDirectory(directory);
            created.Add(directory);
        }
    }

    private void CheckOpen()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished) { throw new InvalidOperationException("Patch execution is already finalized."); }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
                File.Delete(path);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Discards unpublished staging. Recovery backups are outside this private directory.<para>丢弃未发布的暂存内容。恢复备份位于该私有目录之外。</para></summary>
    public ValueTask DisposeAsync()
    {
        if (_disposed) { return ValueTask.CompletedTask; }
        _disposed = true;
        if (Directory.Exists(_staging))
        {
            Directory.Delete(_staging, true);
        }
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed record FileIdentity(bool Exists, long Length, string? Sha256, DateTime LastWrite)
    {
        internal static FileIdentity Missing { get; } = new(false, 0, null, default);
    }

    private sealed record PendingFile(string Path, FileIdentity Before, FileIdentity After, string? StagedPath,
        string? BeforePreview = null, string? AfterPreview = null);
    private sealed record Publication(PendingFile File, string Temporary, string Backup);
}