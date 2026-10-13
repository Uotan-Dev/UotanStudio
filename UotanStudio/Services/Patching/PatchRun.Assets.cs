namespace UotanStudio.Services.Patching;

public sealed partial class PatchRun
{
    /// <summary>Stages verified resource bytes under the same publication boundary as ordinary file edits.<para>在与普通文件编辑相同的发布边界下暂存已校验的资源字节。</para></summary>
    /// <param name="asset">The resource bytes with their expected length and hash.<para>带预期长度与哈希的资源字节。</para></param>
    /// <param name="target">The confined target path.<para>受限的目标路径。</para></param>
    /// <param name="mode">The required file-mode precondition.<para>要求的文件模式前置条件。</para></param>
    /// <param name="token">The cancellation token.<para>取消令牌。</para></param>
    public async Task StageAssetAsync(PatchAsset asset, string target, PatchFileMode mode, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(asset);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            CheckOpen();
            target = Normalize(target);
            var original = await OriginalAsync(target, token).ConfigureAwait(false);
            ValidateMode(target, mode, Current(target, original));
            var temporary = NewStagingPath();
            try
            {
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                                 FileShare.None, 131072, FileOptions.Asynchronous))
                {
                    await asset.CopyToAsync(output, token).ConfigureAwait(false);
                }
                var copied = await IdentifyAsync(temporary, token).ConfigureAwait(false);
                if (copied.Length != asset.Length || !string.Equals(copied.Sha256, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Staged resource identity changed.");
                }
                SetPending(new PendingFile(target, original, copied, temporary));
            }
            catch
            {
                TryDelete(temporary);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}