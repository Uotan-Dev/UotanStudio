using UotanStudio.Firmware.Core.IO;
using UotanStudio.Firmware.VendorFirmware.NTPi;
using UotanStudio.Firmware.VendorFirmware.NTPi.Models;

namespace UotanStudio.Firmware.VendorFirmware.NTPi;

/// <summary>
/// Reads NTPI firmware packages: region-chain walking, file listing, disk extraction and full in-memory parsing.
/// <para>读取 NTPI 固件包：遍历区域链、列出文件、解包到磁盘以及整包解析到内存。</para>
/// </summary>
/// <remarks>
/// The instance holds no per-operation state: each call opens its own read-only <see cref="FileStream"/> on the
/// package and closes it before returning. After <see cref="Dispose"/> the instance is not reusable and every
/// operation throws <see cref="ObjectDisposedException"/>. The I/O operations accept an optional
/// <see cref="CancellationToken"/> that is observed between regions, files and extraction blocks; cancellation
/// surfaces as <see cref="OperationCanceledException"/>.
/// <para>实例不保存单次操作的状态：每次调用都会自行以只读方式打开包的 <see cref="FileStream"/> 并在返回前关闭。
/// 调用 <see cref="Dispose"/> 后实例不可复用，之后每次操作都会抛出 <see cref="ObjectDisposedException"/>。
/// 各 I/O 操作接受可选的 <see cref="CancellationToken"/>，在区域、文件与解压块之间进行检查；取消时抛出
/// <see cref="OperationCanceledException"/>。</para>
/// </remarks>
public sealed class NtpiReader : IDisposable
{
    private readonly NtpiRegionWalker _regionWalker;
    private readonly NtpiFileExtractor _fileExtractor;
    private volatile bool _disposed;

    private const int DefaultBufferSize = 81920;

    /// <summary>
    /// Initializes a new instance of NtpiReader with the specified crypto provider and compressor.
    /// <para>使用指定的加密提供者和压缩器初始化 NtpiReader 的新实例。</para>
    /// </summary>
    /// <param name="cryptoProvider">
    /// AES-CBC or other provider used to decrypt region payloads and per-file key material.
    /// <para>用于解密区域载荷与逐文件密钥材料的 AES-CBC 或其他加密提供者。</para>
    /// </param>
    /// <param name="compressor">
    /// Codec used to decompress file bodies after decryption.
    /// <para>解密后用于解压文件数据的编解码器。</para>
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="cryptoProvider"/> or <paramref name="compressor"/> is <see langword="null"/>.
    /// <para><paramref name="cryptoProvider"/> 或 <paramref name="compressor"/> 为 <see langword="null"/>。</para>
    /// </exception>
    public NtpiReader(ICryptoProvider cryptoProvider, ILzma2Compressor compressor)
    {
        ArgumentNullException.ThrowIfNull(cryptoProvider);
        ArgumentNullException.ThrowIfNull(compressor);

        _regionWalker = new NtpiRegionWalker(cryptoProvider);
        _fileExtractor = new NtpiFileExtractor(cryptoProvider, compressor);
    }

    /// <summary>
    /// Creates an NtpiReader with the default AES-CBC crypto provider and LZMA compressor.
    /// <para>使用默认的 AES-CBC 加密提供者和 LZMA 压缩器创建 NtpiReader。</para>
    /// </summary>
    /// <returns>
    /// A new reader wired to <see cref="AesCbcCryptoProvider"/> and <see cref="Lzma2Compressor"/>; the caller disposes it.
    /// <para>已绑定 <see cref="AesCbcCryptoProvider"/> 与 <see cref="Lzma2Compressor"/> 的新读取器，由调用方负责释放。</para>
    /// </returns>
    public static NtpiReader CreateDefault()
        => new(new AesCbcCryptoProvider(), new Lzma2Compressor());

    /// <summary>
    /// Reads the file information of an NTPI firmware package, without extracting files.
    /// <para>读取 NTPI 固件包的文件信息,包括版本号、区域列表和文件条目,不提取文件。</para>
    /// </summary>
    /// <param name="filePath">
    /// Package to inspect; region payloads are walked with the streaming size caps of the region walker and Region6 contributes only its size.
    /// <para>待检查的包；区域载荷按区域遍历器的流式大小上限遍历，Region6 只记录长度。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Token observed between regions during the walk; the default (none) token cannot cancel.
    /// <para>遍历期间在区域之间检查的取消令牌；默认（无）令牌不可取消。</para>
    /// </param>
    /// <returns>
    /// The version triple, one <c>(type, size)</c> record per region — Region6 is reported by size only — and the parsed
    /// <see cref="FileEntry"/> list when a FileIndex region is present; otherwise <see cref="NtpiFileInfo.FileEntries"/> stays empty.
    /// <para>版本号三元组、每个区域一条 <c>(类型, 长度)</c> 记录（Region6 仅报告长度），存在 FileIndex 区域时还包含解析出的
    /// <see cref="FileEntry"/> 列表；否则 <see cref="NtpiFileInfo.FileEntries"/> 保持为空。</para>
    /// </returns>
    /// <exception cref="ObjectDisposedException">
    /// The reader has been disposed. <para>读取器已被释放。</para>
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="filePath"/> is <see langword="null"/>. <para><paramref name="filePath"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// <paramref name="filePath"/> does not exist. <para><paramref name="filePath"/> 不存在。</para>
    /// </exception>
    /// <exception cref="InvalidFormatException">
    /// The magic, header, region chain or FileIndex XML is invalid, or a region exceeds the streaming region limit.
    /// <para>魔数、头部、区域链或 FileIndex XML 无效，或某个区域超出流式区域大小上限。</para>
    /// </exception>
    /// <example>
    /// <code>
    /// using var reader = NtpiReader.CreateDefault();
    /// var info = reader.ReadInfo(@"C:\firmware\package.ntpi");
    /// Console.WriteLine($"{info.MajorVersion}.{info.MinorVersion}.{info.PatchVersion}: {info.FileEntries.Count} files");
    /// </code>
    /// </example>
    public NtpiFileInfo ReadInfo(string filePath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(filePath);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("NTPI file not found.", filePath);
        }

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, DefaultBufferSize, FileOptions.SequentialScan);
        return ReadInfoCore(stream, cancellationToken);
    }

    /// <summary>
    /// Unpacks the NTPI firmware package to the specified directory (streams extraction per file).
    /// <para>将 NTPI 固件包解包到指定目录(逐文件流式提取)。</para>
    /// </summary>
    /// <param name="filePath">
    /// Package to unpack. <para>待解包的固件包。</para>
    /// </param>
    /// <param name="outputDir">
    /// Destination directory, created when absent. <para>目标目录，不存在时创建。</para>
    /// </param>
    /// <param name="progress">
    /// Sink notified after each handled file with the total, the completed count and the current entry name;
    /// <see langword="null"/> reports nothing.
    /// <para>每处理完一个文件后通知的回调，携带总数、已完成数和当前条目名；传 <see langword="null"/> 表示不上报。</para>
    /// </param>
    /// <param name="fileFilter">
    /// Predicate over the archive entry name; <see langword="false"/> skips that file without writing it, and a
    /// <see langword="null"/> value unpacks every entry.
    /// <para>作用于归档内条目名的谓词；返回 <see langword="false"/> 时跳过该文件且不写入，传 <see langword="null"/> 时解出全部条目。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Token observed between files and extraction blocks; the default (none) token cannot cancel.
    /// <para>在文件之间与解压块之间检查的取消令牌；默认（无）令牌不可取消。</para>
    /// </param>
    /// <remarks>
    /// Equivalent to the six-argument overload with a default <see cref="NtpiUnpackOptions"/>, so the declared
    /// output of every file stays bounded by the default per-file and total limits.
    /// <para>等同于使用默认 <see cref="NtpiUnpackOptions"/> 的六参数重载，因此每个文件声明的输出仍受默认单文件与总长度上限约束。</para>
    /// </remarks>
    public void Unpack(string filePath, string outputDir, IProgress<NtpiExtractionProgress>? progress = null, Func<string, bool>? fileFilter = null, CancellationToken cancellationToken = default)
        => Unpack(filePath, outputDir, new NtpiUnpackOptions(), progress, fileFilter, cancellationToken);

    /// <summary>
    /// Unpacks an NTPI firmware package using trusted output limits.
    /// <para>使用可信输出限制将 NTPI 固件包解包到指定目录。</para>
    /// </summary>
    /// <param name="filePath">
    /// Package to unpack; it is opened read-only and never modified.
    /// <para>待解包的固件包；以只读方式打开，不会被修改。</para>
    /// </param>
    /// <param name="outputDir">
    /// Destination directory, created when absent. Files are written under <see cref="Path.GetFileName(string)"/> of
    /// the entry name, so entries that differ only by directory part overwrite each other.
    /// <para>目标目录，不存在时创建。文件按条目名的 <see cref="Path.GetFileName(string)"/> 写入，因此仅目录部分不同的条目会相互覆盖。</para>
    /// </param>
    /// <param name="options">
    /// Trusted output budget. Unless <see cref="NtpiUnpackOptions.DisableOutputLimits"/> is set, a negative limit is
    /// rejected and the declared lengths are checked against the per-file and running totals before any file is written.
    /// <para>可信输出预算。未设置 <see cref="NtpiUnpackOptions.DisableOutputLimits"/> 时，负数上限会被拒绝，并在写入任何文件之前
    /// 按单文件上限与累计总上限检查各条目声明的长度。</para>
    /// </param>
    /// <param name="progress">
    /// Sink notified after each handled file; <see langword="null"/> reports nothing.
    /// <para>每处理完一个文件后通知的回调；传 <see langword="null"/> 表示不上报。</para>
    /// </param>
    /// <param name="fileFilter">
    /// Predicate over the archive entry name; <see langword="null"/> unpacks every entry. Filtered-out files are still
    /// counted as completed.
    /// <para>作用于条目名的谓词；传 <see langword="null"/> 时解出全部条目。被过滤掉的条目仍计入已完成数。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Token observed between files and extraction blocks; the default (none) token cannot cancel.
    /// <para>在文件之间与解压块之间检查的取消令牌；默认（无）令牌不可取消。</para>
    /// </param>
    /// <remarks>
    /// Each file is written to a temporary path and moved into place with overwrite enabled, so a failed file leaves no
    /// partial output for that file; files completed by earlier iterations stay on disk.
    /// <para>每个文件先写入临时路径，再以允许覆盖的方式移动到目标位置，因此失败的文件本身不会残留半成品；此前已完成的文件仍保留在磁盘上。</para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">
    /// The reader has been disposed. <para>读取器已被释放。</para>
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="filePath"/>, <paramref name="outputDir"/> or <paramref name="options"/> is <see langword="null"/>.
    /// <para><paramref name="filePath"/>、<paramref name="outputDir"/> 或 <paramref name="options"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Output limits are enabled and <paramref name="options"/> carries a negative byte limit.
    /// <para>启用输出限制且 <paramref name="options"/> 含负数字节上限。</para>
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// <paramref name="filePath"/> does not exist. <para><paramref name="filePath"/> 不存在。</para>
    /// </exception>
    /// <exception cref="InvalidFormatException">
    /// A declared output exceeds the trusted per-file or total limit, the declared total overflows <see cref="long"/>, an
    /// entry has a negative original length, the Region6 or KeyMap region is missing while file entries exist, or the
    /// package structure fails validation.
    /// <para>某个声明的输出超出可信的单文件或总上限，声明的总长度使 <see cref="long"/> 溢出，条目原始长度为负，
    /// 存在文件条目却缺少 Region6 或 KeyMap 区域，或包结构校验失败。</para>
    /// </exception>
    /// <exception cref="IntegrityVerificationException">
    /// The SHA-256 recorded for a file does not match the extracted bytes.
    /// <para>文件记录的 SHA-256 与解出的字节不一致。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was canceled before or during the extraction.
    /// <para><paramref name="cancellationToken"/> 在解包开始前或进行中被取消。</para>
    /// </exception>
    /// <example>
    /// <code>
    /// using var reader = NtpiReader.CreateDefault();
    /// reader.Unpack(@"C:\firmware\package.ntpi", @"C:\out", new NtpiUnpackOptions { MaxTotalOutputBytes = 4L * 1024 * 1024 * 1024 });
    /// </code>
    /// </example>
    public void Unpack(string filePath, string outputDir, NtpiUnpackOptions options, IProgress<NtpiExtractionProgress>? progress = null, Func<string, bool>? fileFilter = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(outputDir);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.DisableOutputLimits && (options.MaxFileOutputBytes < 0 || options.MaxTotalOutputBytes < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "NTPI unpack output limits must be non-negative.");
        }
        var maxFileOutput = options.DisableOutputLimits ? long.MaxValue : options.MaxFileOutputBytes;
        var maxTotalOutput = options.DisableOutputLimits ? long.MaxValue : options.MaxTotalOutputBytes;

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("NTPI file not found.", filePath);
        }

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, DefaultBufferSize, FileOptions.RandomAccess);
        var layout = WalkLayout(stream, cancellationToken);


        long declaredTotal = 0;
        foreach (var entry in layout.FileEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.OriginalLength < 0)
            {
                throw new InvalidFormatException($"File entry '{entry.Name}' has a negative original length.");
            }
            if (entry.OriginalLength > maxFileOutput)
            {
                throw new InvalidFormatException($"File entry '{entry.Name}' declares {entry.OriginalLength} output bytes, exceeding the trusted per-file limit {maxFileOutput}.");
            }
            try
            {
                declaredTotal = checked(declaredTotal + entry.OriginalLength);
            }
            catch (OverflowException ex)
            {
                throw new InvalidFormatException("The declared NTPI total output length overflows Int64.", ex);
            }
            if (declaredTotal > maxTotalOutput)
            {
                throw new InvalidFormatException($"Declared NTPI output {declaredTotal} bytes exceeds the trusted total limit {maxTotalOutput}.");
            }
        }

        Directory.CreateDirectory(outputDir);

        var total = layout.FileEntries.Count;
        var completed = 0;
        long trustedRemaining = maxTotalOutput;

        if (total > 0 && !layout.HasRegion6)
        {
            throw new InvalidFormatException("File entries are present but the Region6 region is missing.");
        }
        if (total > 0 && layout.KeyMapProvider is null)
        {
            throw new InvalidFormatException("Region6 files are present but the KeyMap region is missing or empty.");
        }
        var keyMapProvider = layout.KeyMapProvider!;

        foreach (var entry in layout.FileEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (fileFilter is not null && !fileFilter(entry.Name))
            {
                completed++;
                continue;
            }

            var safeName = Path.GetFileName(entry.Name);
            var outPath = Path.Combine(outputDir, safeName);
            var tempPath = TempFileHelper.CreateAdjacentPath(outPath);
            try
            {
                using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, DefaultBufferSize, FileOptions.SequentialScan))
                {
                    var trustedFileLimit = Math.Min(entry.OriginalLength, trustedRemaining);
                    _fileExtractor.ExtractFileToStream(stream, layout.Region6Offset, layout.Region6Length, keyMapProvider, entry, output, trustedFileLimit, cancellationToken);
                }
                File.Move(tempPath, outPath, overwrite: true);
                trustedRemaining -= entry.OriginalLength;
            }
            catch
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {

                }
                throw;
            }

            completed++;
            progress?.Report(new NtpiExtractionProgress { TotalFiles = total, CompletedFiles = completed, CurrentFileName = entry.Name });
        }
    }

    /// <summary>
    /// Parses the NTPI firmware package into memory, returning header, region data, and extracted files.
    /// <para>将 NTPI 固件包解析到内存中,返回头部、区域数据与提取的文件。</para>
    /// </summary>
    /// <param name="filePath">
    /// Package to parse; the whole Region6 and every decrypted file are held in memory at once, and no output limit from
    /// <see cref="NtpiUnpackOptions"/> applies to this path.
    /// <para>待解析的包；整个 Region6 与每个解密后的文件会同时驻留内存，此路径不受 <see cref="NtpiUnpackOptions"/> 的输出限制约束。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Token observed between regions and extracted files; the default (none) token cannot cancel.
    /// <para>在区域与文件提取之间检查的取消令牌；默认（无）令牌不可取消。</para>
    /// </param>
    /// <returns>
    /// The header, a payload entry per region type (Region6 keyed by <see cref="RegionType.Region6"/> holds its raw
    /// bytes when present), the FileIndex entries, and <see cref="NtpiParsedFile.ExtractedFiles"/> keyed by the archive
    /// entry name — empty when the package declares no files.
    /// <para>返回头部、每个区域类型一条载荷（存在时以 <see cref="RegionType.Region6"/> 为键保存其原始字节）、FileIndex 条目，
    /// 以及以归档条目名为键的 <see cref="NtpiParsedFile.ExtractedFiles"/>；包未声明文件时该项为空。</para>
    /// </returns>
    /// <exception cref="ObjectDisposedException">
    /// The reader has been disposed. <para>读取器已被释放。</para>
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="filePath"/> is <see langword="null"/>. <para><paramref name="filePath"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// <paramref name="filePath"/> does not exist. <para><paramref name="filePath"/> 不存在。</para>
    /// </exception>
    /// <exception cref="InvalidFormatException">
    /// The package structure is invalid, or file entries exist while Region6 or the KeyMap region is missing.
    /// <para>包结构无效，或存在文件条目却缺少 Region6 或 KeyMap 区域。</para>
    /// </exception>
    /// <exception cref="IntegrityVerificationException">
    /// The SHA-256 recorded for a file does not match the decrypted bytes.
    /// <para>文件记录的 SHA-256 与解密得到的字节不一致。</para>
    /// </exception>
    /// <exception cref="EndOfStreamException">
    /// The file ends before the recorded Region6 length can be read. <para>文件在读完记录的 Region6 长度之前就结束。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was canceled before or during parsing.
    /// <para><paramref name="cancellationToken"/> 在解析开始前或进行中被取消。</para>
    /// </exception>
    public NtpiParsedFile Parse(string filePath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(filePath);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("NTPI file not found.", filePath);
        }

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, DefaultBufferSize, FileOptions.RandomAccess);
        return ParseCore(stream, cancellationToken);
    }

    private NtpiFileInfo ReadInfoCore(Stream stream, CancellationToken cancellationToken)
    {
        var header = NtpiRegionWalker.ReadHeaderFromStream(stream);
        var keyProvider = new VersionKeyProvider(header.VersionMajor, header.VersionMinor, header.VersionPatch);

        var result = new NtpiFileInfo
        {
            MajorVersion = header.VersionMajor,
            MinorVersion = header.VersionMinor,
            PatchVersion = header.VersionPatch
        };

        _regionWalker.WalkRegionChainStreaming(stream, header, keyProvider,
            onRegion: (type, size, payload) =>
            {
                result.Regions.Add((type, size));
                if (type == RegionType.FileIndex)
                {
                    result.FileEntries = NtpiRegionWalker.ParseFileEntries(payload);
                }
            },
            onRegion6Offset: (offset, size) => result.Regions.Add((RegionType.Region6, size)),
            cancellationToken);

        return result;
    }

    private NtpiParsedFile ParseCore(Stream stream, CancellationToken cancellationToken)
    {
        var layout = WalkLayout(stream, cancellationToken);

        var result = new NtpiParsedFile { Header = layout.Header };
        foreach (var (type, payload) in layout.Regions)
        {
            result.Regions[type] = payload;
        }
        result.FileEntries = layout.FileEntries;


        if (layout.HasRegion6)
        {
            stream.Position = layout.Region6Offset;
            var region6 = new byte[layout.Region6Length];
            stream.ReadExactly(region6);
            result.Regions[RegionType.Region6] = region6;
        }

        if (layout.FileEntries.Count > 0)
        {
            if (!layout.HasRegion6)
            {
                throw new InvalidFormatException("File entries are present but the Region6 region is missing.");
            }
            if (layout.KeyMapProvider is null)
            {
                throw new InvalidFormatException("Region6 files are present but the KeyMap region is missing or empty.");
            }
            var keyMapProvider = layout.KeyMapProvider;

            foreach (var entry in layout.FileEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var data = _fileExtractor.ExtractFileToMemory(stream, layout.Region6Offset, layout.Region6Length, keyMapProvider, entry, cancellationToken);
                result.ExtractedFiles[entry.Name] = data;
            }
        }

        return result;
    }

    /// <summary>
    /// The parse phase records only region/file offsets, lengths, and decryption/compression parameters without loading Region6 data.
    /// <para>解析阶段仅记录区域与文件的 offset、length 以及解密/压缩参数,不加载 Region6 数据。</para>
    /// </summary>
    private ParsedLayout WalkLayout(Stream stream, CancellationToken cancellationToken)
    {
        var header = NtpiRegionWalker.ReadHeaderFromStream(stream);
        var keyProvider = new VersionKeyProvider(header.VersionMajor, header.VersionMinor, header.VersionPatch);

        var regions = new Dictionary<RegionType, byte[]>();
        var fileEntries = new List<FileEntry>();
        var region6Offset = 0L;
        var region6Length = 0L;
        var hasRegion6 = false;

        _regionWalker.WalkRegionChainStreaming(stream, header, keyProvider,
            onRegion: (type, size, payload) =>
            {
                regions[type] = payload;
                if (type == RegionType.FileIndex)
                {
                    fileEntries = NtpiRegionWalker.ParseFileEntries(payload);
                }
            },
            onRegion6Offset: (offset, size) =>
            {
                hasRegion6 = true;
                region6Offset = offset;
                region6Length = size;
            },
            cancellationToken);

        var keyMap = regions.TryGetValue(RegionType.KeyMap, out var km) ? km : [];
        var keyMapProvider = keyMap.Length > 0 ? new KeyMapKeyProvider(keyMap) : null;

        return new ParsedLayout(header, regions, fileEntries, hasRegion6, region6Offset, region6Length, keyMapProvider);
    }

    private readonly record struct ParsedLayout(
        NtpiHeader Header,
        Dictionary<RegionType, byte[]> Regions,
        List<FileEntry> FileEntries,
        bool HasRegion6,
        long Region6Offset,
        long Region6Length,
        KeyMapKeyProvider? KeyMapProvider);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>
    /// Releases resources owned by this instance.
    /// <para>释放此实例持有的资源。</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
    }
}