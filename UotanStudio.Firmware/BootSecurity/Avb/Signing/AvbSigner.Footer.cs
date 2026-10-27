namespace UotanStudio.Firmware.BootSecurity.Avb.Signing;

public static partial class AvbSigner
{
    /// <summary>
    /// Removes a validated AVB footer, VBMeta block and alignment padding from a signed partition image and
    /// publishes the original content bytes recorded by the footer.
    /// <para>从已签名分区镜像移除经过校验的 AVB 尾部、VBMeta 块与对齐填充，并发布尾部记录的原始内容字节。</para>
    /// </summary>
    /// <param name="imagePath">Signed raw or Android Sparse partition image carrying a valid footer.
    /// <para>携带合法尾部的已签名原始或 Android Sparse 分区镜像。</para></param>
    /// <param name="outputPath">Destination file published atomically; must differ from <paramref name="imagePath"/>.
    /// <para>原子发布的目标文件；必须与 <paramref name="imagePath"/> 不同。</para></param>
    /// <param name="overwrite">Whether an existing <paramref name="outputPath"/> may be replaced.
    /// <para>是否允许替换已存在的 <paramref name="outputPath"/>。</para></param>
    /// <param name="cancellationToken">Cancels reads and the atomic copy. <para>取消读取与原子复制。</para></param>
    /// <returns>Success reporting the restored byte count, or a failure result when the footer is absent or inconsistent.
    /// <para>成功时报告恢复的字节数；尾部缺失或不一致时返回失败结果。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="imagePath"/> or <paramref name="outputPath"/> is empty or whitespace.
    /// <para><paramref name="imagePath"/> 或 <paramref name="outputPath"/> 为空或仅含空白。</para></exception>
    public static async Task<AvbSignResult> RemoveFooterAsync(
        string imagePath,
        string outputPath,
        bool overwrite = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (PathsEqual(imagePath, outputPath))
        {
            return Fail("Output path must differ from the signed image. 输出路径不能与已签名镜像相同。");
        }

        try
        {
            var result = await AvbFooterEditor.RemoveFooterAsync(imagePath, outputPath, overwrite, cancellationToken)
                .ConfigureAwait(false);
            if (!result.Success)
            {
                return Fail(result.Summary);
            }

            var size = result.Metadata.TryGetValue("OriginalImageSize", out var text)
                && long.TryParse(text, out var parsed)
                ? parsed
                : 0;
            return new AvbSignResult
            {
                Success = true,
                OutputPath = outputPath,
                AlgorithmName = "footer-removed",
                OutputSize = size,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }
}