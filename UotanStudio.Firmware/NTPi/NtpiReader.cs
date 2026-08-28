using UotanStudio.Firmware.NTPi.Compression;
using UotanStudio.Firmware.NTPi.Crypto;
using UotanStudio.Firmware.NTPi.Exceptions;
using UotanStudio.Firmware.NTPi.Models;
using UotanStudio.Firmware.NTPi.Reader;

namespace UotanStudio.Firmware.NTPi;

/// <summary>
/// <para>NTPI 固件包读取器,提供读取文件信息、解包和解析功能。</para>
/// NTPI firmware package reader that provides functionality to read file information, unpack, and parse.
/// </summary>
public sealed class NtpiReader : IDisposable
{
    private readonly NtpiRegionWalker _regionWalker;
    private readonly NtpiFileExtractor _fileExtractor;
    private volatile bool _disposed;

    private const int DefaultBufferSize = 81920;

    /// <summary>
    /// <para>使用指定的加密提供者和压缩器初始化 NtpiReader 的新实例。</para>
    /// Initializes a new instance of NtpiReader with the specified crypto provider and compressor.
    /// </summary>
    public NtpiReader(ICryptoProvider cryptoProvider, ILzma2Compressor compressor)
    {
        ArgumentNullException.ThrowIfNull(cryptoProvider);
        ArgumentNullException.ThrowIfNull(compressor);

        _regionWalker = new NtpiRegionWalker(cryptoProvider);
        _fileExtractor = new NtpiFileExtractor(cryptoProvider, compressor);
    }

    /// <summary>
    /// <para>使用默认的 AES-CBC 加密提供者和 LZMA 压缩器创建 NtpiReader。</para>
    /// Creates an NtpiReader with the default AES-CBC crypto provider and LZMA compressor.
    /// </summary>
    public static NtpiReader CreateDefault()
        => new(new AesCbcCryptoProvider(), new Lzma2Compressor());

    /// <summary>
    /// <para>读取 NTPI 固件包的文件信息,包括版本号、区域列表和文件条目,不提取文件。</para>
    /// Reads the file information of an NTPI firmware package, without extracting files.
    /// </summary>
    public NtpiFileInfo ReadInfo(string filePath)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(filePath);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("NTPI file not found.", filePath);
        }

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, DefaultBufferSize, FileOptions.SequentialScan);
        return ReadInfoCore(stream);
    }

    /// <summary>
    /// <para>将 NTPI 固件包解包到指定目录(逐文件流式提取)。</para>
    /// Unpacks the NTPI firmware package to the specified directory (streams extraction per file).
    /// </summary>
    public void Unpack(string filePath, string outputDir, IProgress<NtpiExtractionProgress>? progress = null, Func<string, bool>? fileFilter = null)
        => Unpack(filePath, outputDir, new NtpiUnpackOptions(), progress, fileFilter);

    /// <summary>
    /// <para>使用可信输出限制将 NTPI 固件包解包到指定目录。</para>
    /// Unpacks an NTPI firmware package using trusted output limits.
    /// </summary>
    public void Unpack(string filePath, string outputDir, NtpiUnpackOptions options, IProgress<NtpiExtractionProgress>? progress = null, Func<string, bool>? fileFilter = null)
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
        var layout = WalkLayout(stream);

        // Validate every declaration, including filtered entries, before creating or mutating output.
        long declaredTotal = 0;
        foreach (var entry in layout.FileEntries)
        {
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
            if (fileFilter is not null && !fileFilter(entry.Name))
            {
                completed++;
                continue;
            }

            var safeName = Path.GetFileName(entry.Name);
            var outPath = Path.Combine(outputDir, safeName);
            var tempPath = outPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, DefaultBufferSize, FileOptions.SequentialScan))
                {
                    var trustedFileLimit = Math.Min(entry.OriginalLength, trustedRemaining);
                    _fileExtractor.ExtractFileToStream(stream, layout.Region6Offset, layout.Region6Length, keyMapProvider, entry, output, trustedFileLimit);
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
                    // Preserve the original exception.
                }
                throw;
            }

            completed++;
            progress?.Report(new NtpiExtractionProgress { TotalFiles = total, CompletedFiles = completed, CurrentFileName = entry.Name });
        }
    }

    /// <summary>
    /// <para>将 NTPI 固件包解析到内存中,返回头部、区域数据与提取的文件。</para>
    /// Parses the NTPI firmware package into memory, returning header, region data, and extracted files.
    /// </summary>
    /// <remarks>
    /// <para>此 API 为内存便捷包装;大规模包请使用 <see cref="Unpack"/> 进行流式提取。</para>
    /// This API is an in-memory convenience wrapper; for large packages use <see cref="Unpack"/> to stream extraction.
    /// </remarks>
    public NtpiParsedFile Parse(string filePath)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(filePath);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("NTPI file not found.", filePath);
        }

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, DefaultBufferSize, FileOptions.RandomAccess);
        return ParseCore(stream);
    }

    private NtpiFileInfo ReadInfoCore(Stream stream)
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
            onRegion6Offset: (offset, size) => result.Regions.Add((RegionType.Region6, size)));

        return result;
    }

    private NtpiParsedFile ParseCore(Stream stream)
    {
        var layout = WalkLayout(stream);

        var result = new NtpiParsedFile { Header = layout.Header };
        foreach (var (type, payload) in layout.Regions)
        {
            result.Regions[type] = payload;
        }
        result.FileEntries = layout.FileEntries;

        // Materialize Region6 only for the in-memory model; streaming callers use Unpack.
        // A zero-sized Region6 is still a present, valid region for packages containing only empty files.
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
                var data = _fileExtractor.ExtractFileToMemory(stream, layout.Region6Offset, layout.Region6Length, keyMapProvider, entry);
                result.ExtractedFiles[entry.Name] = data;
            }
        }

        return result;
    }

    /// <summary>
    /// <para>解析阶段仅记录区域与文件的 offset、length 以及解密/压缩参数,不加载 Region6 数据。</para>
    /// The parse phase records only region/file offsets, lengths, and decryption/compression
    /// parameters without loading Region6 data.
    /// </summary>
    private ParsedLayout WalkLayout(Stream stream)
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
            });

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

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
    }
}