using System.Security.Cryptography;
using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Firmware.Patching;

namespace UotanStudio.Services.Patching;

public sealed partial class PatchRun
{
    private readonly Dictionary<string, PathKind> _directoryReads = new(PatchPath.Comparer);
    private readonly List<DiscoveryRead> _discoveries = [];
    private int _discoveredEntries;
    private long _discoveredCharacters;

    /// <summary>Probes the staged bytes and checks their identity before and after inspection.<para>探测暂存字节，并在检查前后核对其身份。</para></summary>
    /// <param name="path">The confined file path to probe.<para>要探测的受限文件路径。</para></param>
    /// <param name="hint">The expected format kind, if any.<para>预期的格式类别；无则为空。</para></param>
    /// <param name="token">The cancellation token.<para>取消令牌。</para></param>
    /// <returns>The probed format with its inspected details.<para>探测到的格式及其检查细节。</para></returns>
    public async Task<PatchFormatResult> ProbeFormatAsync(string path, PatchFormatKind hint, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            CheckOpen();
            path = Normalize(path);
            var source = await ResolveReadAsync(path, token).ConfigureAwait(false);
            var identity = Current(path, await OriginalAsync(path, token).ConfigureAwait(false));
            await using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, true);
            await CheckIdentityAsync().ConfigureAwait(false);
            var result = await Task.Run(() => PatchFormatProbe.Read(stream, hint, token), token).ConfigureAwait(false);
            await CheckIdentityAsync().ConfigureAwait(false);
            return result;

            async Task CheckIdentityAsync()
            {
                stream.Position = 0;
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
                if (stream.Length != identity.Length || hash != identity.Sha256)
                {
                    throw new PatchConflictException(path);
                }
                stream.Position = 0;
            }
        }
        finally { _gate.Release(); }
    }

    /// <summary>Checks physical directories and parents implied by staged additions, recording the physical precondition.<para>检查物理目录以及暂存新增所隐含的父目录，并记录该物理前置条件。</para></summary>
    /// <param name="path">The confined directory path to check.<para>要检查的受限目录路径。</para></param>
    /// <param name="token">The cancellation token.<para>取消令牌。</para></param>
    /// <returns>True when the directory exists or is implied by staged additions.<para>目录存在或由暂存新增隐含时为 true。</para></returns>
    public async Task<bool> DirectoryExistsAsync(string path, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            CheckOpen();
            path = NormalizeDirectory(path);
            var kind = ObserveDirectory(path);
            return kind == PathKind.Directory || kind == PathKind.Missing
                && _files.Values.Any(file => file.After.Exists && PatchFileService.Matches(path, "*", file.Path));
        }
        finally { _gate.Release(); }
    }

    /// <summary>Finds the staged file view and rechecks the physical search result before publishing.<para>查找暂存文件视图，并在发布前复核物理搜索结果。</para></summary>
    /// <param name="root">The confined directory to search.<para>要搜索的受限目录。</para></param>
    /// <param name="pattern">The file-name pattern to match.<para>要匹配的文件名模式。</para></param>
    /// <param name="expectedMatches">The required match count, or zero for any.<para>要求的匹配数量，零表示不限。</para></param>
    /// <param name="token">The cancellation token.<para>取消令牌。</para></param>
    /// <returns>The matching paths merged from physical and staged views, ordered.<para>由物理与暂存视图合并出的匹配路径，按序排列。</para></returns>
    public async Task<IReadOnlyList<string>> FindAsync(string root, string pattern, int expectedMatches, CancellationToken token)
    {
        if (expectedMatches is < 0 or > 100_000)
        {
            throw new InvalidDataException("Invalid expected match count.");
        }
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            CheckOpen();
            root = NormalizeDirectory(root);
            PatchFileService.ValidatePattern(pattern);
            var kind = ObserveDirectory(root);
            if (kind == PathKind.File || kind == PathKind.Missing
                && !_files.Values.Any(file => file.After.Exists && PatchFileService.Matches(root, "*", file.Path)))
            {
                throw new DirectoryNotFoundException(root);
            }
            pattern = pattern.Replace('\\', '/');
            var physical = PatchFileService.Discover(root, pattern, true, token);
            var prior = _discoveries.FirstOrDefault(query => PatchPath.Comparer.Equals(query.Root, root) && query.Pattern == pattern);
            if (prior is not null && !prior.Paths.SequenceEqual(physical, StringComparer.Ordinal))
            {
                throw new PatchConflictException(root);
            }
            if (prior is null)
            {
                var characters = physical.Sum(path => (long)path.Length);
                if (_discoveries.Count >= 128 || _discoveredEntries + physical.Count > 100_000
                    || _discoveredCharacters + characters > 16 * 1024 * 1024)
                {
                    throw new InvalidDataException("Patch exceeds the directory query dependency limit.");
                }
                _discoveries.Add(new DiscoveryRead(root, pattern, physical));
                _discoveredEntries += physical.Count;
                _discoveredCharacters += characters;
            }
            var result = physical.ToHashSet(PatchPath.Comparer);
            foreach (var file in _files.Values)
            {
                token.ThrowIfCancellationRequested();
                if (PatchFileService.Matches(root, pattern, file.Path))
                {
                    if (file.After.Exists)
                    {
                        result.Add(file.Path);
                    }
                    else
                    {
                        result.Remove(file.Path);
                    }
                }
            }
            if (result.Count != expectedMatches)
            {
                throw new InvalidDataException($"Expected {expectedMatches} files; found {result.Count}.");
            }
            foreach (var path in result)
            {
                await OriginalAsync(path, token).ConfigureAwait(false);
            }
            return [.. result.Order(StringComparer.Ordinal)];
        }
        finally { _gate.Release(); }
    }

    private PathKind ObserveDirectory(string path)
    {
        var kind = ReadPathKind(path);
        if (_directoryReads.TryGetValue(path, out var prior))
        {
            if (kind != prior)
            {
                throw new PatchConflictException(path);
            }
        }
        else
        {
            if (_directoryReads.Count >= 10_000)
            {
                throw new InvalidDataException("Patch exceeds 10000 directory dependencies.");
            }
            _directoryReads.Add(path, kind);
        }
        return kind;
    }

    private void ValidateQueries(CancellationToken token)
    {
        foreach (var (path, kind) in _directoryReads)
        {
            token.ThrowIfCancellationRequested();
            if (ReadPathKind(path) != kind)
            {
                Outcome = PatchRunOutcome.Conflict;
                throw new PatchConflictException(path);
            }
        }
        foreach (var query in _discoveries)
        {
            var current = PatchFileService.Discover(query.Root, query.Pattern, true, token);
            if (!query.Paths.SequenceEqual(current, StringComparer.Ordinal))
            {
                Outcome = PatchRunOutcome.Conflict;
                throw new PatchConflictException(query.Root);
            }
        }
    }

    private static PathKind ReadPathKind(string path)
    {
        PatchPath.RejectLinks(path);
        try
        {
            return (File.GetAttributes(path) & FileAttributes.Directory) != 0 ? PathKind.Directory : PathKind.File;
        }
        catch (FileNotFoundException) { return PathKind.Missing; }
        catch (DirectoryNotFoundException) { return PathKind.Missing; }
    }

    private static string NormalizeDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        PatchPath.RejectLinks(full);
        return full == Path.GetPathRoot(full) ? full : PatchPath.NormalizeFile(full);
    }

    private enum PathKind { Missing, File, Directory }
    private sealed record DiscoveryRead(string Root, string Pattern, IReadOnlyList<string> Paths);
}