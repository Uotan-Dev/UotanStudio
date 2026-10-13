using UotanStudio.Comm.Protocols.Common.Evidence;

namespace UotanStudio.Comm.Protocols.Edl.Programmer;

/// <summary>
/// Builds programmer catalog rows by scanning a user-selected folder of Sahara programmer blobs whose file
/// names follow the bkerler/edl loader convention, which is exactly how upstream turns a loader folder into
/// its runtime loader database (<c>edlclient/Library/loader_db.py:39-73</c>).
/// <para>通过扫描用户选择的 Sahara programmer 目录构建目录条目，目录中的文件名遵循 bkerler/edl 的
/// loader 命名约定；上游自身也是按该约定把 loader 目录转换成运行时 loader 数据库的
/// （<c>edlclient/Library/loader_db.py:39-73</c>）。</para>
/// </summary>
/// <remarks>
/// The scan is the producer that <see cref="EdlProgrammerCatalog"/> was missing: name parsing supplies only the
/// identity fields a file name can honestly assert, every row is still hashed from the bytes, and a row whose
/// name does not follow the convention is reported as a skip instead of being guessed at.
/// <para>本扫描正是 <see cref="EdlProgrammerCatalog"/> 缺失的生产者：文件名只提供其能够诚实断言的身份字段，
/// 每一行仍然按字节计算哈希；文件名不符合约定的条目会作为跳过项上报，而不做猜测。</para>
/// <para>The OEM public-key-hash prefix recovered from the name is deliberately not declared as
/// <see cref="EdlProgrammerDescriptor.OemPublicKeyHashes"/>: the convention stores 16 hexadecimal characters,
/// while <see cref="EdlProgrammerDescriptor.Validate"/> requires at least 32 and
/// <see cref="EdlRuleProgrammerSelector"/> compares the whole lower-cased Sahara reply
/// (<see cref="SaharaDeviceIdentity.OemPublicKeyHashHex"/>) ordinal-equal to a declared value. Padding or
/// truncating either side would invent matching evidence, so scanned rows declare the hardware id only.</para>
/// <para>文件名中恢复出的 OEM 公钥哈希前缀刻意不写入
/// <see cref="EdlProgrammerDescriptor.OemPublicKeyHashes"/>：约定里该段只有 16 个十六进制字符，而
/// <see cref="EdlProgrammerDescriptor.Validate"/> 要求至少 32 个，且 <see cref="EdlRuleProgrammerSelector"/>
/// 会把 Sahara 回复（<see cref="SaharaDeviceIdentity.OemPublicKeyHashHex"/>）整体小写后与声明值逐字符比较。
/// 对任一侧做补零或截断都等于伪造匹配证据，因此扫描出的条目只声明硬件标识符。</para>
/// <para>Consequently two scanned loaders that carry the same hardware id but different hash prefixes both
/// match positively, and the selector reports <see cref="EdlProgrammerMatchDecision.Ambiguous"/> rather than
/// picking one; the prefix stays visible on the parsed identity for the caller to present.</para>
/// <para>因此，硬件标识符相同而哈希前缀不同的两个扫描结果都会正匹配，选择器上报
/// <see cref="EdlProgrammerMatchDecision.Ambiguous"/> 而不再自行挑选；前缀仍保留在解析身份中供调用方展示。</para>
/// </remarks>
internal static class EdlProgrammerFolderScanner
{
    /// <summary>
    /// Gets the default ceiling on the number of extension-matching candidate files a single scan admits.
    /// <para>获取单次扫描默认接纳的“扩展名匹配候选文件”数量上限。</para>
    /// </summary>
    /// <value>A bounded candidate count; the read-only bkerler loader corpus holds 751 blobs.
    /// <para>有界的候选数量；bkerler 只读 loader 语料共 751 个二进制。</para></value>
    internal const int DefaultMaximumCandidateFiles = 4096;

    /// <summary>
    /// Gets the default per-file size ceiling; a candidate larger than this is refused without being opened.
    /// <para>获取默认的单文件大小上限；超过该值的候选在不打开文件的情况下直接被拒绝。</para>
    /// </summary>
    /// <value>A 256 MiB ceiling, generous for Sahara programmers and mirroring the bounded-buffer ceilings
    /// used elsewhere in the solution. <para>256 MiB 上限，对 Sahara programmer 足够宽松，并与解决方案其他
    /// 有界缓冲上限保持同一量级。</para></value>
    internal const long DefaultMaximumProgrammerFileBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Gets the largest underscore-separated segment count a convention-shaped name may carry.
    /// <para>获取符合约定的文件名允许的下划线分段最大数量。</para>
    /// </summary>
    /// <value>Eight segments: the hardware id, the hash prefix and at most six free-form vendor tags, the
    /// widest shape observed in the read-only bkerler loader corpus.
    /// <para>八段：硬件标识符、哈希前缀，以及至多六个厂商自由标记——即只读 bkerler loader 语料中最宽的形状。</para></value>
    internal const int MaximumNameSegments = 8;

    private const int HardwareIdentifierHexLength = 16;
    private const int PublicKeyHashPrefixHexLength = 16;
    private static readonly string[] ProgrammerExtensions = [".bin", ".mbn", ".elf"];

    /// <summary>
    /// Scans a folder with the default candidate-count and per-file-size ceilings.
    /// <para>以默认的候选数量与单文件大小上限扫描目录。</para>
    /// </summary>
    /// <param name="directoryPath">
    /// The user-selected folder holding programmer blobs. <para>用户选定的 programmer 目录。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation checked before the walk, per directory and per file.
    /// <para>在遍历开始前、每个目录与每个文件处检查的取消令牌。</para>
    /// </param>
    /// <returns>
    /// The deterministically ordered rows and every skipped file with its reason.
    /// <para>按确定性顺序排列的条目，以及带原因的跳过文件列表。</para>
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="directoryPath"/> is null or blank.
    /// <para><paramref name="directoryPath"/> 为 null 或仅空白。</para></exception>
    /// <exception cref="DirectoryNotFoundException">
    /// <paramref name="directoryPath"/> does not exist; thrown before any file is opened.
    /// <para><paramref name="directoryPath"/> 不存在；该异常在任何文件被打开之前抛出。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 已被取消。</para></exception>
    internal static EdlProgrammerFolderScanResult Scan(string directoryPath, CancellationToken cancellationToken = default) =>
        Scan(directoryPath, DefaultMaximumCandidateFiles, DefaultMaximumProgrammerFileBytes, cancellationToken);

    /// <summary>
    /// Scans a folder, turning each convention-shaped programmer blob into a catalog row and reporting every
    /// refused file instead of aborting the walk.
    /// <para>扫描目录，把每个符合约定的 programmer 二进制转换为目录条目，并逐一上报被拒绝的文件而不是中断遍历。</para>
    /// </summary>
    /// <param name="directoryPath">
    /// The user-selected folder holding programmer blobs; vendor subfolders are walked as upstream does.
    /// <para>用户选定的 programmer 目录；与上游一致地遍历厂商子目录。</para>
    /// </param>
    /// <param name="maximumCandidateFiles">
    /// The ceiling on extension-matching candidates; the first candidates up to the ceiling are hashed and the
    /// rest are refused without being opened. <para>扩展名匹配候选的数量上限；上限之内的候选会被哈希，
    /// 其余在不打开文件的情况下被拒绝。</para>
    /// </param>
    /// <param name="maximumProgrammerFileBytes">
    /// The per-file size ceiling checked from file metadata; an oversize candidate is refused without being
    /// opened. <para>依据文件元数据检查的单文件大小上限；超限候选在不打开文件的情况下被拒绝。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation checked before the walk, per directory, before each open and between hash chunks.
    /// <para>在遍历开始前、每个目录处、每次打开前以及哈希数据块之间检查的取消令牌。</para>
    /// </param>
    /// <returns>
    /// Rows ordered by full path with <see cref="StringComparer.Ordinal"/>, the skipped list ordered the same
    /// way, and the checked sum of admitted bytes.
    /// <para>返回按完整路径以 <see cref="StringComparer.Ordinal"/> 排序的条目、同样排序的跳过列表，
    /// 以及入账字节数的 <c>checked</c> 累加值。</para>
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="directoryPath"/> is null or blank.
    /// <para><paramref name="directoryPath"/> 为 null 或仅空白。</para></exception>
    /// <exception cref="DirectoryNotFoundException">
    /// <paramref name="directoryPath"/> does not exist; thrown before any file is opened.
    /// <para><paramref name="directoryPath"/> 不存在；该异常在任何文件被打开之前抛出。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Either bound is below one. <para>任一上界小于 1。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.
    /// <para><paramref name="cancellationToken"/> 已被取消。</para></exception>
    /// <exception cref="OverflowException">The admitted byte sum exceeded <see cref="long"/>.
    /// <para>入账字节总和超出 <see cref="long"/> 范围。</para></exception>
    internal static EdlProgrammerFolderScanResult Scan(string directoryPath, int maximumCandidateFiles,
        long maximumProgrammerFileBytes, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCandidateFiles, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumProgrammerFileBytes, 1);
        var root = Path.GetFullPath(directoryPath);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"The programmer folder '{root}' does not exist.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var skipped = new List<EdlProgrammerFolderSkip>();
        var candidates = CollectCandidateFiles(root, maximumCandidateFiles, cancellationToken, skipped);
        var descriptors = new List<EdlProgrammerDescriptor>(candidates.Count);
        var admittedByHash = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var totalScannedBytes = 0L;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(candidate);
            if (!TryParseProgrammerName(fileName, out var identity, out var refusalReason))
            {
                skipped.Add(new EdlProgrammerFolderSkip(fileName, candidate, refusalReason!));
                continue;
            }

            if (!TryReadMetadata(candidate, fileName, skipped, out var attributes, out var fileLength))
            {
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                skipped.Add(new EdlProgrammerFolderSkip(fileName, candidate,
                    "The candidate is a symbolic link or other reparse point; scanned programmers must be real files."));
                continue;
            }

            if (fileLength == 0)
            {
                skipped.Add(new EdlProgrammerFolderSkip(fileName, candidate,
                    "The candidate is empty, so it cannot be a Sahara programmer image."));
                continue;
            }

            if (fileLength > maximumProgrammerFileBytes)
            {
                skipped.Add(new EdlProgrammerFolderSkip(fileName, candidate,
                    $"The candidate is {fileLength} bytes and exceeds the {maximumProgrammerFileBytes} byte " +
                    "per-file programmer limit, so it was refused without being opened."));
                continue;
            }

            string sha256Hex;
            try
            {
                using var stream = File.Open(candidate, FileMode.Open, FileAccess.Read, FileShare.Read);
                sha256Hex = EdlProgrammerHash.ComputeSha256Hex(stream, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped.Add(new EdlProgrammerFolderSkip(fileName, candidate,
                    $"The candidate could not be read for hashing: {ex.Message}"));
                continue;
            }

            if (admittedByHash.TryGetValue(sha256Hex, out var admittedFrom))
            {
                skipped.Add(new EdlProgrammerFolderSkip(fileName, candidate,
                    $"Duplicate programmer identity: the bytes repeat SHA-256 {sha256Hex} already admitted from " +
                    $"'{admittedFrom}', and {nameof(EdlProgrammerCatalog)} rejects two rows claiming one hash."));
                continue;
            }

            admittedByHash[sha256Hex] = candidate;
            totalScannedBytes = checked(totalScannedBytes + fileLength);
            descriptors.Add(new EdlProgrammerDescriptor
            {
                FileName = fileName,
                Sha256Hex = sha256Hex,
                HardwareId = identity!.HardwareId,
                Provenance = new ResourceProvenance
                {
                    Origin = ResourceOrigin.UserSupplied,
                    Source = candidate,
                    Sha256Hex = sha256Hex
                }
            });
        }

        skipped.Sort(static (left, right) => string.CompareOrdinal(left.Path, right.Path));
        return new EdlProgrammerFolderScanResult(descriptors, skipped, totalScannedBytes);
    }

    /// <summary>
    /// Parses one bare file name against the loader naming convention without touching the disk, mirroring the
    /// field extraction of <c>loader_db.py:51-62</c> while refusing rather than silently repairing a name.
    /// <para>在不访问磁盘的前提下按 loader 命名约定解析单个纯文件名；字段抽取与
    /// <c>loader_db.py:51-62</c> 一致，但对不符合约定的名称直接拒绝而不做静默修复。</para>
    /// </summary>
    /// <param name="fileName">
    /// The bare candidate file name, without any directory part. <para>不含目录部分的纯候选文件名。</para>
    /// </param>
    /// <param name="identity">
    /// The parsed identity when the name followed the convention, otherwise <see langword="null"/>.
    /// <para>名称符合约定时返回解析出的身份，否则为 <see langword="null"/>。</para>
    /// </param>
    /// <param name="refusalReason">
    /// The English explanation of the refusal when parsing failed, otherwise <see langword="null"/>.
    /// <para>解析失败时返回英文拒绝原因，否则为 <see langword="null"/>。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the name yielded a hardware id and an OEM public-key-hash prefix.
    /// <para>当文件名给出硬件标识符与 OEM 公钥哈希前缀时返回 <see langword="true"/>。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="fileName"/> is <see langword="null"/>.
    /// An empty or whitespace-padded name is refused through <paramref name="refusalReason"/> instead.
    /// <para><paramref name="fileName"/> 为 <see langword="null"/>；空名称或带首尾空白的名称改由
    /// <paramref name="refusalReason"/> 拒绝返回。</para></exception>
    internal static bool TryParseProgrammerName(string fileName, out EdlProgrammerNameIdentity? identity,
        out string? refusalReason)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        identity = null;
        refusalReason = null;
        if (fileName.Length == 0 || char.IsWhiteSpace(fileName[0]) || char.IsWhiteSpace(fileName[^1])
            || fileName.Contains(Path.DirectorySeparatorChar)
            || fileName.Contains(Path.AltDirectorySeparatorChar))
        {
            refusalReason = $"'{fileName}' is not a bare file name; a candidate must carry no surrounding " +
                "whitespace and no directory separators.";
            return false;
        }

        if (!HasProgrammerExtension(fileName))
        {
            var extension = Path.GetExtension(fileName);
            refusalReason = $"'{fileName}' has the extension '{extension}' instead of one of the programmer " +
                "extensions .bin, .mbn or .elf.";
            return false;
        }

        var segments = Path.GetFileNameWithoutExtension(fileName).Split('_');
        if (segments.Length < 2)
        {
            refusalReason = $"'{fileName}' carries no '_' separator, so the second hexadecimal field with the " +
                "OEM public-key-hash prefix is missing.";
            return false;
        }

        if (segments.Length > MaximumNameSegments)
        {
            refusalReason = $"'{fileName}' splits into {segments.Length} underscore-separated segments, more than " +
                $"the {MaximumNameSegments} segments the loader naming convention allows.";
            return false;
        }

        for (var index = 0; index < segments.Length; index++)
        {
            if (segments[index].Length > 0)
            {
                continue;
            }

            refusalReason = $"'{fileName}' has an empty segment at position {index + 1}, so its fields are not " +
                "separated by single underscores as the convention requires.";
            return false;
        }

        if (IdentifyHexFieldProblem(segments[0], "hardware id", HardwareIdentifierHexLength) is { } hardwareProblem)
        {
            refusalReason = $"'{fileName}': {hardwareProblem}";
            return false;
        }

        if (IdentifyHexFieldProblem(segments[1], "OEM public-key-hash", PublicKeyHashPrefixHexLength) is { } hashProblem)
        {
            refusalReason = $"'{fileName}': {hashProblem}";
            return false;
        }

        var hardwareHex = segments[0];
        identity = new EdlProgrammerNameIdentity(
            fileName,
            Convert.ToUInt64(hardwareHex, 16),
            hardwareHex[..8],
            hardwareHex[8..],
            segments[1]);
        return true;
    }

    /// <summary>
    /// Walks the folder depth-first through an explicit stack rather than a recursive call, keeping only
    /// extension-matching candidates and recording every file that the walk refused to consider.
    /// <para>用显式栈做深度优先遍历，而不是递归调用；只保留扩展名匹配的候选文件，
    /// 并记录每一个被遍历过程拒绝考虑的文件。</para>
    /// </summary>
    /// <param name="root">
    /// The absolute folder to walk. <para>要遍历的绝对目录。</para>
    /// </param>
    /// <param name="maximumCandidateFiles">
    /// The candidate ceiling; the excess is reported instead of opened. <para>候选数量上限；超出部分只做上报。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation checked once per directory. <para>每个目录检查一次的取消令牌。</para>
    /// </param>
    /// <param name="skipped">
    /// The accumulating skip report. <para>累积上报的跳过列表。</para>
    /// </param>
    /// <returns>
    /// The candidate paths ordered by <see cref="StringComparer.Ordinal"/>.
    /// <para>按 <see cref="StringComparer.Ordinal"/> 排序的候选路径。</para>
    /// </returns>
    private static List<string> CollectCandidateFiles(string root, int maximumCandidateFiles,
        CancellationToken cancellationToken, List<EdlProgrammerFolderSkip> skipped)
    {
        var candidates = new List<string>();
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(root);
        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentDirectory = pendingDirectories.Pop();
            if (!TryListDirectory(currentDirectory, out var entries, out var childDirectories, out var listingProblem))
            {
                skipped.Add(new EdlProgrammerFolderSkip(Path.GetFileName(currentDirectory), currentDirectory,
                    $"The folder could not be listed, so none of its files were considered: {listingProblem}"));
                continue;
            }

            Array.Sort(childDirectories, StringComparer.Ordinal);
            foreach (var childDirectory in childDirectories)
            {
                FileAttributes childAttributes;
                try
                {
                    childAttributes = new DirectoryInfo(childDirectory).Attributes;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    skipped.Add(new EdlProgrammerFolderSkip(Path.GetFileName(childDirectory), childDirectory,
                        $"The subfolder could not be inspected: {ex.Message}"));
                    continue;
                }

                if ((childAttributes & FileAttributes.ReparsePoint) != 0)
                {
                    skipped.Add(new EdlProgrammerFolderSkip(Path.GetFileName(childDirectory), childDirectory,
                        "The subfolder is a symbolic link or junction; the scan never follows links out of the " +
                        "selected folder."));
                    continue;
                }

                pendingDirectories.Push(childDirectory);
            }

            Array.Sort(entries, StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                var entryName = Path.GetFileName(entry);
                if (!HasProgrammerExtension(entryName))
                {
                    skipped.Add(new EdlProgrammerFolderSkip(entryName, entry,
                        "The file does not carry a programmer extension, so it was never considered a candidate."));
                    continue;
                }

                if (candidates.Count >= maximumCandidateFiles)
                {
                    skipped.Add(new EdlProgrammerFolderSkip(entryName, entry,
                        $"The scan stopped at {maximumCandidateFiles} candidate files, so this candidate was " +
                        "refused without being opened."));
                    continue;
                }

                candidates.Add(entry);
            }
        }

        candidates.Sort(StringComparer.Ordinal);
        return candidates;
    }

    /// <summary>
    /// Lists one directory once and hands back the failure text instead of aborting the scan, so an
    /// inaccessible folder becomes a reported skip rather than a lost whole-folder scan.
    /// <para>一次性列出单个目录，并返回失败说明而不是中断遍历，使无法访问的目录成为上报的跳过项，
    /// 不至于让整次扫描丢失。</para>
    /// </summary>
    /// <param name="directory">
    /// The directory to list. <para>要列出的目录。</para>
    /// </param>
    /// <param name="entries">
    /// The file paths inside the directory, empty when listing failed.
    /// <para>目录中的文件路径；列出失败时为空。</para>
    /// </param>
    /// <param name="childDirectories">
    /// The subfolder paths inside the directory, empty when listing failed.
    /// <para>目录中的子目录路径；列出失败时为空。</para>
    /// </param>
    /// <param name="problem">
    /// The failure text when listing failed, otherwise <see langword="null"/>.
    /// <para>列出失败时的失败说明，否则为 <see langword="null"/>。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the directory could be listed.
    /// <para>目录可以列出时返回 <see langword="true"/>。</para>
    /// </returns>
    private static bool TryListDirectory(string directory, out string[] entries, out string[] childDirectories,
        out string? problem)
    {
        try
        {
            entries = Directory.GetFiles(directory);
            childDirectories = Directory.GetDirectories(directory);
            problem = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            entries = [];
            childDirectories = [];
            problem = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Reads the metadata a candidate needs before it is opened, reporting unreadable metadata as a skip.
    /// <para>在打开候选文件之前读取所需元数据，无法读取时作为跳过项上报。</para>
    /// </summary>
    /// <param name="path">
    /// The candidate path. <para>候选文件路径。</para>
    /// </param>
    /// <param name="fileName">
    /// The bare candidate name. <para>候选文件的纯文件名。</para>
    /// </param>
    /// <param name="skipped">
    /// The accumulating skip report. <para>累积上报的跳过列表。</para>
    /// </param>
    /// <param name="attributes">
    /// The file attributes when the read succeeded. <para>读取成功时的文件属性。</para>
    /// </param>
    /// <param name="length">
    /// The file length in bytes when the read succeeded. <para>读取成功时的文件字节长度。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when metadata was available. <para>元数据可读时返回 <see langword="true"/>。</para>
    /// </returns>
    private static bool TryReadMetadata(string path, string fileName, List<EdlProgrammerFolderSkip> skipped,
        out FileAttributes attributes, out long length)
    {
        var info = new FileInfo(path);
        try
        {
            attributes = info.Attributes;
            length = info.Exists ? info.Length : 0L;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            attributes = FileAttributes.Normal;
            length = 0L;
            skipped.Add(new EdlProgrammerFolderSkip(fileName, path,
                $"The candidate metadata could not be read: {ex.Message}"));
            return false;
        }
    }

    /// <summary>
    /// Reports whether a name ends with one of the programmer extensions recognised by upstream's loader scan.
    /// <para>判断文件名是否以上游 loader 扫描认可的 programmer 扩展名结尾。</para>
    /// </summary>
    /// <param name="fileName">
    /// The bare candidate file name. <para>纯候选文件名。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> for a <c>.bin</c>, <c>.mbn</c> or <c>.elf</c> suffix.
    /// <para>以 <c>.bin</c>、<c>.mbn</c> 或 <c>.elf</c> 结尾时返回 <see langword="true"/>。</para>
    /// </returns>
    private static bool HasProgrammerExtension(string fileName)
    {
        foreach (var extension in ProgrammerExtensions)
        {
            if (fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Describes why one hexadecimal field does not fit the convention, or returns <see langword="null"/> when it does.
    /// <para>说明某个十六进制字段不符合约定的原因；符合约定时返回 <see langword="null"/>。</para>
    /// </summary>
    /// <param name="value">
    /// The field to inspect. <para>待检查的字段。</para>
    /// </param>
    /// <param name="fieldName">
    /// The English field name used in the refusal. <para>拒绝原因中使用的英文字段名。</para>
    /// </param>
    /// <param name="expectedLength">
    /// The exact hexadecimal width the convention requires. <para>约定要求的精确十六进制宽度。</para>
    /// </param>
    /// <returns>
    /// The refusal sentence, or <see langword="null"/> for a well-formed field.
    /// <para>拒绝说明；字段格式正确时为 <see langword="null"/>。</para>
    /// </returns>
    private static string? IdentifyHexFieldProblem(string value, string fieldName, int expectedLength)
    {
        if (value.Length != expectedLength)
        {
            return $"the {fieldName} field '{value}' is {value.Length} characters wide instead of exactly " +
                $"{expectedLength} hexadecimal characters";
        }

        foreach (var character in value)
        {
            if (character is (>= '0' and <= '9') or (>= 'a' and <= 'f'))
            {
                continue;
            }

            if (character is >= 'A' and <= 'F')
            {
                return $"the {fieldName} field '{value}' uses upper-case hexadecimal, while the convention and " +
                    "the descriptor contract fix the lower-case form";
            }

            return $"the {fieldName} field '{value}' contains '{character}', which is not a hexadecimal digit";
        }

        return null;
    }
}

/// <summary>
/// The outcome of one programmer folder scan: the admissible catalog rows, every refused file with its reason,
/// and the summed size of the images that were actually hashed.
/// <para>单次 programmer 目录扫描的结果：可入册的目录条目、每个被拒绝文件及其原因，以及实际被哈希的镜像总大小。</para>
/// </summary>
/// <param name="Descriptors">
/// The rows ordered by full path; identical bytes collapse to the first admitted row so that
/// <see cref="EdlProgrammerCatalog.FromDescriptors"/> never rejects the
/// whole batch. <para>按完整路径排序的条目；字节相同的文件只保留首个入册条目，从而
/// <see cref="EdlProgrammerCatalog.FromDescriptors"/> 不会拒绝整批数据。</para>
/// </param>
/// <param name="Skipped">
/// The refused files and unreadable folders with their English reasons, ordered by full path.
/// <para>被拒绝的文件与不可读目录及其英文原因，按完整路径排序。</para>
/// </param>
/// <param name="TotalScannedBytes">
/// The checked sum of the hashed image sizes. <para>已哈希镜像大小的 <c>checked</c> 累加值。</para>
/// </param>
internal sealed record EdlProgrammerFolderScanResult(
    IReadOnlyList<EdlProgrammerDescriptor> Descriptors,
    IReadOnlyList<EdlProgrammerFolderSkip> Skipped,
    long TotalScannedBytes);

/// <summary>
/// One candidate the scan refused, kept so a user can see why a folder produced fewer rows than files.
/// <para>扫描拒绝的单个候选，保留它可使用户明白目录中文件数多于产出条目数的原因。</para>
/// </summary>
/// <param name="FileName">
/// The bare file or folder name. <para>纯文件或目录名。</para>
/// </param>
/// <param name="Path">
/// The absolute path that was refused. <para>被拒绝的绝对路径。</para>
/// </param>
/// <param name="Reason">
/// The English refusal explanation. <para>英文拒绝原因。</para>
/// </param>
internal sealed record EdlProgrammerFolderSkip(string FileName, string Path, string Reason);

/// <summary>
/// The identity pieces a convention-shaped loader file name asserts, before any byte was read.
/// <para>符合约定的 loader 文件名在读取任何字节之前所断言的身份片段。</para>
/// </summary>
/// <param name="FileName">
/// The parsed bare file name. <para>被解析的纯文件名。</para>
/// </param>
/// <param name="HardwareId">
/// The full 64-bit hardware id, the same value <see cref="SaharaDeviceIdentity.HardwareId"/> is compared
/// against. <para>完整的 64 位硬件标识符，与 <see cref="SaharaDeviceIdentity.HardwareId"/> 比较所用的值一致。</para>
/// </param>
/// <param name="ChipIdentifierHex">
/// The lower-case first eight hexadecimal characters, the MSM chip id slot of <c>loader_db.py:52</c>.
/// <para>小写的前八个十六进制字符，即 <c>loader_db.py:52</c> 的 MSM 芯片标识槽。</para>
/// </param>
/// <param name="DeviceIdentifierHex">
/// The trailing eight hexadecimal characters, the device id slot of <c>loader_db.py:57</c>.
/// <para>末尾的八个十六进制字符，即 <c>loader_db.py:57</c> 的设备标识槽。</para>
/// </param>
/// <param name="OemPublicKeyHashPrefixHex">
/// The lower-case sixteen-character OEM public-key-hash prefix of <c>loader_db.py:62</c>. It is never declared
/// as <see cref="EdlProgrammerDescriptor.OemPublicKeyHashes"/>, because that contract needs at least 32
/// hexadecimal characters and the selector compares the complete Sahara reply.
/// <para>小写的十六字符 OEM 公钥哈希前缀，见 <c>loader_db.py:62</c>。它从不写入
/// <see cref="EdlProgrammerDescriptor.OemPublicKeyHashes"/>：该契约要求至少 32 个十六进制字符，而选择器比较的是
/// 完整的 Sahara 回复。</para>
/// </param>
internal sealed record EdlProgrammerNameIdentity(
    string FileName,
    ulong HardwareId,
    string ChipIdentifierHex,
    string DeviceIdentifierHex,
    string OemPublicKeyHashPrefixHex);