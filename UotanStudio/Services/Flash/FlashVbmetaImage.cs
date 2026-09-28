using UotanStudio.Firmware.BootSecurity.Avb.Signing;

namespace UotanStudio.Services.Flash;

/// <summary>Stages a verification-disabled VBMeta image without changing the source.</summary>
public static class FlashVbmetaImage
{
    /// <summary>Returns a new temporary image owned by the caller.</summary>
    public static async Task<string> PrepareAsync(string source, CancellationToken ct)
    {
        var output = Path.Combine(Path.GetTempPath(), "uotan-vbmeta-" + Guid.NewGuid().ToString("N") + ".img");
        try
        {
            var result = await AvbSigner.DisableVbmetaVerificationAsync(source, output, ct).ConfigureAwait(false);
            if (!result.Success)
            {
                throw new InvalidDataException(result.ErrorMessage ?? "Failed to disable VBMeta verification.");
            }
            ct.ThrowIfCancellationRequested();
            return output;
        }
        catch
        {
            if (File.Exists(output)) File.Delete(output);
            throw;
        }
    }
}