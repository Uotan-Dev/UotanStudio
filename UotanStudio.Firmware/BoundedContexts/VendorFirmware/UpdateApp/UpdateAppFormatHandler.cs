using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.UpdateApp;

/// <summary>
/// Inspects and extracts Huawei update.app packages; repacking is not offered.
/// <para>检查并解包华为 update.app 升级包；不提供重新打包。</para>
/// </summary>
public sealed class UpdateAppFormatHandler : IFormatHandler
{
    /// <summary>Display name of the Huawei update.app package family.
    /// <para>华为 update.app 升级包族的显示名称。</para></summary>
    /// <value>A fixed label shown in format lists.
    /// <para>格式列表中显示的固定名称。</para></value>
    public string FormatName => "Huawei update.app";

    /// <summary>File extensions owned by this handler.
    /// <para>本处理器负责的文件扩展名。</para></summary>
    /// <value>The dot-less extensions <c>app</c> and <c>bin</c>; content is confirmed by magic probing,
    /// not by the extension alone.
    /// <para>不带点号的扩展名 <c>app</c> 与 <c>bin</c>；内容是否受支持由魔数探测确认，而非仅看扩展名。</para></value>
    public IReadOnlyList<string> FileExtensions => ["app", "bin"];

    /// <summary>
    /// Determines whether the file matches this format.
    /// <para>判断文件是否匹配此格式。</para>
    /// </summary>
    /// <param name="filePath">Path whose first 4,096 bytes are scanned byte by byte for the update.app magic in
    /// any 4-byte window.
    /// <para>逐字节扫描前 4096 字节、在任意 4 字节窗口中查找 update.app 魔数的路径。</para></param>
    /// <returns><see langword="true"/> when the magic appears within the probe window; <see langword="false"/>
    /// for blank paths, missing files and any read error.
    /// <para>魔数出现在探测窗口内时为 <see langword="true"/>；路径为空、文件缺失或读取异常时为
    /// <see langword="false"/>。</para></returns>
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }
        try
        {
            using var fs = File.OpenRead(filePath);
            const int probeLimit = 4096;
            Span<byte> window = stackalloc byte[4];
            Span<byte> candidate = stackalloc byte[4];
            var read = 0;
            while (read < probeLimit && fs.ReadByte() is var value && value >= 0)
            {
                window[read % 4] = (byte)value;
                read++;
                if (read >= 4)
                {
                    for (var i = 0; i < 4; i++)
                        candidate[i] = window[(read - 4 + i) % 4];
                    if (candidate.SequenceEqual(UpdateAppProcessor.MagicBytes))
                        return true;
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the firmware metadata asynchronously.
    /// <para>异步读取固件元数据。</para>
    /// </summary>
    /// <param name="filePath">update.app package whose partition entries are enumerated.
    /// <para>待枚举分区块项的 update.app 包。</para></param>
    /// <param name="cancellationToken">Accepted for interface uniformity; entry enumeration itself is
    /// synchronous.
    /// <para>为接口一致性而保留；条目枚举本身是同步的。</para></param>
    /// <returns>An <see cref="FirmwareOpResult"/> listing the entry names and reporting the entry count, or a
    /// failed result carrying the parse error message.
    /// <para>返回列出条目名称并报告条目数的 <see cref="FirmwareOpResult"/>，或携带解析错误信息的失败结果。</para></returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled inside
    /// the enumeration.
    /// <para>枚举过程中 <paramref name="cancellationToken"/> 被触发。</para></exception>
    public Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var entries = UpdateAppProcessor.GetEntries(filePath).ToList();
            var summary = $"Huawei update.app: {entries.Count} partitions";
            return Task.FromResult(FirmwareOpResult.Ok(summary,
                entries: [.. entries.Select(e => e.Name)],
                metadata: new Dictionary<string, string> { ["EntryCount"] = entries.Count.ToString() }));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail(ex.Message));
        }
    }

    /// <summary>
    /// Extracts the firmware contents asynchronously.
    /// <para>异步提取固件内容。</para>
    /// </summary>
    /// <param name="filePath">
    /// update.app package to extract.
    /// <para>待解包的 update.app 包。</para>
    /// </param>
    /// <param name="outputDirectory">
    /// Directory receiving the extracted partitions and, on success, the repack metadata file.
    /// <para>接收解出分区以及成功后打包元数据文件的目录。</para>
    /// </param>
    /// <param name="options">
    /// Extraction policy; <c>null</c> uses defaults. <c>SelectedEntries</c>, when set, restricts extraction to
    /// those exact entry names, and <c>Overwrite</c> controls replacement of existing files.
    /// <para>解包策略；为 <c>null</c> 时使用默认值。设置 <c>SelectedEntries</c> 时仅解出这些精确名称的条目，
    /// <c>Overwrite</c> 决定是否替换已存在文件。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the extraction; cancellation is rethrown rather than converted to a failed result.
    /// <para>取消解包；取消会向上抛出，而不是转换为失败结果。</para>
    /// </param>
    /// <returns>
    /// An <see cref="FirmwareOpResult"/> reporting the extracted partition count, or a failed result carrying the
    /// error message.
    /// <para>返回报告解出分区数的 <see cref="FirmwareOpResult"/>，或携带错误信息的失败结果。</para>
    /// </returns>
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
        => UotanStudio.Firmware.SharedKernel.Repacking.RepackMetadata.UnpackAsync(this, filePath, outputDirectory, options,
            cancellationToken, () => UnpackCoreAsync(filePath, outputDirectory, options, cancellationToken));

    private Task<FirmwareOpResult> UnpackCoreAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var selected = options?.CreateSelectedEntrySet();
            var count = UpdateAppProcessor.Extract(filePath, outputDirectory,
                selected?.ToList(),
                overwrite: options?.Overwrite ?? true, cancellationToken: cancellationToken);
            return Task.FromResult(FirmwareOpResult.Ok($"Extracted {count} partitions to {outputDirectory}"));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail(ex.Message));
        }
    }

    /// <summary>
    /// Packs the input into firmware asynchronously.
    /// <para>异步将输入打包为固件。</para>
    /// </summary>
    /// <param name="inputPath">Ignored; no source layout is accepted for this format.
    /// <para>被忽略；此格式不接受任何源目录布局。</para></param>
    /// <param name="outputPath">Ignored; nothing is written.
    /// <para>被忽略；不会写出任何文件。</para></param>
    /// <param name="cancellationToken">Ignored; the call completes immediately.
    /// <para>被忽略；调用立即完成。</para></param>
    /// <returns>An always-failed <see cref="FirmwareOpResult"/> stating that update.app packing is not
    /// supported; no exception escapes for unsupported packing.
    /// <para>始终失败的 <see cref="FirmwareOpResult"/>，说明不支持打包 update.app；不支持的打包不会抛出异常。</para></returns>
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default) => Task.FromResult(FirmwareOpResult.Fail("update.app packing is not supported by this handler."));
}