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
    /// <para>将 NTPI 固件包解包到指定目录。</para>
    /// Unpacks the NTPI firmware package to the specified directory.
    /// </summary>
    public void Unpack(string filePath, string outputDir, IProgress<NtpiExtractionProgress>? progress = null, Func<string, bool>? fileFilter = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(outputDir);

        var parsed = Parse(filePath);
        Directory.CreateDirectory(outputDir);

        var total = parsed.FileEntries.Count;
        var completed = 0;

        foreach (var entry in parsed.FileEntries)
        {
            if (fileFilter is not null && !fileFilter(entry.Name))
            {
                completed++;
                continue;
            }

            var data = parsed.ExtractedFiles.TryGetValue(entry.Name, out var value) ? value : null;
            if (data is null)
            {
                // Extract on demand from Region6 data.
                if (parsed.Regions.TryGetValue(RegionType.Region6, out var region6) && region6.Length > 0)
                {
                    var keyMap = parsed.Regions.TryGetValue(RegionType.KeyMap, out var km) ? km : [];
                    var keyMapProvider = new KeyMapKeyProvider(keyMap);
                    data = _fileExtractor.ExtractFileFromRegion6(region6, 0, keyMapProvider, entry);
                    _fileExtractor.VerifyHash(entry, data);
                }
            }

            if (data is not null)
            {
                var safeName = Path.GetFileName(entry.Name);
                var outPath = Path.Combine(outputDir, safeName);
                File.WriteAllBytes(outPath, data);
            }

            completed++;
            progress?.Report(new NtpiExtractionProgress { TotalFiles = total, CompletedFiles = completed, CurrentFileName = entry.Name });
        }
    }

    /// <summary>
    /// <para>将 NTPI 固件包解析到内存中,返回头部、区域数据与提取的文件。</para>
    /// Parses the NTPI firmware package into memory, returning header, region data, and extracted files.
    /// </summary>
    public NtpiParsedFile Parse(string filePath)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(filePath);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("NTPI file not found.", filePath);
        }

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, DefaultBufferSize, FileOptions.SequentialScan);
        var fileData = new byte[stream.Length];
        stream.ReadExactly(fileData);
        return ParseCore(fileData, fileData.Length);
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
            onRegion: (type, size, payload) => result.Regions.Add((type, size)),
            onRegion6Offset: (offset, size) => result.Regions.Add((RegionType.Region6, size)));

        return result;
    }

    private NtpiParsedFile ParseCore(byte[] fileData, int fileLength)
    {
        var header = NtpiRegionWalker.ValidateAndReadHeader(fileData);
        var keyProvider = new VersionKeyProvider(header.VersionMajor, header.VersionMinor, header.VersionPatch);

        var result = new NtpiParsedFile { Header = header };
        byte[]? region6Data = null;
        var region6Offset = 0;

        _regionWalker.WalkRegionChain(fileData, fileLength, header, keyProvider,
            onRegion: (type, size, payload, offset) =>
            {
                result.Regions[type] = payload;
                if (type == RegionType.Region6)
                {
                    region6Data = payload;
                    region6Offset = offset;
                }
            });

        if (result.Regions.TryGetValue(RegionType.FileIndex, out var fileIndexData))
        {
            result.FileEntries = NtpiRegionWalker.ParseFileEntries(fileIndexData);
        }

        // Extract all files from Region6.
        if (region6Data is { Length: > 0 } && result.FileEntries.Count > 0)
        {
            var keyMap = result.Regions.TryGetValue(RegionType.KeyMap, out var km) ? km : [];
            var keyMapProvider = new KeyMapKeyProvider(keyMap);
            foreach (var entry in result.FileEntries)
            {
                try
                {
                    var data = _fileExtractor.ExtractFileFromRegion6(region6Data, region6Offset, keyMapProvider, entry);
                    _fileExtractor.VerifyHash(entry, data);
                    result.ExtractedFiles[entry.Name] = data;
                }
                catch (IntegrityVerificationException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is InvalidFormatException or NtpiException)
                {
                    throw;
                }
            }
        }

        return result;
    }

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