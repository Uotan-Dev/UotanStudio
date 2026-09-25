using UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb.Models;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Streams;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb.Signing;

/// <summary>Removes a validated AVB footer, VBMeta payload, and alignment padding from a partition image.</summary>
public static class AvbFooterEditor
{
    /// <summary>Publishes the original unsigned image bytes recorded by a valid trailing AVB footer.</summary>
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