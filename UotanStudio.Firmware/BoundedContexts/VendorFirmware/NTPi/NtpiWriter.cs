using UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Compression;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Crypto;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Exceptions;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Helpers;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Models;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi.Writer;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.NTPi;

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
    /// Creates an NtpiWriter with the default AES-CBC crypto provider and LZMA compressor.
    /// <para>使用默认的 AES-CBC 加密提供者和 LZMA 压缩器创建 NtpiWriter。</para>
    /// </summary>
    public static NtpiWriter CreateDefault()
        => new(new AesCbcCryptoProvider(), new Lzma2Compressor());

    /// <summary>
    /// Packs files from the specified directory into an NTPI firmware package file.
    /// <para>将指定目录中的文件打包为 NTPI 固件包文件。</para>
    /// </summary>
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

        var outputFullPath = Path.GetFullPath(outputFile);
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var files = Directory.GetFiles(inputDir, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetFullPath(path).Equals(outputFullPath, pathComparison));
        var entries = new List<FileEntry>();
        var sourcePaths = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var filePath in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            var relativePath = GetRelativePath(inputDir, filePath).Replace('\\', '/');
            entries.Add(new FileEntry { Name = relativePath });
            sourcePaths[relativePath] = filePath;
        }

        Stream OpenSource(FileEntry entry) => new FileStream(
            sourcePaths[entry.Name], FileMode.Open, FileAccess.Read, FileShare.Read, DefaultBufferSize, FileOptions.SequentialScan);

        PackCore(entries, OpenSource, outputFile, major, minor, patch, progress, customRegionPayloads);
    }

    /// <summary>
    /// Packs files into an NTPI firmware package file using pre-defined entries and data.
    /// <para>使用预定义的文件条目和数据将文件打包为 NTPI 固件包文件。</para>
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

        var validEntries = new List<FileEntry>();
        foreach (var entry in entries)
        {
            if (fileData.TryGetValue(entry.Name, out var data))
            {
                validEntries.Add(new FileEntry
                {
                    Name = entry.Name,
                    OriginalLength = data.Length,
                    Length = entry.Length,
                    FileSha256Hash = entry.FileSha256Hash,
                    KeyIndex = entry.KeyIndex,
                    Offset = entry.Offset
                });
            }
        }

        Stream OpenSource(FileEntry entry) => new MemoryStream(fileData[entry.Name], writable: false);

        PackCore(validEntries, OpenSource, outputFile, major, minor, patch, progress, customRegionPayloads);
    }

    /// <summary>
    /// Core two-pass packing: first pass computes stats/hash/layout, second pass encodes and writes files one by one.
    /// <para>两遍扫描的核心打包流程:第一遍统计/哈希/布局,第二遍逐文件编码写入。</para>
    /// </summary>
    private void PackCore(
        IReadOnlyList<FileEntry> entryList,
        Func<FileEntry, Stream> openSource,
        string outputFile,
        ulong major,
        ulong minor,
        ulong patch,
        IProgress<NtpiPackProgress>? progress,
        Dictionary<RegionType, byte[]>? customRegionPayloads)
    {
        var versionKeys = NtpiVersionRegistry.GetKeysForVersion(major, minor, patch)
            ?? throw new UnsupportedVersionException(major, minor, patch);

        for (var i = 1; i <= 5; i++)
        {
            if (!versionKeys.ContainsKey(i))
            {
                throw new UnsupportedVersionException(major, minor, patch);
            }
        }

        var fileCount = entryList.Count;

        var keyMapEntries = Math.Max(DefaultKeyMapEntries, fileCount);
        var keyMap = NtpiPaddingHelper.GenerateRandomBytes(keyMapEntries * KeyMapEntrySize);
        var keyMapProvider = new KeyMapKeyProvider(keyMap);
        var zeroIv = KeyMapKeyProvider.GetZeroIv();


        var plans = new FilePlan[fileCount];
        var offset = 0L;
        for (var i = 0; i < fileCount; i++)
        {
            var entry = entryList[i];
            using var source = openSource(entry);
            var plan = _fileProcessor.MeasureFile(entry, source, source.Length, i);
            plan.Offset = offset;
            if (plan.OriginalLength != 0)
            {
                offset = checked(offset + NtEncodeHeaderSize + plan.EncryptedLength);
            }
            plans[i] = plan;

            progress?.Report(new NtpiPackProgress
            {
                TotalFiles = fileCount,
                CompletedFiles = i + 1,
                CurrentFileName = entry.Name,
                Phase = NtpiPackPhase.ProcessingFiles
            });
        }

        var region6Length = offset;

        var fileEntries = new List<FileEntry>(fileCount);
        foreach (var plan in plans)
        {
            fileEntries.Add(new FileEntry
            {
                Name = plan.Entry.Name,
                OriginalLength = plan.OriginalLength,
                Length = plan.OriginalLength == 0 ? 0 : NtEncodeHeaderSize + plan.EncryptedLength,
                FileSha256Hash = plan.Sha256Hash,
                KeyIndex = plan.KeyIndex,
                Offset = plan.Offset
            });
        }

        var buildResult = _regionBuilder.BuildRegions(fileEntries, region6Length, keyMap, versionKeys, major, minor, patch, customRegionPayloads);

        var outputDir = Path.GetDirectoryName(outputFile);
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        var finalOutputPath = Path.GetFullPath(outputFile);
        var tempOutputPath = Path.Combine(
            Path.GetDirectoryName(finalOutputPath) ?? Directory.GetCurrentDirectory(),
            $".{Path.GetFileName(finalOutputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var outputStream = new FileStream(tempOutputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, DefaultBufferSize, FileOptions.SequentialScan))
            {

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
                    CompletedFiles = 0,
                    CurrentFileName = string.Empty,
                    Phase = NtpiPackPhase.WritingRegion6
                });

                for (var i = 0; i < fileCount; i++)
                {
                    var plan = plans[i];
                    using var source = openSource(plan.Entry);
                    _fileProcessor.EncodeFile(plan, source, outputStream, keyMapProvider, zeroIv);

                    progress?.Report(new NtpiPackProgress
                    {
                        TotalFiles = fileCount,
                        CompletedFiles = i + 1,
                        CurrentFileName = plan.Entry.Name,
                        Phase = NtpiPackPhase.WritingRegion6
                    });
                }

                outputStream.Flush(flushToDisk: true);
            }

            File.Move(tempOutputPath, finalOutputPath, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(tempOutputPath);
            }
            catch
            {

            }
            throw;
        }

        progress?.Report(new NtpiPackProgress
        {
            TotalFiles = fileCount,
            CompletedFiles = fileCount,
            CurrentFileName = string.Empty,
            Phase = NtpiPackPhase.Completed
        });
    }

    private const int NtEncodeHeaderSize = 112;

    private static string GetRelativePath(string baseDir, string filePath) => Path.GetRelativePath(baseDir, filePath);

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
