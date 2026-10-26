using System.Globalization;
using UotanStudio.Firmware.Core;

namespace UotanStudio.Firmware.VendorFirmware.Coolpad.Cpb;

/// <summary>
/// Detection, metadata reading and unpacking entry point for Coolpad CPB firmware packages.
/// <para>酷派 CPB 固件包的检测、元数据读取与解包入口。</para>
/// </summary>
/// <remarks>
/// CPB is a read-only container: this handler lists and extracts the stored images and never repacks.
/// <para>CPB 是只读容器：本处理器只列举并提取已存储的镜像，不会重打包。</para>
/// </remarks>
internal sealed class CpbFormatHandler : IReadonlyFormatHandler
{
    /// <summary>Display name of the Coolpad CPB package format.
    /// <para>酷派 CPB 包格式的显示名称。</para></summary>
    /// <value>The constant <c>Coolpad CPB Package</c>, also the <see cref="FormatDetector"/> registration key.
    /// <para>常量 <c>Coolpad CPB Package</c>，同时作为 <see cref="FormatDetector"/> 的注册键。</para></value>
    public string FormatName => "Coolpad CPB Package";

    /// <summary>Extensions this handler claims before content inspection.
    /// <para>在内容检查之前本处理器声明认领的扩展名。</para></summary>
    /// <value>The single extension <c>cpb</c>; the <c>CP</c> magic with version 1.7 still decides acceptance.
    /// <para>仅有 <c>cpb</c> 一个扩展名；是否接受仍由 <c>CP</c> 魔数与 1.7 版本决定。</para></value>
    public IReadOnlyList<string> FileExtensions => ["cpb"];

    /// <summary>
    /// Determines whether the file starts with the <c>CP</c> magic followed by the supported 1.7 version bytes.
    /// <para>判断文件是否以 <c>CP</c> 魔数加受支持的 1.7 版本字节开头。</para>
    /// </summary>
    /// <param name="filePath">Candidate file path; blank or absent paths never match.
    /// <para>候选文件路径；空白或不存在的路径绝不匹配。</para></param>
    /// <returns><see langword="true"/> when the first four bytes are <c>CP\x01\x07</c> and the file is at least one
    /// header long. <para>前四字节为 <c>CP\x01\x07</c> 且文件不短于一个头部时为 <see langword="true"/>。</para></returns>
    public bool CanHandle(string filePath)
        => HeaderProbe.ProbeMagic(filePath, CpbFormat.HeaderSize, 0, CpbFormat.Magic);

    /// <summary>
    /// Reads the container header and image directory without extracting payloads.
    /// <para>读取容器头与镜像目录，不提取负载。</para>
    /// </summary>
    /// <param name="filePath">Container to inspect; the file is opened read-only.
    /// <para>待检查的容器；文件以只读方式打开。</para></param>
    /// <param name="cancellationToken">Cancels the read between image headers.
    /// <para>在镜像头之间取消本次读取。</para></param>
    /// <returns>Outcome carrying the version, target strings, image count, stored and recomputed checksums, and the
    /// sanitized image names in <see cref="FirmwareOpResult.Entries"/>.
    /// <para>结果对象携带版本、适用信息、镜像数量、存储与重算的校验和，以及 <see cref="FirmwareOpResult.Entries"/>
    /// 中净化后的镜像名称。</para></returns>
    public async Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = OpenContainer(filePath);
            var archive = CpbFormat.Read(stream, stream.Length, cancellationToken);
            var header = archive.Header;
            var metadata = new Dictionary<string, string>
            {
                ["CpbVersion"] = header.VersionText,
                ["FileCount"] = archive.Images.Count.ToString(CultureInfo.InvariantCulture),
                ["HeaderSize"] = header.ImageHeaderEndPosition.ToString(CultureInfo.InvariantCulture),
                ["Checksum"] = "0x" + header.CheckSum.ToString("X8", CultureInfo.InvariantCulture),
                ["ChecksumStatus"] = header.CheckSumMatches ? "Verified" : "Mismatch",
            };
            AddWhenPresent(metadata, "Model", header.Model);
            AddWhenPresent(metadata, "HardwareVersion", header.HardwareVersion);
            AddWhenPresent(metadata, "Version", header.Version);
            AddWhenPresent(metadata, "RomFrom", header.RomFrom);

            return FirmwareOpResult.Ok(
                $"Coolpad CPB {header.VersionText}: {archive.Images.Count} images",
                entries: [.. archive.Images.Select(image => image.Name)],
                metadata: metadata);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or OverflowException)
        {
            return FirmwareOpResult.Fail(exception.Message);
        }
    }

    /// <summary>
    /// Extracts the stored images into the output directory.
    /// <para>将已存储的镜像提取到输出目录。</para>
    /// </summary>
    /// <param name="filePath">Container to unpack; never modified.
    /// <para>待解包的容器；不会被修改。</para></param>
    /// <param name="outputDirectory">Destination directory, created when absent.
    /// <para>目标目录，不存在时创建。</para></param>
    /// <param name="options">Entry selection, per-entry size ceiling, overwrite policy and progress sink;
    /// <see langword="null"/> extracts everything with defaults. Selection compares the image names reported by
    /// <see cref="ReadMetadataAsync"/> case-insensitively.
    /// <para>条目选择、单条目大小上限、覆盖策略与进度回调；传 <see langword="null"/> 时按默认值全部解出。
    /// 选择按 <see cref="ReadMetadataAsync"/> 报告的镜像名忽略大小写比较。</para></param>
    /// <param name="cancellationToken">Cancels between images and inside a single copy; a cancelled extraction
    /// surfaces as an <see cref="OperationCanceledException"/> and leaves no partially written image behind.
    /// <para>在镜像之间与单次拷贝内部取消；取消会以 <see cref="OperationCanceledException"/> 呈现，且不会残留写了一半的镜像。</para></param>
    /// <returns>Outcome listing the written file names with their sizes; images whose recorded checksum does not
    /// match are still written but named in the summary as checksum failures.
    /// <para>结果对象列出已写入的文件名及其大小；记录校验和不一致的镜像仍会写出，但在摘要中列为校验失败。</para></returns>
    /// <exception cref="OperationCanceledException">The token was canceled.
    /// <para>令牌被取消。</para></exception>
    public async Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory,
        UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new UnpackOptions();
        var selected = options.CreateSelectedEntrySet();
        var written = new List<string>();
        var components = new List<FirmwareComponent>();
        var mismatched = new List<string>();
        try
        {
            Directory.CreateDirectory(outputDirectory);
            await using var stream = OpenContainer(filePath);
            var archive = CpbFormat.Read(stream, stream.Length, cancellationToken);
            if (!archive.Header.CheckSumMatches)
            {
                return FirmwareOpResult.Fail(
                    $"CPB checksum mismatch: stored 0x{archive.Header.CheckSum:X8}, computed 0x{archive.Header.ComputedCheckSum:X8}.");
            }

            var total = Math.Max(1, archive.Images.Count(image => image.Size > 0 && IsSelected(selected, image)));
            var buffer = new byte[1024 * 1024];
            foreach (var image in archive.Images)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsSelected(selected, image))
                {
                    continue;
                }

                if (image.Size == 0)
                {
                    continue;
                }

                if (image.Size > options.MaximumOutputBytes)
                {
                    throw new InvalidDataException(
                        $"CPB image '{image.Name}' declares {image.Size} bytes, above the {options.MaximumOutputBytes}-byte limit.");
                }

                var destination = Path.Combine(outputDirectory, image.Name);
                if (!options.Overwrite && File.Exists(destination))
                {
                    throw new IOException($"Output already exists: {destination}");
                }

                var staging = destination + ".cpb-" + Guid.NewGuid().ToString("N");
                try
                {
                    var checksum = await CopyImageAsync(stream, image, staging, buffer, cancellationToken).ConfigureAwait(false);
                    if ((checksum & 0xFFFF) != (image.CheckSum & 0xFFFF))
                    {
                        mismatched.Add(image.Name);
                    }

                    File.Move(staging, destination, overwrite: true);
                }
                finally
                {
                    if (File.Exists(staging))
                    {
                        File.Delete(staging);
                    }
                }

                written.Add(image.Name);
                components.Add(FirmwareComponent.Of(image.Name, image.Size));
                options.Progress?.Report(written.Count * 100.0 / total, image.Name);
            }

            var summary = $"Extracted {written.Count} images from CPB to {outputDirectory}";
            if (mismatched.Count > 0)
            {
                summary += $"; {mismatched.Count} image(s) failed checksum: {string.Join(", ", mismatched)}";
            }

            return FirmwareOpResult.Ok(summary, entries: written, components: components);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or OverflowException)
        {
            return FirmwareOpResult.Fail(exception.Message);
        }
    }

    private static bool IsSelected(HashSet<string>? selected, CpbImage image)
        => selected is null || selected.Contains(image.Name);

    private static FileStream OpenContainer(string filePath)
        => new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);

    private static async Task<uint> CopyImageAsync(Stream stream, CpbImage image, string destination, byte[] buffer,
        CancellationToken cancellationToken)
    {
        stream.Position = image.Offset;
        var remaining = (long)image.Size;
        uint checksum = 0;
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = (int)Math.Min(buffer.Length, remaining);
            var read = await stream.ReadAsync(buffer.AsMemory(0, chunk), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                throw new InvalidDataException($"CPB image '{image.Name}' ends before its declared {image.Size} bytes.");
            }

            checksum = CpbFormat.Crc16(buffer.AsSpan(0, read), checksum);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }

        return checksum;
    }

    private static void AddWhenPresent(IDictionary<string, string> metadata, string key, string value)
    {
        if (value.Length > 0)
        {
            metadata[key] = value;
        }
    }
}