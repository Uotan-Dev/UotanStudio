using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Streams;

namespace UotanStudio.Firmware.BootSecurity.Avb.Signing;

/// <summary>
/// Removes a validated AVB footer, VBMeta payload, and alignment padding from a partition image.
/// <para>从分区镜像中移除已校验的 AVB 尾部、VBMeta 载荷与对齐填充。</para>
/// </summary>
internal static class AvbFooterEditor
{
    /// <summary>
    /// Publishes the original unsigned image bytes recorded by a valid trailing AVB footer. The footer must carry the
    /// <c>AVBf</c> magic with major version 1 and its recorded ranges must fit inside the image; only the first
    /// <c>OriginalImageSize</c> bytes are copied and published via an atomic temporary-file move.
    /// <para>发布由镜像末尾合法 AVB 尾部记录的原始未签名镜像字节。尾部必须携带 <c>AVBf</c> 魔数且主版本为 1，
    /// 其记录的范围必须落在镜像内；仅复制前 <c>OriginalImageSize</c> 字节并通过临时文件原子移动发布。</para>
    /// </summary>
    /// <param name="imagePath">
    /// Existing signed partition image to read. <para>要读取的已签名分区镜像。</para>
    /// </param>
    /// <param name="outputPath">
    /// Destination file; parent directories are created when missing. <para>目标文件；缺失的父目录会被创建。</para>
    /// </param>
    /// <param name="overwrite">
    /// Whether an existing <paramref name="outputPath"/> may be replaced. <para>是否允许替换已存在的 <paramref name="outputPath"/>。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the footer read and the byte copy; the partial temporary file is discarded on cancellation.
    /// <para>取消尾部读取与字节复制；取消时未完成的临时文件会被丢弃。</para>
    /// </param>
    /// <returns>
    /// A result reporting the restored byte count and output path, or a failure when no valid footer or
    /// consistent ranges are found; I/O errors are folded into a failure result.
    /// <para>成功时报告恢复的字节数与输出路径；未找到有效尾部或范围不一致时返回失败；I/O 错误会被归入失败结果。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="imagePath"/> or <paramref name="outputPath"/> is null or whitespace.
    /// <para><paramref name="imagePath"/> 或 <paramref name="outputPath"/> 为 null 或空白。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was signaled. <para><paramref name="cancellationToken"/> 被触发。</para>
    /// </exception>
    public static async Task<FirmwareOpResult> RemoveFooterAsync(
        string imagePath,
        string outputPath,
        bool overwrite = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var inputLength = new FileInfo(imagePath).Length;
            if (inputLength < AvbFooter.Size)
            {
                return FirmwareOpResult.Fail("The image is too small to contain an AVB footer.");
            }

            AvbFooter footer;
            await using (var input = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                AvbFooter.Size, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                input.Position = inputLength - AvbFooter.Size;
                var bytes = new byte[AvbFooter.Size];
                await input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                if (AvbFooter.TryLoad(bytes) is not { } parsed)
                {
                    return FirmwareOpResult.Fail("The image does not contain a supported AVB footer.");
                }

                footer = parsed;
            }

            var dataEnd = checked((ulong)(inputLength - AvbFooter.Size));
            if (footer.OriginalImageSize > long.MaxValue ||
                footer.OriginalImageSize > footer.VbmetaOffset ||
                footer.VbmetaOffset > dataEnd ||
                footer.VbmetaSize > dataEnd - footer.VbmetaOffset)
            {
                return FirmwareOpResult.Fail("The AVB footer references ranges outside the image.");
            }

            await FileExtraction.WriteAtomicAsync(outputPath, async output =>
            {
                await using var input = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await StreamCopy.CopyExactlyAsync(input, output, checked((long)footer.OriginalImageSize),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }, overwrite, cancellationToken).ConfigureAwait(false);

            return FirmwareOpResult.Ok(
                $"Removed the AVB footer and restored {footer.OriginalImageSize} original image bytes.",
                [outputPath],
                new Dictionary<string, string>
                {
                    ["OriginalImageSize"] = footer.OriginalImageSize.ToString(),
                    ["RemovedBytes"] = (checked((ulong)inputLength) - footer.OriginalImageSize).ToString(),
                });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return FirmwareOpResult.Fail(ex.Message);
        }
    }
}