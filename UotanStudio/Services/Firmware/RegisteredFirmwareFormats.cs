using UotanStudio.Firmware.Application.Formats;
using UotanStudio.Firmware.Core;

namespace UotanStudio.Services.Firmware;

/// <summary>Shared registered-format lookup and directory packing, without adapter-specific defaults.<para>共享的已注册格式查找与目录打包，不含适配器特定默认值。</para></summary>
internal static class RegisteredFirmwareFormats
{
    /// <summary>Looks up a registered format by name without normalizing the input.<para>按名称查找已注册格式，不对输入做规范化。</para></summary>
    /// <param name="name">Format name to match.<para>待匹配的格式名称。</para></param>
    /// <param name="comparison">String comparison rule.<para>字符串比较规则。</para></param>
    /// <returns>The matching handler, or null when absent.<para>匹配的处理程序，缺失时为空。</para></returns>
    internal static IReadonlyFormatHandler? FindByName(string name, StringComparison comparison = StringComparison.Ordinal)
        => FormatDetector.RegisteredHandlers.FirstOrDefault(handler => string.Equals(handler.FormatName, name, comparison));

    /// <summary>Packs a directory using the exact-name matching contract.<para>按精确名称匹配契约打包目录。</para></summary>
    /// <param name="format">Target format name.<para>目标格式名称。</para></param>
    /// <param name="source">Source directory path.<para>源目录路径。</para></param>
    /// <param name="output">Output archive path.<para>输出归档路径。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <param name="throwIfUnsupported">Whether to throw for an unsupported format.<para>格式不受支持时是否抛出异常。</para></param>
    /// <param name="progress">Optional byte progress reporter.<para>可选的字节进度报告器。</para></param>
    /// <returns>The packing operation result.<para>打包操作结果。</para></returns>
    internal static async Task<FirmwareOperationResult> PackDirectoryAsync(
        string format, string source, string output, CancellationToken cancellationToken = default,
        bool throwIfUnsupported = false, IProgress<FileSystemByteProgress>? progress = null)
    {
        FirmwareFormatRegistry.RegisterAll();
        if (FindByName(format) is not IWritableFormatHandler writer)
        {
            var error = $"No directory packer is registered for {format}.";
            if (throwIfUnsupported) throw new InvalidDataException(error);
            return FirmwareOperationResult.Fail(error);
        }
        var result = await writer.PackAsync(source, output, progress, cancellationToken).ConfigureAwait(false);
        return FirmwareOperationResult.From(result);
    }
}