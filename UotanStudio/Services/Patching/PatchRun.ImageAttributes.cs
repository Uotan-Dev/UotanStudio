using UotanStudio.Firmware.FileSystem.Rebuilding;
using UotanStudio.Firmware.Patching;

namespace UotanStudio.Services.Patching;

public sealed partial class PatchRun
{
    /// <summary>Reads a bounded raw xattr from the staged image and retains the original image dependency.<para>从暂存镜像读取有界的原始扩展属性值，并保留原始镜像依赖。</para></summary>
    /// <param name="path">The confined image file path.<para>受限的镜像文件路径。</para></param>
    /// <param name="format">The image container format selector.<para>镜像容器格式选择器。</para></param>
    /// <param name="imagePath">The inode path inside the image.<para>镜像内的 inode 路径。</para></param>
    /// <param name="name">The extended attribute name.<para>扩展属性名称。</para></param>
    /// <param name="token">The cancellation token.<para>取消令牌。</para></param>
    /// <returns>The raw attribute bytes, or null when the attribute is absent.<para>原始属性字节；属性不存在时为 null。</para></returns>
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

    /// <summary>Reads inode attributes from the staged view and retains the image dependency.<para>从暂存视图读取 inode 属性，并保留镜像依赖。</para></summary>
    /// <param name="path">The confined image file path.<para>受限的镜像文件路径。</para></param>
    /// <param name="format">The image container format selector.<para>镜像容器格式选择器。</para></param>
    /// <param name="imagePath">The inode path inside the image.<para>镜像内的 inode 路径。</para></param>
    /// <param name="token">The cancellation token.<para>取消令牌。</para></param>
    /// <returns>The observed uid, gid and mode attributes.<para>观测到的 uid、gid 与 mode 属性。</para></returns>
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

    /// <summary>Stages one inode attribute change, retaining exact image bytes for an unchanged request.<para>暂存一次 inode 属性变更；请求无变化时保留精确的镜像字节。</para></summary>
    /// <param name="path">The confined image file path.<para>受限的镜像文件路径。</para></param>
    /// <param name="format">The image container format selector.<para>镜像容器格式选择器。</para></param>
    /// <param name="edit">The attribute edit with its optional expected-value guards.<para>带可选预期值守卫的属性编辑。</para></param>
    /// <param name="capacity">The maximum rebuilt image size in bytes.<para>重建镜像的最大字节容量。</para></param>
    /// <param name="maximumExtractionBytes">The upper bound on extracted bytes.<para>提取字节的数量上限。</para></param>
    /// <param name="token">The cancellation token.<para>取消令牌。</para></param>
    /// <returns>True when a rebuild was staged; false when the attributes already match.<para>已暂存重建时为 true；属性本就相同时为 false。</para></returns>
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