using UotanStudio.Firmware.Patching;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Services.Patching;

public sealed partial class PatchRun
{
    private readonly Dictionary<string, List<PatchBinaryPreview>> _binaryPreviews = new(PatchPath.Comparer);
    private readonly HashSet<string> _truncatedBinaryPreviews = new(PatchPath.Comparer);
    private int _binaryPreviewCount;
    /// <summary>Stages an equal-length binary patch without modifying the source or publishing early.
    /// <para>暂存等长的二进制补丁，不修改源文件，也不提前发布。</para></summary>
    /// <param name="path">The confined file path to patch.<para>要打补丁的受限文件路径。</para></param>
    /// <param name="find">The hexadecimal pattern to search for.<para>要搜索的十六进制模式。</para></param>
    /// <param name="findMask">The mask pairing with the search pattern.<para>与搜索模式配对的掩码。</para></param>
    /// <param name="replacement">The hexadecimal replacement bytes.<para>替换用的十六进制字节。</para></param>
    /// <param name="replaceMask">The mask pairing with the replacement.<para>与替换字节配对的掩码。</para></param>
    /// <param name="offset">The starting byte offset of the search window.
    /// <para>搜索窗口的起始字节偏移。</para></param>
    /// <param name="length">The byte length of the search window.<para>搜索窗口的字节长度。</para></param>
    /// <param name="alignment">The required match alignment in bytes.<para>要求的匹配字节对齐。</para></param>
    /// <param name="skip">The number of leading matches to skip.<para>要跳过的开头匹配数量。</para></param>
    /// <param name="expectedMatches">The required match count, or zero for any.
    /// <para>要求的匹配数量，零表示不限。</para></param>
    /// <param name="token">The cancellation token.<para>取消令牌。</para></param>
    /// <returns>The offsets of the applied replacements.<para>已应用替换的偏移列表。</para></returns>
    public async Task<IReadOnlyList<long>> StageBinaryAsync(string path, string find, string findMask,
        string replacement, string replaceMask, long offset, long length, int alignment, int skip,
        int expectedMatches, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            CheckOpen();
            path = Normalize(path);
            var source = await ResolveReadAsync(path, token).ConfigureAwait(false);
            var original = await OriginalAsync(path, token).ConfigureAwait(false);
            var identity = Current(path, original);
            temporary = NewStagingPath();
            IReadOnlyList<long> result;
            List<PatchBinaryPreview> previews = [];
            var remainingPreviews = Math.Min(64 - (_binaryPreviews.GetValueOrDefault(path)?.Count ?? 0), 4096 - _binaryPreviewCount);
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
            {
                await VerifyAsync(input).ConfigureAwait(false);
                await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, true);
                result = await BinaryPattern.ReplaceAsync(input, output, find, findMask, replacement, replaceMask,
                    offset, length, alignment, skip, expectedMatches, token).ConfigureAwait(false);
                await output.FlushAsync(token).ConfigureAwait(false);
                var windowLength = find.Count(character => !char.IsWhiteSpace(character)) / 2;
                var before = new byte[Math.Min(64, windowLength)];
                var afterBytes = new byte[before.Length];
                foreach (var match in result.Take(remainingPreviews))
                {
                    input.Position = match;
                    output.Position = match;
                    await input.ReadExactlyAsync(before, token).ConfigureAwait(false);
                    await output.ReadExactlyAsync(afterBytes, token).ConfigureAwait(false);
                    previews.Add(new PatchBinaryPreview(match, windowLength, _currentComposite.Value ?? 0,
                        Convert.ToHexString(before), Convert.ToHexString(afterBytes)));
                }
                await VerifyAsync(input).ConfigureAwait(false);
            }
            var after = await IdentifyAsync(temporary, token).ConfigureAwait(false);
            SetPending(new PendingFile(path, original, after, temporary));
            temporary = null;
            if (!_binaryPreviews.TryGetValue(path, out var existing))
            {
                existing = [];
                _binaryPreviews.Add(path, existing);
            }
            existing.AddRange(previews);
            _binaryPreviewCount += previews.Count;
            if (previews.Count < result.Count)
            {
                _truncatedBinaryPreviews.Add(path);
            }
            return result;

            async Task VerifyAsync(Stream stream)
            {
                stream.Position = 0;
                var hash = Convert.ToHexString(await Sha256.ComputeHashAsync(stream, token).ConfigureAwait(false));
                if (stream.Length != identity.Length || hash != identity.Sha256)
                {
                    throw new PatchConflictException(path);
                }
            }
        }
        finally
        {
            if (temporary is not null)
            {
                TryDelete(temporary);
            }
            _gate.Release();
        }
    }

    /// <summary>Searches original or staged bytes, preserving a verified read dependency for publication.<para>搜索原始或已暂存的字节，并为发布保留已校验的读取依赖。</para></summary>
    /// <param name="path">The confined file path to search.<para>要搜索的受限文件路径。</para></param>
    /// <param name="find">The hexadecimal pattern to search for.<para>要搜索的十六进制模式。</para></param>
    /// <param name="mask">The mask pairing with the search pattern.<para>与搜索模式配对的掩码。</para></param>
    /// <param name="offset">The starting byte offset of the search window.<para>搜索窗口的起始字节偏移。</para></param>
    /// <param name="length">The byte length of the search window.<para>搜索窗口的字节长度。</para></param>
    /// <param name="alignment">The required match alignment in bytes.<para>要求的匹配字节对齐。</para></param>
    /// <param name="skip">The number of leading matches to skip.<para>要跳过的开头匹配数量。</para></param>
    /// <param name="expectedMatches">The required match count, or zero for any.<para>要求的匹配数量，零表示不限。</para></param>
    /// <param name="token">The cancellation token.<para>取消令牌。</para></param>
    /// <returns>The offsets of the found matches.<para>找到的匹配偏移列表。</para></returns>
    public async Task<IReadOnlyList<long>> FindBinaryAsync(string path, string find, string mask,
        long offset, long length, int alignment, int skip, int expectedMatches, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            CheckOpen();
            path = Normalize(path);
            var source = await ResolveReadAsync(path, token).ConfigureAwait(false);
            var identity = Current(path, await OriginalAsync(path, token).ConfigureAwait(false));
            await using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            await VerifyAsync().ConfigureAwait(false);
            var result = await BinaryPattern.FindAsync(stream, find, mask, offset, length, alignment, skip, expectedMatches, token).ConfigureAwait(false);
            await VerifyAsync().ConfigureAwait(false);
            return result;

            async Task VerifyAsync()
            {
                stream.Position = 0;
                var hash = Convert.ToHexString(await Sha256.ComputeHashAsync(stream, token).ConfigureAwait(false));
                if (stream.Length != identity.Length || hash != identity.Sha256)
                {
                    throw new PatchConflictException(path);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}