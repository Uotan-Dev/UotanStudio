using UotanStudio.Firmware.Patching;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Services.Patching;

public sealed partial class PatchRun
{
    /// <summary>Stages a verified configuration rebuild; identical encoded bytes preserve the current image.<para>暂存一次已校验的配置重建；编码字节相同时保留当前镜像。</para></summary>
    /// <param name="path">The confined image file path.<para>受限的镜像文件路径。</para></param>
    /// <param name="format">The image container format selector.<para>镜像容器格式选择器。</para></param>
    /// <param name="imagePath">The configuration path inside the image.<para>镜像内的配置路径。</para></param>
    /// <param name="text">The new configuration text.<para>新的配置文本。</para></param>
    /// <param name="encoding">The encoding name for the text.<para>文本的编码名称。</para></param>
    /// <param name="expectedHash">The SHA-256 guard for the current extracted bytes.<para>当前已提取字节的 SHA-256 校验值。</para></param>
    /// <param name="capacity">The maximum rebuilt image size in bytes.<para>重建镜像的最大字节容量。</para></param>
    /// <param name="maximumExtractionBytes">The upper bound on extracted bytes.<para>提取字节的数量上限。</para></param>
    /// <param name="token">The cancellation token.<para>取消令牌。</para></param>
    /// <returns>True when a rebuild was staged; false when the bytes already match.<para>已暂存重建时为 true；字节本就相同时为 false。</para></returns>
    public Task<bool> StageImageTextAsync(string path, string format, string imagePath, string text,
        string encoding, string expectedHash, long capacity, long maximumExtractionBytes, CancellationToken token)
        => StageImageTextsAsync(path, format, [new(imagePath, text, encoding, expectedHash)],
            capacity, maximumExtractionBytes, token);

    /// <summary>Validates every old-file guard and stages one rebuild containing all changed configurations.<para>校验每个旧文件守卫，并暂存一次包含全部变更配置的重建。</para></summary>
    /// <param name="path">The confined image file path.<para>受限的镜像文件路径。</para></param>
    /// <param name="format">The image container format selector.<para>镜像容器格式选择器。</para></param>
    /// <param name="edits">The configuration edits to apply together.<para>要一并应用的配置编辑集合。</para></param>
    /// <param name="capacity">The maximum rebuilt image size in bytes.<para>重建镜像的最大字节容量。</para></param>
    /// <param name="maximumExtractionBytes">The upper bound on extracted bytes.<para>提取字节的数量上限。</para></param>
    /// <param name="token">The cancellation token.<para>取消令牌。</para></param>
    /// <returns>True when a rebuild was staged; false when every encoded byte already matches.<para>已暂存重建时为 true；所有编码字节本就相同时为 false。</para></returns>
    public async Task<bool> StageImageTextsAsync(string path, string format, IReadOnlyList<PatchImageTextEdit> edits,
        long capacity, long maximumExtractionBytes, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumExtractionBytes);
        ArgumentNullException.ThrowIfNull(edits);
        if (edits.Count is < 1 or > 256) { throw new InvalidDataException("Image edit batches require 1 to 256 targets."); }
        var snapshot = edits.ToArray();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edit in snapshot)
        {
            ArgumentNullException.ThrowIfNull(edit);
            ArgumentNullException.ThrowIfNull(edit.Path);
            if (!paths.Add(edit.Path.TrimStart('/'))) { throw new InvalidDataException("Duplicate image edit target."); }
        }
        await _gate.WaitAsync(token).ConfigureAwait(false);
        List<string> temporaryFiles = [];
        List<ImageConfigurationReplacement> replacements = [];
        var changed = false;
        string? rebuilt = null;
        try
        {
            CheckOpen();
            path = Normalize(path);
            var source = await ResolveReadAsync(path, token).ConfigureAwait(false);
            var original = await OriginalAsync(path, token).ConfigureAwait(false);
            var current = Current(path, original);
            await VerifyAsync().ConfigureAwait(false);
            foreach (var edit in snapshot)
            {
                var extracted = NewStagingPath();
                temporaryFiles.Add(extracted);
                using (var input = File.OpenRead(source))
                using (var output = new FileStream(extracted, FileMode.CreateNew, FileAccess.Write))
                {
                    await Task.Run(() => ImageConfigurationReader.Extract(input, format, edit.Path, output, token), token).ConfigureAwait(false);
                }
                var before = await IdentifyAsync(extracted, token).ConfigureAwait(false);
                ValidateHash(before.Sha256, edit.ExpectedHash, path);
                var replacement = NewStagingPath();
                temporaryFiles.Add(replacement);
                await PatchFileService.WriteNewAsync(replacement, edit.Text, edit.Encoding, token).ConfigureAwait(false);
                var desired = await IdentifyAsync(replacement, token).ConfigureAwait(false);
                changed |= before.Sha256 != desired.Sha256;
                replacements.Add(new(edit.Path, File.OpenRead(replacement)));
            }
            if (!changed)
            {
                await VerifyAsync().ConfigureAwait(false);
                if (current.Length > capacity) { throw new InvalidDataException("Current image exceeds the specified capacity."); }
                return false;
            }
            rebuilt = NewStagingPath();
            await Task.Run(() => ImageConfigurationEditor.RebuildManyAsync(source, format, replacements,
                rebuilt, capacity, maximumExtractionBytes, token), token).ConfigureAwait(false);
            await VerifyAsync().ConfigureAwait(false);
            var after = await IdentifyAsync(rebuilt, token).ConfigureAwait(false);
            SetPending(new PendingFile(path, original, after, rebuilt));
            rebuilt = null;
            return true;

            async Task VerifyAsync()
            {
                var observed = await IdentifyAsync(source, token).ConfigureAwait(false);
                if (observed.Length != current.Length || observed.Sha256 != current.Sha256)
                {
                    throw new PatchConflictException(path);
                }
            }
        }
        finally
        {
            foreach (var replacement in replacements) { replacement.Content.Dispose(); }
            foreach (var temporary in temporaryFiles) { TryDelete(temporary); }
            if (rebuilt is not null) { TryDelete(rebuilt); }
            _gate.Release();
        }
    }

    /// <summary>Reads a configuration from the current image overlay and retains its original image dependency.<para>从当前镜像叠加视图读取配置，并保留其原始镜像依赖。</para></summary>
    /// <param name="path">The confined image file path.<para>受限的镜像文件路径。</para></param>
    /// <param name="format">The image container format selector.<para>镜像容器格式选择器。</para></param>
    /// <param name="imagePath">The configuration path inside the image.<para>镜像内的配置路径。</para></param>
    /// <param name="token">The cancellation token.<para>取消令牌。</para></param>
    /// <returns>The decoded text document with its observed hash.<para>带观测哈希的解码文本文档。</para></returns>
    public async Task<PatchTextDocument> ReadImageTextAsync(string path, string format, string imagePath, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            CheckOpen();
            path = Normalize(path);
            var source = await ResolveReadAsync(path, token).ConfigureAwait(false);
            var identity = Current(path, await OriginalAsync(path, token).ConfigureAwait(false));
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            await VerifyAsync().ConfigureAwait(false);
            temporary = NewStagingPath();
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await Task.Run(() => ImageConfigurationReader.Extract(input, format, imagePath, output, token), token).ConfigureAwait(false);
            }
            await VerifyAsync().ConfigureAwait(false);
            return await PatchFileService.ReadAsync(temporary, token).ConfigureAwait(false);

            async Task VerifyAsync()
            {
                input.Position = 0;
                var hash = Convert.ToHexString(await Sha256.ComputeHashAsync(input, token).ConfigureAwait(false));
                if (input.Length != identity.Length || hash != identity.Sha256) { throw new PatchConflictException(path); }
            }
        }
        finally
        {
            if (temporary is not null) { TryDelete(temporary); }
            _gate.Release();
        }
    }
}

/// <summary>One configuration edit with an optional SHA-256 guard for the current extracted bytes.<para>一条配置编辑，可带针对当前已提取字节的 SHA-256 守卫。</para></summary>
/// <param name="Path">The configuration path inside the image.<para>镜像内的配置路径。</para></param>
/// <param name="Text">The replacement configuration text.<para>替换用的配置文本。</para></param>
/// <param name="Encoding">The encoding name for the text.<para>文本的编码名称。</para></param>
/// <param name="ExpectedHash">The SHA-256 guard, or empty when unchecked.<para>SHA-256 守卫；为空表示不校验。</para></param>
public sealed record PatchImageTextEdit(string Path, string Text, string Encoding, string ExpectedHash);