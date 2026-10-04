using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Firmware.Core.Streams;

namespace UotanStudio.Services.Flash;

/// <summary>Captures a download-agent memory window into a published file.
/// <para>把下载代理的内存窗口采集为一份已发布的文件。</para></summary>
/// <remarks>
/// <para>This is the host counterpart of the reference <c>dumpbrom</c> command: it walks the documented
/// boot-ROM window with the family's own capture loop, appends every step to a staging file while hashing
/// it incrementally, and replaces the destination only after the staged byte count equals the requested
/// length. A failure therefore leaves any pre-existing image untouched, and no window is ever buffered in
/// memory.</para>
/// <para>这是参考 <c>dumpbrom</c> 命令在宿主机侧的对应实现：使用该家族自身的采集循环遍历已记载的 boot-ROM
/// 窗口，把每个步长追加到暂存文件并增量哈希，只有暂存字节数与请求长度相等时才替换目标。因此失败时既有镜像
/// 保持不变，且任何窗口都不会整体缓冲在内存中。</para>
/// <para>Only the boot-ROM window described by the reference dump loop is offered. The reference
/// <c>memdump</c> sibling additionally walks a DRAM span of <c>0x1_0000_0000 - base</c>, an SRAM span of
/// <c>0x11200000</c> and an eFuse span of <c>0x10000</c>, but it reads them through a page loop that
/// swallows every per-page exception (Library/DA/mtk_da_handler.py:784-812 with :597-630), so no bounded,
/// failure-reporting framing exists for those windows and they are refused instead of invented.</para>
/// <para>本服务只提供参考采集循环所描述的 boot-ROM 窗口。参考的 <c>memdump</c> 兄弟命令还会遍历
/// <c>0x1_0000_0000 - base</c> 的 DRAM 范围、<c>0x11200000</c> 的 SRAM 范围与 <c>0x10000</c> 的 eFuse
/// 范围，但它通过会吞掉每次分页异常的循环读取这些区域
/// （Library/DA/mtk_da_handler.py:784-812，配合 :597-630），因此这些窗口没有可报告失败的有界帧结构，此处
/// 直接拒绝而不是凭空构造。</para>
/// </remarks>
internal static class MtkMemoryCaptureService
{
    private const string BromRegionName = "BROM";

    /// <summary>Captures the whole documented boot-ROM window to a file.
    /// <para>把完整的已记载 boot-ROM 窗口采集到文件。</para></summary>
    /// <param name="session">A session already at a download-agent stage of the named family.
    /// <para>已处于所指代理家族下载代理阶段的会话。</para></param>
    /// <param name="family">The agent family whose capture framing is used; no fallback occurs.
    /// <para>使用其采集帧结构的代理家族；不发生回退。</para></param>
    /// <param name="destinationPath">Destination file inside an existing directory; replaced only on
    /// success. <para>位于已存在目录中的目标文件；仅在成功时被替换。</para></param>
    /// <param name="progress">Optional per-step progress; full progress is not publication.
    /// <para>可选的按步进度；进度跑满并不等于发布成功。</para></param>
    /// <param name="ct">Cancellation honoured before the first device byte and between every step.
    /// <para>在首个设备字节之前以及每步之间都会响应的取消令牌。</para></param>
    /// <returns>The published image description. 已发布镜像的描述信息。</returns>
    internal static Task<MtkMemoryCaptureRecord> CaptureAsync(MtkSession session, MtkDaProtocol family,
        string destinationPath, IProgress<FlashProgress>? progress, CancellationToken ct)
        => CaptureAsync(session, family, MtkDaMemoryRange.BromDump, destinationPath, progress, ct);

    /// <summary>Captures a bounded sub-window of the documented boot-ROM range to a file.
    /// <para>把已记载 boot-ROM 范围内的一个有界子窗口采集到文件。</para></summary>
    /// <param name="session">A session already at a download-agent stage of the named family.
    /// <para>已处于所指代理家族下载代理阶段的会话。</para></param>
    /// <param name="family">The agent family whose capture framing is used; no fallback occurs.
    /// <para>使用其采集帧结构的代理家族；不发生回退。</para></param>
    /// <param name="range">The step-aligned window to walk, validated before any device byte.
    /// <para>要遍历的步长对齐窗口，在任何设备字节之前完成校验。</para></param>
    /// <param name="destinationPath">Destination file inside an existing directory; replaced only on
    /// success. <para>位于已存在目录中的目标文件；仅在成功时被替换。</para></param>
    /// <param name="progress">Optional per-step progress; full progress is not publication.
    /// <para>可选的按步进度；进度跑满并不等于发布成功。</para></param>
    /// <param name="ct">Cancellation honoured before the first device byte and between every step.
    /// <para>在首个设备字节之前以及每步之间都会响应的取消令牌。</para></param>
    /// <returns>The published image description. 已发布镜像的描述信息。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.
    /// <para><paramref name="session"/> 为 null。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="destinationPath"/> is empty, or the range is not
    /// step-aligned. <para><paramref name="destinationPath"/> 为空，或范围未按步长对齐。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">The family is undefined or the range is empty,
    /// oversized or leaves the 32-bit address space. <para>家族未定义，或范围为空、超限、越出 32
    /// 位地址空间。</para></exception>
    /// <exception cref="NotSupportedException">The range reaches outside the documented boot-ROM window.
    /// <para>范围越出已记载的 boot-ROM 窗口。</para></exception>
    /// <exception cref="DirectoryNotFoundException">The destination directory does not exist.
    /// <para>目标目录不存在。</para></exception>
    /// <exception cref="IOException">The staged length did not match the requested length, or publication
    /// failed. <para>暂存长度与请求长度不符，或发布失败。</para></exception>
    /// <exception cref="InvalidOperationException">The session is not at a capture-ready stage of the named
    /// family. <para>会话未处于所指家族可采集的阶段。</para></exception>
    /// <exception cref="MtkProtocolException">The agent malformed a capture exchange; the family faults.
    /// <para>代理破坏了某次采集交互；该家族进入 fault 状态。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested before the first device byte
    /// or at any cooperative point; the destination is left untouched.
    /// <para>在首个设备字节之前或任一协作点请求了取消；目标保持不变。</para></exception>
    internal static async Task<MtkMemoryCaptureRecord> CaptureAsync(MtkSession session, MtkDaProtocol family,
        MtkDaMemoryRange range, string destinationPath, IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        range.Validate(nameof(range));
        if (family is not (MtkDaProtocol.XFlash or MtkDaProtocol.Legacy or MtkDaProtocol.Xml))
        {
            throw new ArgumentOutOfRangeException(nameof(family),
                "Memory capture supports the XFlash, XML and Legacy download agent families.");
        }

        if (range.EndAddress > MtkDaMemoryAccess.BromDumpStartAddress + (long)MtkDaMemoryAccess.BromDumpLengthBytes)
        {
            throw new NotSupportedException(
                $"The window 0x{range.StartAddress:X8}+0x{range.LengthBytes:X} is outside the reference boot-ROM " +
                "capture range, whose wider memory-dump siblings have no failure-reporting framing to mirror.");
        }

        var destination = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(destination)
            ?? throw new DirectoryNotFoundException($"The capture destination '{destination}' has no parent directory.");
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"The capture destination directory '{directory}' does not exist.");
        }

        ct.ThrowIfCancellationRequested();
        var stagingPath = destination + ".uotan-" + Guid.NewGuid().ToString("N");
        long captured;
        string sha256Hex;
        progress?.Report(new FlashProgress(BromRegionName, 0,
            $"Capturing 0x{range.LengthBytes:X} bytes from 0x{range.StartAddress:X8} in " +
            $"{MtkDaMemoryAccess.DumpStepBytes:X}-byte steps..."));
        var stepProgress = progress is null
            ? null
            : new StepProgress(range.LengthBytes, BromRegionName, progress);
        try
        {
            await using (var staging = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await using var hashingDestination = new HashingWriteStream(staging, leaveOpen: true);
                captured = family switch
                {
                    MtkDaProtocol.XFlash => await session.DumpXFlashBromAsync(hashingDestination, range, stepProgress, ct)
                        .ConfigureAwait(false),
                    MtkDaProtocol.Legacy => await session.DumpLegacyBromAsync(hashingDestination, range, stepProgress, ct)
                        .ConfigureAwait(false),
                    _ => await session.DumpXmlBromAsync(hashingDestination, range, stepProgress, ct).ConfigureAwait(false)
                };

                if (captured != range.LengthBytes || staging.Length != range.LengthBytes)
                {
                    throw new IOException(
                        $"The staged {family} capture holds {staging.Length} bytes after a reported {captured} but " +
                        $"{range.LengthBytes} were requested, so the destination was left untouched.");
                }

                sha256Hex = Convert.ToHexString(hashingDestination.GetHashAndReset());
            }

            ct.ThrowIfCancellationRequested();
            File.Move(stagingPath, destination, overwrite: true);
        }
        finally
        {
            DiscardStaging(stagingPath);
        }

        progress?.Report(new FlashProgress(BromRegionName, 100,
            $"Published {captured} bytes of the {family} boot-ROM window with SHA-256 {sha256Hex}."));
        return new MtkMemoryCaptureRecord(family, BromRegionName, range.StartAddress, range.LengthBytes,
            MtkDaMemoryAccess.DumpStepBytes, captured, sha256Hex, destination);
    }

    /// <summary>Builds the reference boot-ROM file name inside a directory from the session identity.
    /// <para>依据会话身份信息，在某目录内构造参考实现的 boot-ROM 文件名。</para></summary>
    /// <remarks>
    /// The reference names the dump <c>brom_{hex(hwcode)[2:]}.bin</c> from the hardware code it cached for
    /// the target (Library/DA/mtk_da_handler.py:822-824); the host reads the same code from the completed
    /// boot handshake instead of a side file.
    /// 参考实现按其为目标缓存的硬件码命名为 <c>brom_{hex(hwcode)[2:]}.bin</c>
    /// （Library/DA/mtk_da_handler.py:822-824）；宿主机改为从已完成的 boot 握手读取同一硬件码，而不是读取旁路文件。
    /// </remarks>
    /// <param name="session">An initialized session whose boot handshake identity is known.
    /// <para>已完成 boot 握手、身份已知的会话。</para></param>
    /// <param name="directory">An existing directory to place the file in.
    /// <para>用于放置文件的已存在目录。</para></param>
    /// <returns>The absolute destination path. 绝对目标路径。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="session"/> is null.
    /// <para><paramref name="session"/> 为 null。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is empty or whitespace.
    /// <para><paramref name="directory"/> 为空或仅空白。</para></exception>
    /// <exception cref="InvalidOperationException">The boot handshake did not report an identity yet.
    /// <para>boot 握手尚未上报身份信息。</para></exception>
    internal static string ResolveBromDestinationPath(MtkSession session, string directory)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var identity = session.DeviceInfo
            ?? throw new InvalidOperationException(
                "The boot handshake has not reported a hardware code, so the capture file cannot be named.");
        return Path.Combine(directory, MtkDaMemoryAccess.GetBromDumpFileName(identity.HardwareCode));
    }

    private static void DiscardStaging(string stagingPath)
    {
        try
        {
            if (File.Exists(stagingPath))
            {
                File.Delete(stagingPath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Converts the agent's cumulative step byte count into a percentage progress message.
    /// <para>把代理累计的步长字节数转换为百分比进度消息。</para></summary>
    private sealed class StepProgress(int totalBytes, string regionName, IProgress<FlashProgress> sink)
        : IProgress<long>
    {
        public void Report(long value)
        {
            var percent = (int)Math.Clamp(Math.Round(value / (double)totalBytes * 99,
                MidpointRounding.AwayFromZero), 0, 99);
            sink.Report(new FlashProgress(regionName, percent,
                $"Captured {value} of {totalBytes} bytes..."));
        }
    }
}

/// <summary>Describes one published download-agent capture window.
/// <para>描述一个已发布的下载代理采集窗口。</para></summary>
/// <param name="Family">The agent family that served the capture.
/// <para>提供本次采集的代理家族。</para></param>
/// <param name="RegionName">The window name used in progress reports.
/// <para>进度报告使用的窗口名称。</para></param>
/// <param name="StartAddress">The inclusive first address that was walked.
/// <para>实际遍历的起始（含）地址。</para></param>
/// <param name="RequestedBytes">The byte count the caller requested. <para>调用方请求的字节数。</para></param>
/// <param name="StepBytes">The address step one capture command covered.
/// <para>单条采集命令覆盖的地址步长。</para></param>
/// <param name="ByteLength">The published byte count. <para>已发布的字节数。</para></param>
/// <param name="Sha256Hex">The upper-case SHA-256 of the published bytes.
/// <para>已发布字节的大写 SHA-256。</para></param>
/// <param name="DestinationPath">The absolute published path. <para>已发布的绝对路径。</para></param>
internal sealed record MtkMemoryCaptureRecord(MtkDaProtocol Family, string RegionName, uint StartAddress,
    int RequestedBytes, int StepBytes, long ByteLength, string Sha256Hex, string DestinationPath);