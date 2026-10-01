using UotanStudio.Firmware.Core.IO;
using UotanStudio.Firmware.VendorFirmware.NTPi.Models;
using UotanStudio.Firmware.VendorFirmware.NTPi.Writer;

namespace UotanStudio.Firmware.VendorFirmware.NTPi;

/// <summary>
/// Builds NTPI firmware packages by compressing, encrypting and laying out file contents as region6 blocks.
/// <para>通过压缩、加密并将文件内容布局为 region6 块来构建 NTPI 固件包。</para>
/// </summary>
/// <remarks>
/// The instance holds no per-operation state: each call opens its own streams and closes them before returning. After
/// <see cref="Dispose"/> the instance is not reusable and every operation throws <see cref="ObjectDisposedException"/>.
/// Packing runs synchronously and observes no <see cref="CancellationToken"/>.
/// <para>实例不保存单次操作的状态：每次调用自行打开并关闭流，返回前已全部释放。调用 <see cref="Dispose"/> 后实例不可复用，
/// 之后每次操作都会抛出 <see cref="ObjectDisposedException"/>。打包为同步执行，不检查 <see cref="CancellationToken"/>。</para>
/// </remarks>
/// <param name="cryptoProvider">
/// Provider used to encrypt regions and region6 blocks; the constructor rejects <see langword="null"/>.
/// <para>用于加密区域与 region6 块的加密提供者；构造函数会拒绝 <see langword="null"/>。</para>
/// </param>
/// <param name="compressor">
/// Compressor applied to file bodies before encryption; the constructor rejects <see langword="null"/>.
/// <para>加密前用于压缩文件数据的压缩器；构造函数会拒绝 <see langword="null"/>。</para>
/// </param>
/// <exception cref="ArgumentNullException">
/// <paramref name="cryptoProvider"/> or <paramref name="compressor"/> is <see langword="null"/>.
/// <para><paramref name="cryptoProvider"/> 或 <paramref name="compressor"/> 为 <see langword="null"/>。</para>
/// </exception>
internal sealed class NtpiWriter(ICryptoProvider cryptoProvider, ILzma2Compressor compressor) : IDisposable
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
    /// <returns>
    /// A new writer wired to <see cref="AesCbcCryptoProvider"/> and <see cref="Lzma2Compressor"/>; the caller disposes it.
    /// <para>已绑定 <see cref="AesCbcCryptoProvider"/> 与 <see cref="Lzma2Compressor"/> 的新写入器，由调用方负责释放。</para>
    /// </returns>
    public static NtpiWriter CreateDefault()
        => new(new AesCbcCryptoProvider(), new Lzma2Compressor());

    /// <summary>
    /// Packs files from the specified directory into an NTPI firmware package file.
    /// <para>将指定目录中的文件打包为 NTPI 固件包文件。</para>
    /// </summary>
    /// <param name="inputDir">
    /// Source directory, searched recursively. Repack metadata files and the output file itself are excluded; entry names
    /// are the paths relative to this directory with <c>/</c> separators, ordered by ordinal comparison of the source path.
    /// <para>递归搜索的源目录。重复打包元数据文件与输出文件本身会被排除；条目名是相对该目录、使用 <c>/</c> 分隔的相对路径，
    /// 并按源路径的序数比较排序。</para>
    /// </param>
    /// <param name="outputFile">
    /// Destination package path. Parent directories are created when missing; the file is built in a temporary sibling and
    /// moved into place only after it is fully written and flushed, replacing any existing package.
    /// <para>目标包路径。缺失的父目录会被创建；包先写入同目录的临时文件，完整写入并刷盘后才移动到位并替换已有包文件。</para>
    /// </param>
    /// <param name="major">
    /// Major version written into the header; together with <paramref name="minor"/> and <paramref name="patch"/> it selects
    /// the registered key set. Default <c>1</c>.
    /// <para>写入头部的主版本；与 <paramref name="minor"/>、<paramref name="patch"/> 一起决定使用哪组已注册密钥。默认 <c>1</c>。</para>
    /// </param>
    /// <param name="minor">
    /// Minor version written into the header. Default <c>3</c>.
    /// <para>写入头部的次版本。默认 <c>3</c>。</para>
    /// </param>
    /// <param name="patch">
    /// Patch version written into the header. Default <c>0</c>.
    /// <para>写入头部的补丁版本。默认 <c>0</c>。</para>
    /// </param>
    /// <param name="progress">
    /// Sink notified per file during measurement and during region6 writing, and once per layout phase; <see langword="null"/>
    /// reports nothing.
    /// <para>测量阶段与 region6 写入阶段按文件通知，并在每个布局阶段各通知一次；传 <see langword="null"/> 表示不上报。</para>
    /// </param>
    /// <param name="customRegionPayloads">
    /// Payloads that replace the built-in <see cref="RegionType.Metadata"/>, <see cref="RegionType.Patch"/> and
    /// <see cref="RegionType.RawProgram"/> region contents; other keys are ignored and <see langword="null"/> keeps the
    /// built-in payloads.
    /// <para>用于替换内置 <see cref="RegionType.Metadata"/>、<see cref="RegionType.Patch"/> 与
    /// <see cref="RegionType.RawProgram"/> 区域内容的载荷；其他键被忽略，传 <see langword="null"/> 时使用内置载荷。</para>
    /// </param>
    /// <exception cref="ObjectDisposedException">
    /// The writer has been disposed. <para>写入器已被释放。</para>
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="inputDir"/> or <paramref name="outputFile"/> is <see langword="null"/>.
    /// <para><paramref name="inputDir"/> 或 <paramref name="outputFile"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">
    /// <paramref name="inputDir"/> does not exist. <para><paramref name="inputDir"/> 不存在。</para>
    /// </exception>
    /// <exception cref="UnsupportedVersionException">
    /// No key set is registered for the requested version, or the registered set lacks one of region keys 1 through 5.
    /// <para>请求的版本没有已注册的密钥集，或该密钥集缺少 1 到 5 号区域密钥中的某一个。</para>
    /// </exception>
    /// <exception cref="IOException">
    /// A source file changes size between the measuring and the writing pass, or the destination cannot be published.
    /// <para>源文件在测量遍与写入遍之间大小发生变化，或目标文件无法发布。</para>
    /// </exception>
    /// <exception cref="OverflowException">
    /// The accumulated region6 layout exceeds the range of <see cref="long"/>.
    /// <para>累计的 region6 布局超出 <see cref="long"/> 的取值范围。</para>
    /// </exception>
    /// <example>
    /// <code>
    /// using var writer = NtpiWriter.CreateDefault();
    /// writer.Pack(@"C:\staging", @"C:\out\package.ntpi", major: 1, minor: 3, patch: 0);
    /// </code>
    /// </example>
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
            .Where(path => !UotanStudio.Firmware.Core.Repacking.RepackMetadata.IsMetadataFile(path))
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
    /// <param name="entries">
    /// Entry descriptors defining the archive names and their order. An entry is kept only when <paramref name="fileData"/>
    /// contains its <see cref="FileEntry.Name"/>; the size, hash and offsets written to the index are recomputed from the
    /// supplied bytes, and entries whose data is empty are recorded without a payload block.
    /// <para>定义归档名称及其顺序的条目描述。只有 <paramref name="fileData"/> 中存在其 <see cref="FileEntry.Name"/> 时条目才会保留；
    /// 写入索引的大小、哈希与偏移都按提供的数据重新计算，数据为空的条目记录为零长载荷。</para>
    /// </param>
    /// <param name="fileData">
    /// Complete content of each file, keyed by archive entry name; the arrays are read in place, not copied. An empty
    /// dictionary yields a package with no file entries.
    /// <para>以归档条目名为键的每个文件完整内容；数组就地读取而不复制。空字典会得到不含文件条目的包。</para>
    /// </param>
    /// <param name="outputFile">
    /// Destination package path; missing parent directories are created and the file is published from a temporary sibling
    /// after the whole package has been written.
    /// <para>目标包路径；缺失的父目录会被创建，整包写入完成后由同目录的临时文件发布到位。</para>
    /// </param>
    /// <param name="major">
    /// Major version written into the header, selecting the registered key set together with <paramref name="minor"/> and
    /// <paramref name="patch"/>. Default <c>1</c>.
    /// <para>写入头部的主版本，与 <paramref name="minor"/>、<paramref name="patch"/> 共同选择已注册密钥集。默认 <c>1</c>。</para>
    /// </param>
    /// <param name="minor">
    /// Minor version written into the header. Default <c>3</c>.
    /// <para>写入头部的次版本。默认 <c>3</c>。</para>
    /// </param>
    /// <param name="patch">
    /// Patch version written into the header. Default <c>0</c>.
    /// <para>写入头部的补丁版本。默认 <c>0</c>。</para>
    /// </param>
    /// <param name="progress">
    /// Sink notified per entry during measurement and during region6 writing, and once per layout phase; <see langword="null"/>
    /// reports nothing.
    /// <para>测量与 region6 写入阶段按条目通知，并在每个布局阶段各通知一次；传 <see langword="null"/> 表示不上报。</para>
    /// </param>
    /// <param name="customRegionPayloads">
    /// Payloads replacing the built-in <see cref="RegionType.Metadata"/>, <see cref="RegionType.Patch"/> and
    /// <see cref="RegionType.RawProgram"/> regions; other keys are ignored and <see langword="null"/> keeps the built-ins.
    /// <para>替换内置 <see cref="RegionType.Metadata"/>、<see cref="RegionType.Patch"/> 与 <see cref="RegionType.RawProgram"/>
    /// 区域的载荷；其他键被忽略，传 <see langword="null"/> 时使用内置载荷。</para>
    /// </param>
    /// <exception cref="ObjectDisposedException">
    /// The writer has been disposed. <para>写入器已被释放。</para>
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="entries"/>, <paramref name="fileData"/> or <paramref name="outputFile"/> is <see langword="null"/>.
    /// <para><paramref name="entries"/>、<paramref name="fileData"/> 或 <paramref name="outputFile"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="UnsupportedVersionException">
    /// No key set is registered for the requested version, or the registered set lacks one of region keys 1 through 5.
    /// <para>请求的版本没有已注册的密钥集，或该密钥集缺少 1 到 5 号区域密钥中的某一个。</para>
    /// </exception>
    /// <exception cref="IOException">
    /// The destination cannot be published. <para>目标文件无法发布。</para>
    /// </exception>
    /// <exception cref="OverflowException">
    /// The accumulated region6 layout exceeds the range of <see cref="long"/>.
    /// <para>累计的 region6 布局超出 <see cref="long"/> 的取值范围。</para>
    /// </exception>
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

            AtomicPublish.Publish(tempOutputPath, finalOutputPath);
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