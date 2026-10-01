using System.Globalization;
using UotanStudio.Firmware.Core;

namespace UotanStudio.Firmware.OtaDelta.Diff;

/// <summary>
/// Read-only format handler for bsdiff binary patches: it recognizes and inspects them, but applying or
/// creating one needs a second file and goes through <see cref="DeltaEngine"/>.
/// <para>bsdiff 二进制补丁的只读格式处理器：可识别并检视补丁，但应用或生成补丁需要第二个文件，
/// 须经 <see cref="DeltaEngine"/> 完成。</para>
/// </summary>
public sealed class DiffFormatHandler : IFormatHandler
{
    private const string BsdiffMagic = "BSDIFF40";
    private const int BsdiffHeaderSize = 32;

    /// <summary>Display name reported for this format. <para>本格式上报的显示名称。</para></summary>
    /// <value>
    /// The constant string <c>bsdiff Binary Patch</c>. <para>固定字符串 <c>bsdiff Binary Patch</c>。</para>
    /// </value>
    public string FormatName => "bsdiff Binary Patch";

    /// <summary>File-name extensions this handler claims. <para>本处理器认领的文件扩展名。</para></summary>
    /// <value>
    /// A single-element read-only list containing <c>bsdiff</c>; the list order is fixed for the handler lifetime.
    /// <para>只含 <c>bsdiff</c> 的单元素只读列表；列表顺序在处理器生命周期内固定。</para>
    /// </value>
    public IReadOnlyList<string> FileExtensions { get; } = ["bsdiff"];

    private static bool HasSupportedMagic(ReadOnlySpan<byte> header) => header.Length >= 8 &&
        (header[..8].SequenceEqual("BSDIFF40"u8) ||
         (header[..5].SequenceEqual("BSDF2"u8) && header[5] is 1 or 2 && header[6] is 1 or 2 && header[7] is 1 or 2));

    /// <summary>
    /// Recognizes BSDIFF40 and Android BSDF2 patch headers.
    /// <para>识别 BSDIFF40 和 Android BSDF2 补丁头。</para>
    /// </summary>
    /// <param name="filePath">
    /// Path probed for a supported magic; only the first eight bytes are read. <c>null</c>, empty, whitespace-only
    /// and unreadable paths are treated as a non-match rather than an error.
    /// <para>用于探测支持魔数的路径；仅读取前八个字节。<c>null</c>、空串、仅含空白以及不可读取的路径都视为不匹配，而非错误。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the file starts with the <c>BSDIFF40</c> magic, or with <c>BSDF2</c> followed by
    /// three version bytes each in the range 1–2; otherwise <see langword="false"/>.
    /// <para>文件以 <c>BSDIFF40</c> 魔数开头，或以 <c>BSDF2</c> 开头且随后三个版本字节均落在 1–2 范围时返回
    /// <see langword="true"/>；其余情况返回 <see langword="false"/>。</para>
    /// </returns>
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            var header = FormatDetector.ReadHeader(filePath, 8);
            return HasSupportedMagic(header);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads bsdiff header metadata without unpacking: patch type, file length, and the 32-byte header's control-block length, diff-block length and reconstructed new-file size.
    /// <para>不解包读取 bsdiff 头部元数据：补丁类型、文件长度，以及 32 字节头部中的控制块长度、差异块长度、 还原出的新文件大小。</para>
    /// </summary>
    /// <param name="filePath">
    /// Patch file to inspect; the first <c>32</c> bytes are read and the file size is queried, but no block is decoded.
    /// <para>待检视的补丁文件；读取前 <c>32</c> 字节并查询文件大小，不解码任何数据块。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Checked once before the file is opened and passed to the task scheduler. <para>在打开文件前检查一次，并传给任务调度器。</para>
    /// </param>
    /// <returns>
    /// A task whose result reports failure for a missing file, an unsupported magic, a negative block length, or
    /// control and diff lengths that exceed the file size. On success the summary names the three stream lengths
    /// and the metadata dictionary holds <c>PatchType</c>, <c>ControlBlockLength</c>, <c>DiffBlockLength</c>,
    /// <c>NewFileSize</c> and <c>FileLength</c> as invariant-culture decimal strings.
    /// <para>返回的任务结果在文件缺失、魔数不受支持、块长度为负，或控制块与差异块长度超出文件大小时报告失败。
    /// 成功时摘要写明三个流长度，元数据字典以固定区域十进制字符串保存 <c>PatchType</c>、<c>ControlBlockLength</c>、
    /// <c>DiffBlockLength</c>、<c>NewFileSize</c> 与 <c>FileLength</c>。</para>
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled before the work started.
    /// <para>工作开始前 <paramref name="cancellationToken"/> 已被取消。</para>
    /// </exception>
    public Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var header = FormatDetector.ReadHeader(filePath, BsdiffHeaderSize);
                if (header.Length < BsdiffHeaderSize || !HasSupportedMagic(header))
                {
                    return FirmwareOpResult.Fail($"Not a bsdiff (BSDIFF40) patch: '{filePath}'.");
                }

                var controlLength = BinaryDeltaPatch.ReadNumber(header.AsSpan(8, 8));
                var diffLength = BinaryDeltaPatch.ReadNumber(header.AsSpan(16, 8));
                var newSize = BinaryDeltaPatch.ReadNumber(header.AsSpan(24, 8));
                if (controlLength < 0 || diffLength < 0 || newSize < 0)
                {
                    return FirmwareOpResult.Fail("Invalid negative length in bsdiff header.");
                }

                var fileLength = new FileInfo(filePath).Length;
                if (controlLength > fileLength - BsdiffHeaderSize || diffLength > fileLength - BsdiffHeaderSize - controlLength)
                    return FirmwareOpResult.Fail("Binary patch streams exceed the file length.");
                var metadata = new Dictionary<string, string>
                {
                    ["PatchType"] = header.AsSpan(0, 5).SequenceEqual("BSDF2"u8) ? "bsdiff (BSDF2)" : "bsdiff (BSDIFF40)",
                    ["ControlBlockLength"] = controlLength.ToString(CultureInfo.InvariantCulture),
                    ["DiffBlockLength"] = diffLength.ToString(CultureInfo.InvariantCulture),
                    ["NewFileSize"] = newSize.ToString(CultureInfo.InvariantCulture),
                    ["FileLength"] = fileLength.ToString(CultureInfo.InvariantCulture),
                };

                var summary =
                    $"bsdiff binary patch: control {controlLength} bytes, diff {diffLength} bytes, " +
                    $"reconstructs a {newSize}-byte target file.";

                return FirmwareOpResult.Ok(summary, metadata: metadata);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail($"Failed to read bsdiff metadata from '{filePath}': {ex.Message}");
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Not supported: applying a patch needs a source file; use DeltaEngine.ApplyDeltaAsync.
    /// <para>不支持：应用补丁需要源文件；请使用 DeltaEngine.ApplyDeltaAsync。</para>
    /// </summary>
    /// <param name="filePath">
    /// Patch that would be unpacked; never opened, because the operation is refused before any file is read.
    /// <para>本应解包的补丁；由于在读取任何文件之前就拒绝该操作，因此从不被打开。</para>
    /// </param>
    /// <param name="outputDirectory">
    /// Directory that would receive the contents; it is neither created nor written to.
    /// <para>本应接收内容的目录；既不会被创建也不会被写入。</para>
    /// </param>
    /// <param name="options">
    /// Extraction options, including the overwrite policy consulted by the repack-metadata wrapper; <c>null</c>
    /// keeps the wrapper's default policy.
    /// <para>解包选项，含打包元数据包装层会参考的覆盖策略；<c>null</c> 表示沿用包装层的默认策略。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the call before the operation runs. <para>在操作运行前取消本次调用。</para>
    /// </param>
    /// <returns>
    /// A task whose result always reports failure with a message pointing at <see cref="DeltaEngine.ApplyDeltaAsync"/>;
    /// no output files and no repack metadata are written.
    /// <para>返回的任务结果始终报告失败，消息指向 <see cref="DeltaEngine.ApplyDeltaAsync"/>；不写出任何输出
    /// 文件，也不写打包元数据。</para>
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was already cancelled. <para><paramref name="cancellationToken"/> 已被取消。</para>
    /// </exception>
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
        => UotanStudio.Firmware.Core.Repacking.RepackMetadata.UnpackAsync(this, filePath, outputDirectory, options,
            cancellationToken, () => UnpackCoreAsync(filePath, outputDirectory, options, cancellationToken));

    private Task<FirmwareOpResult> UnpackCoreAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(FirmwareOpResult.Fail(
            "Applying a bsdiff patch requires a source file; use DeltaEngine.ApplyDeltaAsync."));

    /// <summary>
    /// Not supported: generating a patch needs source and target files; use DeltaEngine.GenerateDeltaAsync.
    /// <para>不支持：生成补丁需要源文件与目标文件；请使用 DeltaEngine.GenerateDeltaAsync。</para>
    /// </summary>
    /// <param name="inputPath">
    /// Directory or file that would be packed; it is never opened, because the operation is refused first.
    /// <para>本应被打包的目录或文件；由于操作先行拒绝，因此从不被打开。</para>
    /// </param>
    /// <param name="outputPath">
    /// Archive that would be created; no file is written at this path.
    /// <para>本应生成的归档；不会在该路径写出任何文件。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Unused by this call, which completes synchronously. <para>本次调用不使用该参数，其会同步完成。</para>
    /// </param>
    /// <returns>
    /// A completed task whose result reports failure with a message pointing at
    /// <see cref="DeltaEngine.GenerateDeltaAsync"/>.
    /// <para>已完成的任务，其结果报告失败，消息指向 <see cref="DeltaEngine.GenerateDeltaAsync"/>。</para>
    /// </returns>
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
        => Task.FromResult(FirmwareOpResult.Fail(
            "Generating a bsdiff patch requires source and target files; use DeltaEngine.GenerateDeltaAsync."));
}