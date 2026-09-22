using System.Security.Cryptography;
using UotanStudio.Firmware.BoundedContexts.Patching;

namespace UotanStudio.Services.Patching;

public sealed partial class PatchRun
{
    private readonly Dictionary<string, List<PatchBinaryPreview>> _binaryPreviews = new(PatchPath.Comparer);
    private readonly HashSet<string> _truncatedBinaryPreviews = new(PatchPath.Comparer);
    private int _binaryPreviewCount;
    /// <summary>Stages an equal-length binary patch without modifying the source or publishing early.</summary>
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
            var previews = new List<PatchBinaryPreview>();
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
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
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

    /// <summary>Searches original or staged bytes, preserving a verified read dependency for publication.</summary>
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
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
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