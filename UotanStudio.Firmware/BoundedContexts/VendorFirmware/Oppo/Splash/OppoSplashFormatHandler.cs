using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Splash;

/// <summary>
/// Detection, metadata reading and unpacking entry point for OPPO/OnePlus splash logo images.
/// <para>OPPO/OnePlus splash 开机图镜像的检测、元数据读取与解包入口。</para>
/// </summary>
public sealed class OppoSplashFormatHandler : IReadonlyFormatHandler
{
    /// <summary>Display name of the splash image format.
    /// <para>splash 镜像格式的显示名称。</para></summary>
    /// <value>The constant <c>OPPO/OnePlus Splash Image</c>, also the <see cref="FormatDetector"/> registration key.
    /// <para>常量 <c>OPPO/OnePlus Splash Image</c>，同时作为 <see cref="FormatDetector"/> 的注册键。</para></value>
    public string FormatName => "OPPO/OnePlus Splash Image";

    /// <summary>Extensions this handler claims before content inspection.
    /// <para>在内容检查之前本处理器声明认领的扩展名。</para></summary>
    /// <value>The single extension <c>img</c>; the <c>SPLASH LOGO!</c> magic at 0x4000 still decides acceptance.
    /// <para>仅有 <c>img</c> 一个扩展名；是否接受仍由 0x4000 处的 <c>SPLASH LOGO!</c> 魔数决定。</para></value>
    public IReadOnlyList<string> FileExtensions => ["img"];

    /// <summary>
    /// Determines whether the file carries the splash magic at its fixed header offset.
    /// <para>判断文件是否在固定头偏移处携带 splash 魔数。</para>
    /// </summary>
    /// <param name="filePath">Candidate file path. <para>候选文件路径。</para></param>
    /// <returns><see langword="true"/> when 0x4000 holds <c>SPLASH LOGO!</c>.
    /// <para>0x4000 处为 <c>SPLASH LOGO!</c> 时为 <see langword="true"/>。</para></returns>
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(filePath);
            if (stream.Length < OppoSplashFormat.DataOffset)
            {
                return false;
            }

            stream.Position = OppoSplashFormat.SplashHeaderOffset;
            Span<byte> magic = stackalloc byte[12];
            return stream.Read(magic) == 12 && OppoSplashFormat.HasSplashMagic(magic);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads splash metadata without extracting payloads.
    /// <para>读取 splash 元数据，不提取负载。</para>
    /// </summary>
    /// <param name="filePath">Splash image to inspect. <para>待检查的 splash 镜像。</para></param>
    /// <param name="cancellationToken">Cancels the read. <para>取消本次读取。</para></param>
    /// <returns>Outcome carrying dimensions, metadata strings and entry names.
    /// <para>结果对象，携带尺寸、元数据字符串与条目名。</para></returns>
    public async Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = File.OpenRead(filePath);
            var image = OppoSplashFormat.Read(stream, cancellationToken);
            return FirmwareOpResult.Ok(
                $"OPPO splash v{image.Version}: {image.Width}x{image.Height}, {image.Entries.Count} images",
                entries: [.. image.Entries.Select(e => e.Name)],
                metadata: new Dictionary<string, string>
                {
                    ["Version"] = image.Version.ToString(),
                    ["Width"] = image.Width.ToString(),
                    ["Height"] = image.Height.ToString(),
                    ["Special"] = $"0x{image.Special:X8}",
                    ["DdphHeader"] = image.HasDdphHeader.ToString(),
                    ["DdphFlag"] = $"0x{image.DdphFlag:X8}",
                    ["Metadata"] = string.Join(" | ", image.Metadata)
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
    /// Decompresses the splash payloads into the output directory as raw image files.
    /// <para>将 splash 负载解压为原始镜像文件写入输出目录。</para>
    /// </summary>
    /// <param name="filePath">Splash image to unpack; never modified. <para>待解包的 splash 镜像；不会被修改。</para></param>
    /// <param name="outputDirectory">Destination directory, created when absent.
    /// <para>目标目录，不存在时创建。</para></param>
    /// <param name="options">Entry selection, decompression ceiling and overwrite policy; selection
    /// compares stored names case-insensitively. <see langword="null"/> uses defaults.
    /// <para>条目选择、解压上限与覆盖策略；选择按存储名忽略大小写比较。传 <see langword="null"/> 时使用默认值。</para></param>
    /// <param name="cancellationToken">Cancels extraction. <para>取消解包。</para></param>
    /// <returns>Outcome listing written files with their decompressed sizes.
    /// <para>结果对象列出已写入的文件及其解压后大小。</para></returns>
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
            var image = OppoSplashFormat.Read(stream, cancellationToken);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (entry, index) in image.Entries.Select((e, i) => (e, i)))
            {
                if (entry.CompressedSize == 0 || (selected is not null && !selected.Contains(entry.Name)))
                {
                    continue;
                }

                var name = OppoSplashFormat.SafeEntryFileName(entry, index);
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
                    OppoSplashFormat.DecompressEntry(stream, entry, output,
                        Math.Min(options.MaximumOutputBytes, int.MaxValue), cancellationToken);
                }

                written.Add(name);
                components.Add(FirmwareComponent.Of(name, entry.RealSize));
            }

            return FirmwareOpResult.Ok($"Extracted {written.Count} splash images to {outputDirectory}",
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