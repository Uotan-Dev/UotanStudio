using UotanStudio.Firmware.BootSecurity.Avb.Signing;

namespace UotanStudio.Services.Flash;

/// <summary>Stages a verification-disabled VBMeta image without changing the source.
/// <para>在不改动源文件的前提下暂存关闭校验的 VBMeta 镜像。</para></summary>
public static class FlashVbmetaImage
{
    /// <summary>Returns a new temporary image owned by the caller.
    /// <para>返回一份全新的临时镜像，由接收方负责管理。</para></summary>
    /// <param name="source">The source VBMeta image path. <para>源 VBMeta 镜像路径。</para></param>
    /// <param name="ct">Cancellation for staging. <para>暂存过程的取消令牌。</para></param>
    /// <returns>Path of the new temporary image file. <para>新临时镜像文件的路径。</para></returns>
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