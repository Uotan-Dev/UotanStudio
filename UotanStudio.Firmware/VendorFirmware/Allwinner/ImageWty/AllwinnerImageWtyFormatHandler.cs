using UotanStudio.Firmware.Core;

namespace UotanStudio.Firmware.VendorFirmware.Allwinner.ImageWty;

/// <summary>
/// Detection, metadata reading and unpacking entry point for Allwinner IMAGEWTY boot containers.
/// <para>Allwinner IMAGEWTY 启动容器的检测、元数据读取与解包入口。</para>
/// </summary>
internal sealed class AllwinnerImageWtyFormatHandler : IReadonlyFormatHandler
{
    /// <summary>Display name of the IMAGEWTY container format.
    /// <para>IMAGEWTY 容器格式的显示名称。</para></summary>
    /// <value>The constant <c>Allwinner IMAGEWTY Image</c>, also the <see cref="FormatDetector"/> registration key.
    /// <para>常量 <c>Allwinner IMAGEWTY Image</c>，同时作为 <see cref="FormatDetector"/> 的注册键。</para></value>
    public string FormatName => "Allwinner IMAGEWTY Image";

    /// <summary>Extensions this handler claims before content inspection.
    /// <para>在内容检查之前本处理器声明认领的扩展名。</para></summary>
    /// <value><c>img</c> and <c>bin</c>; the <c>IMAGEWTY</c> magic still decides acceptance.
    /// <para><c>img</c> 与 <c>bin</c>；是否接受仍由 <c>IMAGEWTY</c> 魔数决定。</para></value>
    public IReadOnlyList<string> FileExtensions => ["img", "bin"];

    /// <summary>
    /// Determines whether the file starts with the plaintext <c>IMAGEWTY</c> magic.
    /// <para>判断文件是否以明文 <c>IMAGEWTY</c> 魔数开头。</para>
    /// </summary>
    /// <param name="filePath">Candidate file path. <para>候选文件路径。</para></param>
    /// <returns><see langword="true"/> when the first eight bytes spell <c>IMAGEWTY</c>.
    /// <para>前八字节为 <c>IMAGEWTY</c> 时为 <see langword="true"/>。</para></returns>
    public bool CanHandle(string filePath)
    {
        return HeaderProbe.ProbeMagic(filePath, 8, 0, "IMAGEWTY"u8);
    }

    /// <summary>
    /// Reads IMAGEWTY header and entry metadata without extracting payloads.
    /// <para>读取 IMAGEWTY 头与条目的元数据，不提取负载。</para>
    /// </summary>
    /// <param name="filePath">Container to inspect. <para>待检查的容器。</para></param>
    /// <param name="cancellationToken">Cancels the read. <para>取消本次读取。</para></param>
    /// <returns>Outcome carrying version words, USB ids and entry names.
    /// <para>结果对象，携带版本字、USB id 与条目名。</para></returns>
    public async Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = File.OpenRead(filePath);
            var header = AllwinnerImageWtyFormat.Read(stream, cancellationToken);
            return FirmwareOpResult.Ok(
                $"IMAGEWTY 0x{header.HeaderVersion:X}: {header.Entries.Count} files",
                entries: [.. header.Entries.Select(e => $"{e.Name} ({e.MainType}:{e.SubType})")],
                metadata: new Dictionary<string, string>
                {
                    ["HeaderVersion"] = $"0x{header.HeaderVersion:X}",
                    ["Version"] = $"0x{header.Version:X}",
                    ["ImageSize"] = header.ImageSize.ToString(),
                    ["ImageHeaderSize"] = header.ImageHeaderSize.ToString(),
                    ["Vid"] = $"0x{header.Vid:X4}",
                    ["Pid"] = $"0x{header.Pid:X4}",
                    ["HardwareId"] = $"0x{header.HardwareId:X}",
                    ["FirmwareId"] = $"0x{header.FirmwareId:X}",
                    ["FileCount"] = header.Entries.Count.ToString()
                });
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

    /// <summary>Extracts the container payloads into the output directory; zero-length entries are skipped.
    /// <para>将容器负载提取到输出目录；长度为 0 的条目会被跳过。</para></summary>
    /// <param name="filePath">Container to unpack; never modified.<para>待解包的容器；不会被修改。</para></param>
    /// <param name="outputDirectory">Destination directory, created when absent.
    /// <para>目标目录，不存在时创建。</para></param>
    /// <param name="options">Entry selection and overwrite policy; selection compares entry names case-insensitively. <see langword="null"/> extracts everything.
    /// <para>条目选择与覆盖策略；选择按条目名忽略大小写比较。传 <see langword="null"/> 时全部解出。</para></param>
    /// <param name="cancellationToken">Cancels extraction.<para>取消解包。</para></param>
    /// <returns>Outcome listing the written files with sizes.<para>结果对象列出已写入的文件及其大小。</para></returns>
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
        try
        {
            Directory.CreateDirectory(outputDirectory);
            await using var stream = File.OpenRead(filePath);
            var header = AllwinnerImageWtyFormat.Read(stream, cancellationToken);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (entry, index) in header.Entries.Select((e, i) => (e, i)))
            {
                if (entry.Length == 0 || (selected is not null && !selected.Contains(entry.Name)))
                {
                    continue;
                }

                var name = AllwinnerImageWtyFormat.SafeEntryFileName(entry, index);
                if (!used.Add(name))
                {
                    name = $"{index}_{name}";
                    used.Add(name);
                }

                var destination = Path.Combine(outputDirectory, name);
                if (!options.Overwrite && File.Exists(destination))
                {
                    throw new IOException($"Output already exists: {destination}");
                }

                await using (var output = File.Create(destination))
                {
                    AllwinnerImageWtyFormat.ExtractEntry(stream, entry, output, cancellationToken);
                }

                written.Add(name);
                components.Add(FirmwareComponent.Of(name, entry.Length));
            }

            return FirmwareOpResult.Ok($"Extracted {written.Count} files to {outputDirectory}",
                entries: written, components: components);
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
}