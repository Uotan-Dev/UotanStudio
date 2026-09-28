using UotanStudio.Firmware.Core;

namespace UotanStudio.Firmware.VendorFirmware.Meizu.Mpk;

/// <summary>
/// Detection, metadata reading and unpacking entry point for Meizu MPK packages.
/// <para>Meizu MPK 包的检测、元数据读取与解包入口。</para>
/// </summary>
internal sealed class MpkFormatHandler : IReadonlyFormatHandler
{
    /// <summary>Display name of the MPK package format.
    /// <para>MPK 包格式的显示名称。</para></summary>
    /// <value>The constant <c>Meizu MPK Package</c>, also the <see cref="FormatDetector"/> registration key.
    /// <para>常量 <c>Meizu MPK Package</c>，同时作为 <see cref="FormatDetector"/> 的注册键。</para></value>
    public string FormatName => "Meizu MPK Package";

    /// <summary>Extensions this handler claims before content inspection.
    /// <para>在内容检查之前本处理器声明认领的扩展名。</para></summary>
    /// <value>The single extension <c>mpk</c>; the <c>!MPK</c> magic still decides acceptance.
    /// <para>仅有 <c>mpk</c> 一个扩展名；是否接受仍由 <c>!MPK</c> 魔数决定。</para></value>
    public IReadOnlyList<string> FileExtensions => ["mpk"];

    /// <summary>
    /// Determines whether the file starts with the <c>!MPK</c> archive magic.
    /// <para>判断文件是否以 <c>!MPK</c> 归档魔数开头。</para>
    /// </summary>
    /// <param name="filePath">Candidate file path; blank or absent paths never match.
    /// <para>候选文件路径；空白或不存在的路径绝不匹配。</para></param>
    /// <returns><see langword="true"/> when the first four bytes are <c>!MPK</c>.
    /// <para>前四字节为 <c>!MPK</c> 时为 <see langword="true"/>。</para></returns>
    public bool CanHandle(string filePath)
    {
        return HeaderProbe.ProbeMagic(filePath, 4, 0, "!MPK"u8);
    }

    /// <summary>
    /// Reads MPK header metadata without extracting payloads.
    /// <para>读取 MPK 头元数据，不提取负载。</para>
    /// </summary>
    /// <param name="filePath">Archive to inspect. <para>待检查的归档。</para></param>
    /// <param name="cancellationToken">Cancels the read. <para>取消本次读取。</para></param>
    /// <returns>Outcome carrying version, CRC32, identifier, name and entry names.
    /// <para>结果对象，携带版本、CRC32、标识、名称与条目名。</para></returns>
    public async Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = File.OpenRead(filePath);
            var archive = MpkFormat.Read(stream, cancellationToken);
            return FirmwareOpResult.Ok(
                $"MPK v{archive.Header.Version}: {archive.Entries.Count} files",
                entries: [.. archive.Entries.Select(e => e.Path)],
                metadata: new Dictionary<string, string>
                {
                    ["Version"] = archive.Header.Version.ToString(),
                    ["Crc32"] = $"0x{archive.Header.Crc32:X8}",
                    ["Identifier"] = archive.Header.Identifier,
                    ["Name"] = archive.Header.Name,
                    ["SupportSystem"] = archive.Header.SupportSystem,
                    ["SupportMachine"] = archive.Header.SupportMachine,
                    ["FileCount"] = archive.Header.FileCount.ToString()
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

    /// <summary>
    /// Extracts the archive payloads into the output directory.
    /// <para>将归档负载提取到输出目录。</para>
    /// </summary>
    /// <param name="filePath">Archive to unpack; never modified. <para>待解包的归档；不会被修改。</para></param>
    /// <param name="outputDirectory">Destination directory, created when absent.
    /// <para>目标目录，不存在时创建。</para></param>
    /// <param name="options">Entry selection and overwrite policy; <see langword="null"/> extracts everything.
    /// Selection compares the stored paths case-insensitively.
    /// <para>条目选择与覆盖策略；传 <see langword="null"/> 时全部解出。选择按存储路径忽略大小写比较。</para></param>
    /// <param name="cancellationToken">Cancels extraction; nothing partial is claimed as success.
    /// <para>取消解包；部分完成不会被声称为成功。</para></param>
    /// <returns>Outcome listing the written file names with their sizes.
    /// <para>结果对象列出已写入的文件名及其大小。</para></returns>
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
            var archive = MpkFormat.Read(stream, cancellationToken);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (entry, index) in archive.Entries.Select((e, i) => (e, i)))
            {
                if (selected is not null && !selected.Contains(entry.Path))
                {
                    continue;
                }

                var name = MpkFormat.SafeEntryFileName(entry, index);
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
                    MpkFormat.ExtractEntry(stream, entry, output, cancellationToken);
                }

                written.Add(name);
                components.Add(FirmwareComponent.Of(name, entry.Size));
                options.Progress?.Report(written.Count * 100.0 / archive.Entries.Count, name);
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