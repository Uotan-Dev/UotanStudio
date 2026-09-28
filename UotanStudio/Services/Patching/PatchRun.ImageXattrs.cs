using UotanStudio.Firmware.FileSystem.Rebuilding;
using UotanStudio.Firmware.Patching;

namespace UotanStudio.Services.Patching;

public sealed partial class PatchRun
{
    /// <summary>Stages one user xattr set/delete, distinguishing absence from empty and retaining byte identity on no-op.</summary>
    public async Task<bool> StageImageXattrAsync(string path, string format, FileSystemXattrEdit edit,
        long capacity, long maximumExtractionBytes, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(edit.Name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumExtractionBytes);
        if (!edit.Name.StartsWith("user.", StringComparison.Ordinal) || edit.Name.Length is <= 5 or > 255
            || edit.Name.Any(character => character is < '!' or > '~') || edit.Value?.Length > 65536 || edit.ExpectedValue?.Length > 65536)
        {
            throw new InvalidDataException("Invalid user extended attribute edit.");
        }
        edit = edit with { Value = edit.Value?.ToArray(), ExpectedValue = edit.ExpectedValue?.ToArray() };
        await _gate.WaitAsync(token).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            CheckOpen();
            path = Normalize(path);
            var source = await ResolveReadAsync(path, token).ConfigureAwait(false);
            var original = await OriginalAsync(path, token).ConfigureAwait(false);
            var current = Current(path, original);
            await VerifyImageIdentityAsync(source, path, current, token).ConfigureAwait(false);
            byte[]? before;
            using (var input = File.OpenRead(source))
            {
                before = await Task.Run(() => ImageAttributeReader.ReadXattr(input, format, edit.Path, edit.Name, token), token).ConfigureAwait(false);
            }
            if (edit.CheckOldValue && !Same(before, edit.ExpectedValue)) { throw new PatchConflictException(path); }
            if (Same(before, edit.Value))
            {
                await VerifyImageIdentityAsync(source, path, current, token).ConfigureAwait(false);
                if (current.Length > capacity) { throw new InvalidDataException("Current image exceeds the specified capacity."); }
                return false;
            }
            temporary = NewStagingPath();
            await Task.Run(() => ImageConfigurationEditor.RebuildManyAsync(source, format, [], temporary,
                capacity, maximumExtractionBytes, token, xattrEdits: [edit]), token).ConfigureAwait(false);
            await VerifyImageIdentityAsync(source, path, current, token).ConfigureAwait(false);
            var after = await IdentifyAsync(temporary, token).ConfigureAwait(false);
            SetPending(new PendingFile(path, original, after, temporary));
            temporary = null;
            return true;
        }
        finally
        {
            if (temporary is not null) { TryDelete(temporary); }
            _gate.Release();
        }

        static bool Same(byte[]? first, byte[]? second)
            => first is null ? second is null : second is not null && first.AsSpan().SequenceEqual(second);
    }
}