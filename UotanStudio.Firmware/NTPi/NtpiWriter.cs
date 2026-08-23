using UotanStudio.Firmware.NTPi.Compression;
using UotanStudio.Firmware.NTPi.Crypto;
using UotanStudio.Firmware.NTPi.Exceptions;
using UotanStudio.Firmware.NTPi.Helpers;
using UotanStudio.Firmware.NTPi.Models;
using UotanStudio.Firmware.NTPi.Writer;

namespace UotanStudio.Firmware.NTPi;

/// <summary>
/// <para>NTPI 固件包写入器,提供将文件打包为 NTPI 固件包的功能。</para>
/// NTPI firmware package writer that packs files into an NTPI firmware package.
/// </summary>
/// <remarks>
/// <para>使用指定的加密提供者和压缩器初始化 NtpiWriter 的新实例。</para>
/// Initializes a new instance of NtpiWriter with the specified crypto provider and compressor.
/// </remarks>
public sealed class NtpiWriter(ICryptoProvider cryptoProvider, ILzma2Compressor compressor) : IDisposable
{
    private const int NtpiHeaderSize = 48;
    private const int KeyMapEntrySize = 32;
    private const int DefaultKeyMapEntries = 16;
    private const int DefaultBufferSize = 81920;

    private readonly NtpiFileProcessor _fileProcessor = new(
        cryptoProvider ?? throw new ArgumentNullException(nameof(cryptoProvider)),
        compressor ?? throw new ArgumentNullException(nameof(compressor)));
    private readonly NtpiRegionBuilder _regionBuilder = new(cryptoProvider ?? throw new ArgumentNullException(nameof(cryptoProvider)));
    private volatile bool _disposed;

    /// <summary>
    /// <para>使用默认的 AES-CBC 加密提供者和 LZMA 压缩器创建 NtpiWriter。</para>
    /// Creates an NtpiWriter with the default AES-CBC crypto provider and LZMA compressor.
    /// </summary>
    public static NtpiWriter CreateDefault()
        => new(new AesCbcCryptoProvider(), new Lzma2Compressor());

    /// <summary>
    /// <para>将指定目录中的文件打包为 NTPI 固件包文件。</para>
    /// Packs files from the specified directory into an NTPI firmware package file.
    /// </summary>
    /// <param name="inputDir">包含要打包文件的输入目录路径。</param>
    /// <param name="outputFile">输出的 NTPI 固件包文件路径。</param>
    /// <param name="major">主版本号,默认为 1。</param>
    /// <param name="minor">次版本号,默认为 3。</param>
    /// <param name="patch">修订版本号,默认为 0。</param>
    /// <param name="progress">打包进度报告器(可选)。</param>
    /// <param name="customRegionPayloads">自定义区域载荷字典(可选)。</param>
    /// <exception cref="UnsupportedVersionException">指定的版本号没有对应的加密密钥。</exception>
    public void Pack(string inputDir, string outputFile, ulong major = 1, ulong minor = 3, ulong patch = 0,
        IProgress<NtpiPackProgress>? progress = null,
        Dictionary<RegionType, byte[]>? customRegionPayloads = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(inputDir);
        ArgumentNullException.ThrowIfNull(outputFile);

        if (!Directory.Exists(inputDir))
        {
            throw new DirectoryNotFoundException($"Input directory not found: {inputDir}");
        }

        var files = Directory.GetFiles(inputDir, "*", SearchOption.AllDirectories);
        var entries = new List<FileEntry>();
        var fileData = new Dictionary<string, byte[]>();

        foreach (var filePath in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            var relativePath = GetRelativePath(inputDir, filePath).Replace('\\', '/');
            var data = File.ReadAllBytes(filePath);
            entries.Add(new FileEntry { Name = relativePath });
            fileData[relativePath] = data;
        }

        PackFromEntries(entries, fileData, outputFile, major, minor, patch, progress, customRegionPayloads);
    }

    /// <summary>
    /// <para>使用预定义的文件条目和数据将文件打包为 NTPI 固件包文件。</para>
    /// Packs files into an NTPI firmware package file using pre-defined entries and data.
    /// </summary>
    public void PackFromEntries(
        IEnumerable<FileEntry> entries,
        Dictionary<string, byte[]> fileData,
        string outputFile,
        ulong major = 1,
        ulong minor = 3,
        ulong patch = 0,
        IProgress<NtpiPackProgress>? progress = null,
        Dictionary<RegionType, byte[]>? customRegionPayloads = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(fileData);
        ArgumentNullException.ThrowIfNull(outputFile);

        var versionKeys = NtpiVersionRegistry.GetKeysForVersion(major, minor, patch)
            ?? throw new UnsupportedVersionException(major, minor, patch);

        for (var i = 1; i <= 5; i++)
        {
            if (!versionKeys.ContainsKey(i))
            {
                throw new UnsupportedVersionException(major, minor, patch);
            }
        }

        var entryList = entries.ToList();

        var keyMapEntries = Math.Max(DefaultKeyMapEntries, entryList.Count);
        var keyMap = NtpiPaddingHelper.GenerateRandomBytes(keyMapEntries * KeyMapEntrySize);
        var keyMapProvider = new KeyMapKeyProvider(keyMap);
        var zeroIv = KeyMapKeyProvider.GetZeroIv();

        var fileCount = entryList.Count;
        var keyIndices = new int[fileCount];
        var validCount = 0;
        for (var i = 0; i < fileCount; i++)
        {
            if (fileData.ContainsKey(entryList[i].Name))
            {
                keyIndices[i] = validCount;
                validCount++;
            }
            else
            {
                keyIndices[i] = -1;
            }
        }

        var processedResults = _fileProcessor.ProcessFiles(entryList, fileData, keyMapProvider, zeroIv, keyIndices, 1, progress);

        var buildResult = _regionBuilder.BuildRegions(processedResults, fileCount, keyMap, versionKeys, major, minor, patch, customRegionPayloads);

        var outputDir = Path.GetDirectoryName(outputFile);
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        try
        {
            using var outputStream = new FileStream(outputFile, FileMode.Create, FileAccess.Write, FileShare.None, DefaultBufferSize, FileOptions.SequentialScan);

            progress?.Report(new NtpiPackProgress
            {
                TotalFiles = fileCount,
                CompletedFiles = fileCount,
                CurrentFileName = string.Empty,
                Phase = NtpiPackPhase.WritingRegions
            });

            var ntpiHeaderBytes = new byte[NtpiHeaderSize];
            StructHelper.WriteNtpiHeader(buildResult.Header, ntpiHeaderBytes);
            outputStream.Write(ntpiHeaderBytes, 0, NtpiHeaderSize);

            foreach (var encryptedRegion in buildResult.EncryptedRegions)
            {
                outputStream.Write(encryptedRegion, 0, encryptedRegion.Length);
            }

            progress?.Report(new NtpiPackProgress
            {
                TotalFiles = fileCount,
                CompletedFiles = fileCount,
                CurrentFileName = string.Empty,
                Phase = NtpiPackPhase.WritingRegion6
            });

            outputStream.Write(buildResult.Region6Data, 0, buildResult.Region6Length);

            progress?.Report(new NtpiPackProgress
            {
                TotalFiles = fileCount,
                CompletedFiles = fileCount,
                CurrentFileName = string.Empty,
                Phase = NtpiPackPhase.Completed
            });
        }
        catch
        {
            try
            {
                if (File.Exists(outputFile))
                {
                    File.Delete(outputFile);
                }
            }
            catch
            {
                // Preserve the original exception.
            }
            throw;
        }
    }

    private static string GetRelativePath(string baseDir, string filePath) => Path.GetRelativePath(baseDir, filePath);

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