using UotanStudio.Firmware.BootSecurity.Patcher;

namespace UotanStudio.Services.Flash;

/// <summary>Prepares a supported Root-patched boot image without modifying the source.<para>在不改动源文件的前提下准备受支持的 Root 修补 boot 镜像。</para></summary>
public static class FlashRootImage
{
    /// <summary>Reports whether a detection result names a patcher usable without extra secrets.<para>判断检测结果指向的修补方式是否无需额外密钥。</para></summary>
    /// <param name="detection">Detection result describing the package patcher.<para>描述该包修补方式的检测结果。</para></param>
    /// <returns>True when this flow can patch the package.<para>当前流程可以修补该包时为真。</para></returns>
    public static bool IsSupportedPackage(BootPatchDetection detection)
        => detection.IsSupported &&
            (detection.Kind is BootPatchKind.Magisk or BootPatchKind.KernelSu ||
                detection.Kind == BootPatchKind.KernelSuLkm &&
                detection.Architectures.Any(arch => arch is "arm64" or "arm32" or "x64" or "x86"));

    /// <summary>Returns a temporary image owned by the caller after native package validation.<para>完成包校验后返回由接收方持有的临时镜像。</para></summary>
    /// <param name="packagePath">Root package file supplying the patch.<para>提供修补内容的 Root 包文件。</para></param>
    /// <param name="bootPath">Boot image file to patch.<para>要修补的 boot 镜像文件。</para></param>
    /// <param name="ct">Token cancelling the patch.<para>用于取消修补的令牌。</para></param>
    /// <returns>Path of the patched temporary image.<para>修补后的临时镜像路径。</para></returns>
    public static async Task<string> PrepareAsync(string packagePath, string bootPath, CancellationToken ct)
    {
        var output = Path.Combine(Path.GetTempPath(), "uotan-root-" + Guid.NewGuid().ToString("N") + ".img");
        try
        {
            var patcher = new BootPatchService();
            var detection = await patcher.DetectAsync(packagePath, ct).ConfigureAwait(false);
            if (!IsSupportedPackage(detection))
            {
                throw new InvalidDataException("The selected package is not a supported Magisk or KernelSU package.");
            }

            await patcher.PatchAsync(packagePath, bootPath, output, cancellationToken: ct).ConfigureAwait(false);
            if (!File.Exists(output) || new FileInfo(output).Length == 0)
            {
                throw new InvalidDataException("The Root patch did not produce a boot image.");
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