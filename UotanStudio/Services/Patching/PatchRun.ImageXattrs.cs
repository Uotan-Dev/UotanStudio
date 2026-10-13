using UotanStudio.Firmware.FileSystem.Rebuilding;
using UotanStudio.Firmware.Patching;

namespace UotanStudio.Services.Patching;

public sealed partial class PatchRun
{
    /// <summary>Stages one user xattr set or delete, distinguishing absence from empty and retaining byte identity on no-op.<para>暂存一次 user 扩展属性的设置或删除，区分不存在与空值，并在无操作时保留字节一致性。</para></summary>
    /// <param name="path">The confined image file path.<para>受限的镜像文件路径。</para></param>
    /// <param name="format">The image container format selector.<para>镜像容器格式选择器。</para></param>
    /// <param name="edit">The xattr edit with its optional expected-value guard.<para>带可选预期值守卫的扩展属性编辑。</para></param>
    /// <param name="capacity">The maximum rebuilt image size in bytes.<para>重建镜像的最大字节容量。</para></param>
    /// <param name="maximumExtractionBytes">The upper bound on extracted bytes.<para>提取字节的数量上限。</para></param>
    /// <param name="token">The cancellation token.<para>取消令牌。</para></param>
    /// <returns>True when a rebuild was staged; false when the value already matches.<para>已暂存重建时为 true；值本就相同时为 false。</para></returns>
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