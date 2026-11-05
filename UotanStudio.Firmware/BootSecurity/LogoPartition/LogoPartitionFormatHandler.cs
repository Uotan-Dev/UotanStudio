using UotanStudio.Firmware.Core;

namespace UotanStudio.Firmware.BootSecurity.LogoPartition;

/// <summary>
/// Detection, metadata reading and unpacking entry point for boot logo and splash partitions.
/// OPPO SPLASH LOGO! images are left to their dedicated handler.
/// <para>开机 Logo 与 splash 分区的检测、元数据读取与解包入口；OPPO SPLASH LOGO! 镜像仍交由其专用处理器。</para>
/// </summary>
internal sealed class LogoPartitionFormatHandler : IReadonlyFormatHandler, IFormatProbeHints
{
    private const int LogoTableOffset = 0x4000;
    private const int SplashPrefixOffset = 1024;
    private const int MioFormatProbeOffset = 4000;
    private const int GuoKeHeaderSize = 128;

    /// <summary>Display name of the logo partition format.
    /// <para>Logo 分区格式的显示名称。</para></summary>
    /// <value>The constant <c>Logo Partition Image</c>, also the <see cref="FormatDetector"/> registration key.
    /// <para>常量 <c>Logo Partition Image</c>，同时作为 <see cref="FormatDetector"/> 的注册键。</para></value>
    public string FormatName => "Logo Partition Image";

    /// <summary>Extensions claimed before content inspection.
    /// <para>内容检查之前声明认领的扩展名。</para></summary>
    /// <value>The single extension <c>img</c>; acceptance still depends on a validated magic.
    /// <para>仅有 <c>img</c> 一个扩展名；是否接受仍取决于经过校验的魔数。</para></value>
    public IReadOnlyList<string> FileExtensions => ["img"];

    /// <summary>
    /// Determines whether the file carries one of the supported logo magics at a known offset.
    /// <para>判断文件在已知偏移处是否携带受支持的 Logo 魔数。</para>
    /// </summary>
    /// <param name="filePath">Candidate file path. <para>候选文件路径。</para></param>
    /// <returns>True when a non-OPPO logo container magic is present.
    /// <para>存在非 OPPO 的 Logo 容器魔数时为 true。</para></returns>
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        foreach (var offset in new[] { 0, LogoTableOffset, MioFormatProbeOffset })
        {
            if (HeaderProbe.ProbeMagic(filePath, 0, offset, "LOGO!!!!"u8))
            {
                return true;
            }
        }

        foreach (var offset in new[] { SplashPrefixOffset, 0 })
        {
            if (HeaderProbe.ProbeMagic(filePath, 0, offset, "SPLASH!!"u8))
            {
                return true;
            }
        }

        return HeaderProbe.ProbeMagic(filePath, GuoKeHeaderSize, 0, "###"u8)
            && HasGuoKeTable(filePath);
    }

    /// <summary>
    /// Confirms the format from bytes the registry already read.
    /// <para>依据注册表已读取的字节确认格式。</para>
    /// </summary>
    /// <param name="header">Leading bytes of the candidate file. <para>候选文件的头部字节。</para></param>
    /// <param name="path">Candidate path, forwarded for name-based disambiguation.
    /// <para>候选路径，用于按名称消歧。</para></param>
    /// <returns>True when the header belongs to a logo container this handler owns.
    /// <para>文件头属于本处理器负责的 Logo 容器时为 true。</para></returns>
    public bool MatchesMagic(ReadOnlySpan<byte> header, string path) =>
        LogoPartitionFormat.IsLogoPartitionHeader(header);

    /// <summary>
    /// Reads the entry table and header facts without decoding payloads.
    /// <para>读取条目表与头部信息，不解码负载。</para>
    /// </summary>
    /// <param name="filePath">Logo partition to inspect. <para>待检查的 Logo 分区。</para></param>
    /// <param name="cancellationToken">Cancels the read. <para>取消本次读取。</para></param>
    /// <returns>Outcome carrying the entry names and header metadata.
    /// <para>结果对象，携带条目名与头部元数据。</para></returns>
    public async Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var image = await Task.Run(() => LogoPartitionFormat.Read(filePath, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            var metadata = new Dictionary<string, string>(image.Metadata)
            {
                ["SourceLength"] = image.SourceLength.ToString()
            };
            return FirmwareOpResult.Ok(
                $"{image.FormatName}: {image.Entries.Count} embedded image(s).",
                entries: [.. image.Entries.Select(entry => entry.FileName)],
                metadata: metadata,
                components: [.. image.Entries.Select(entry => FirmwareComponent.Of(entry.FileName, entry.DataSize))]);
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
    /// Extracts the embedded images as standard BMP or JPEG files into the output directory.
    /// <para>把内嵌图片以标准 BMP 或 JPEG 文件提取到输出目录。</para>
    /// </summary>
    /// <param name="filePath">Container path; never modified. <para>容器路径；不会被修改。</para></param>
    /// <param name="outputDirectory">Destination directory, created when absent.
    /// <para>目标目录，不存在时创建。</para></param>
    /// <param name="options">Entry selection and overwrite policy; null uses defaults.
    /// <para>条目选择与覆盖策略；为 null 时使用默认值。</para></param>
    /// <param name="cancellationToken">Cancels extraction. <para>取消提取。</para></param>
    /// <returns>Outcome listing the written files. <para>结果对象列出已写出的文件。</para></returns>
    /// <exception cref="OperationCanceledException">The token was canceled. <para>令牌被取消。</para></exception>
    public async Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory,
        UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var written = await Task.Run(() => LogoPartitionFormat.Extract(filePath, outputDirectory,
                    options?.CreateSelectedEntrySet(), options?.Overwrite ?? true, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            var components = written
                .Select(name => FirmwareComponent.Of(name, new FileInfo(Path.Combine(outputDirectory, name)).Length))
                .ToArray();
            return FirmwareOpResult.Ok(
                $"Extracted {written.Count} logo image(s) to {outputDirectory}",
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

    private static bool HasGuoKeTable(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            var header = new byte[GuoKeHeaderSize];
            var read = stream.Read(header, 0, header.Length);
            return read == header.Length && header.AsSpan().IndexOf("LOGO_TABLE"u8) >= 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}