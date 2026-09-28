using UotanStudio.Firmware.FileSystem.Rebuilding;
using UotanStudio.Firmware.Patching;

namespace UotanStudio.Services.Patching;

public sealed partial class PatchRun
{
    /// <summary>Reads a bounded raw xattr from the staged image and retains the original image dependency.</summary>
    public async Task<byte[]?> ReadImageXattrAsync(string path, string format, string imagePath, string name, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            CheckOpen();
            path = Normalize(path);
            var source = await ResolveReadAsync(path, token).ConfigureAwait(false);
            var current = Current(path, await OriginalAsync(path, token).ConfigureAwait(false));
            await VerifyImageIdentityAsync(source, path, current, token).ConfigureAwait(false);
            using var input = File.OpenRead(source);
            var result = await Task.Run(() => ImageAttributeReader.ReadXattr(input, format, imagePath, name, token), token).ConfigureAwait(false);
            await VerifyImageIdentityAsync(source, path, current, token).ConfigureAwait(false);
            return result;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reads inode attributes from the staged view and retains the image dependency.</summary>
    public async Task<ImageFileAttributes> ReadImageAttributesAsync(string path, string format, string imagePath, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            CheckOpen();
            path = Normalize(path);
            var source = await ResolveReadAsync(path, token).ConfigureAwait(false);
            var current = Current(path, await OriginalAsync(path, token).ConfigureAwait(false));
            await VerifyImageIdentityAsync(source, path, current, token).ConfigureAwait(false);
            using var input = File.OpenRead(source);
            var result = await Task.Run(() => ImageAttributeReader.Read(input, format, imagePath, token), token).ConfigureAwait(false);
            await VerifyImageIdentityAsync(source, path, current, token).ConfigureAwait(false);
            return result;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Stages one inode attribute change, retaining exact image bytes for an unchanged request.</summary>
    public async Task<bool> StageImageAttributesAsync(string path, string format, FileSystemAttributeEdit edit,
        long capacity, long maximumExtractionBytes, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumExtractionBytes);
        if (edit.Permissions > 0xFFF || edit.ExpectedPermissions is > 0xFFF)
        {
            throw new InvalidDataException("Permission bits exceed the supported range.");
        }
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
            ImageFileAttributes before;
            using (var input = File.OpenRead(source))
            {
                before = await Task.Run(() => ImageAttributeReader.Read(input, format, edit.Path, token), token).ConfigureAwait(false);
            }
            if (edit.ExpectedUid is { } uid && uid != before.Uid
                || edit.ExpectedGid is { } gid && gid != before.Gid
                || edit.ExpectedPermissions is { } permissions && permissions != (before.Mode & 0xFFF))
            {
                throw new PatchConflictException(path);
            }
            if (edit.Uid == before.Uid && edit.Gid == before.Gid && edit.Permissions == (before.Mode & 0xFFF))
            {
                await VerifyImageIdentityAsync(source, path, current, token).ConfigureAwait(false);
                if (current.Length > capacity) { throw new InvalidDataException("Current image exceeds the specified capacity."); }
                return false;
            }
            temporary = NewStagingPath();
            await Task.Run(() => ImageConfigurationEditor.RebuildManyAsync(source, format, [], temporary, capacity,
                maximumExtractionBytes, token, [edit]), token).ConfigureAwait(false);
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
    }

    private static async Task VerifyImageIdentityAsync(string source, string path, FileIdentity current, CancellationToken token)
    {
        var observed = await IdentifyAsync(source, token).ConfigureAwait(false);
        if (observed.Length != current.Length || observed.Sha256 != current.Sha256)
        {
            throw new PatchConflictException(path);
        }
    }
}