using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.Core;
using UotanStudio.Hash.Crc32Ieee;

namespace UotanStudio.Firmware.BlockImage.Gpt;

/// <summary>
/// Enumerates the structural problems a GPT disk image can carry.
/// <para>枚举 GPT 磁盘镜像可能存在的结构性问题。</para>
/// </summary>
internal enum GptValidationIssueCode
{
    /// <summary>The primary header block has no <c>EFI PART</c> signature. <para>主头部块缺少 <c>EFI PART</c> 签名。</para></summary>
    PrimarySignatureMissing,
    /// <summary>The primary header CRC does not match its bytes. <para>主头部 CRC 与其字节不符。</para></summary>
    PrimaryHeaderCrcMismatch,
    /// <summary>The primary header geometry, revision or entry dimensions are invalid. <para>主头部几何、版本或条目维度无效。</para></summary>
    PrimaryGeometryInvalid,
    /// <summary>The primary entry array CRC does not match the stored value. <para>主条目数组 CRC 与存储值不符。</para></summary>
    PrimaryEntryArrayCrcMismatch,
    /// <summary>No backup header candidate was found near the end of the image. <para>在镜像末尾附近未找到任何备份头部候选。</para></summary>
    BackupMissing,
    /// <summary>The backup header block has no <c>EFI PART</c> signature. <para>备份头部块缺少 <c>EFI PART</c> 签名。</para></summary>
    BackupSignatureMissing,
    /// <summary>The backup header CRC does not match its bytes. <para>备份头部 CRC 与其字节不符。</para></summary>
    BackupHeaderCrcMismatch,
    /// <summary>The backup header geometry, revision or entry dimensions are invalid. <para>备份头部几何、版本或条目维度无效。</para></summary>
    BackupGeometryInvalid,
    /// <summary>The backup entry array CRC does not match the stored value. <para>备份条目数组 CRC 与存储值不符。</para></summary>
    BackupEntryArrayCrcMismatch,
    /// <summary>A backup entry leaves the usable range, overlaps another entry or carries illegal fields. <para>某备份条目越出可用区间、与其他条目重叠或携带非法字段。</para></summary>
    BackupEntriesOutOfRange,
    /// <summary>The primary and backup entry arrays differ byte-wise. <para>主备条目数组逐字节不一致。</para></summary>
    ArraysDiffer,
    /// <summary>The protective MBR does not carry a <c>0xEE</c> whole-disk record. <para>保护性 MBR 未携带覆盖整盘的 <c>0xEE</c> 记录。</para></summary>
    ProtectiveMbrInvalid
}

/// <summary>
/// One structural finding with a human-readable detail string.
/// <para>一条结构性发现，附带可读的详情字符串。</para>
/// </summary>
/// <param name="Code">
/// The issue class. <para>问题类别。</para>
/// </param>
/// <param name="Detail">
/// Offsetting or field context for the issue. <para>问题涉及的偏移或字段上下文。</para>
/// </param>
internal sealed record GptValidationIssue(GptValidationIssueCode Code, string Detail);

/// <summary>
/// The action a <see cref="GptRepair"/> call performed.
/// <para><see cref="GptRepair"/> 调用执行的动作。</para>
/// </summary>
internal enum GptRepairAction
{
    /// <summary>No output was produced. <para>未生成输出。</para></summary>
    None,
    /// <summary>The primary header, entry array and protective MBR were rebuilt from the backup. <para>主头部、条目数组与保护性 MBR 已由备份重建。</para></summary>
    RebuiltFromBackup
}

/// <summary>
/// The result of analyzing a GPT disk image, optionally with the repair action that was performed.
/// <para>GPT 磁盘镜像分析结果，可选地附带已执行的修复动作。</para>
/// </summary>
/// <param name="PrimaryValid">
/// Whether the primary header and its entry array validate independently. <para>主头部与其条目数组是否独立校验通过。</para>
/// </param>
/// <param name="BackupValid">
/// Whether the backup header and its entry array validate independently. <para>备份头部与其条目数组是否独立校验通过。</para>
/// </param>
/// <param name="Repairable">
/// Whether the primary copy can be rebuilt from the backup copy. <para>能否由备份副本重建主副本。</para>
/// </param>
/// <param name="Action">
/// The action performed by <see cref="GptRepair.Repair"/>; inspection always reports <see cref="GptRepairAction.None"/>.
/// <para><see cref="GptRepair.Repair"/> 执行的动作；检查操作始终报告 <see cref="GptRepairAction.None"/>。</para>
/// </param>
/// <param name="Issues">
/// Structural findings in discovery order. <para>按发现顺序排列的结构性问题。</para>
/// </param>
internal sealed record GptRepairReport(bool PrimaryValid, bool BackupValid, bool Repairable,
    GptRepairAction Action, IReadOnlyList<GptValidationIssue> Issues);

/// <summary>
/// Analyzes file-based GPT disk images and rebuilds a damaged primary copy from a valid backup.
/// The backup is located through the primary header's alternate LBA when that header still parses,
/// otherwise a bounded backward scan from the end of the image looks for <c>EFI PART</c> blocks whose
/// CRC and self-consistency hold. Repair writes a new file through a temporary publication file and
/// refuses to publish anything that the read-only <see cref="GptReader"/> cannot validate; it never
/// touches real devices and never rewrites the input in place.
/// <para>分析基于文件的 GPT 磁盘镜像，并在主副本受损时由有效备份重建。若主头部仍可解析则经其备用 LBA 定位备份，
/// 否则从镜像末尾进行有界的反向扫描，寻找 CRC 与自一致性均成立的 <c>EFI PART</c> 块。修复操作经临时发布文件写出新文件，
/// 且拒绝发布任何无法通过只读 <see cref="GptReader"/> 校验的结果；绝不触碰真实设备，也绝不对输入就地重写。</para>
/// </summary>
internal static class GptRepair
{
    /// <summary>Block size assumed when the caller does not negotiate one.
    /// <para>调用方未协商块大小时假定的块大小。</para></summary>
    /// <value>The conventional <c>512</c> bytes; GPT headers do not store the logical block size, so
    /// 4Kn media need the explicit overload.
    /// <para>常见的 <c>512</c> 字节；GPT 头部不存储逻辑块大小，因此 4Kn 介质需使用显式重载。</para></value>
    public const int DefaultBlockSize = 512;

    private const uint MaximumEntries = 65536;
    private const uint MaximumEntrySize = 4096;
    private const ulong MaximumArrayBytes = 16 * 1024 * 1024;
    private const uint HeaderRevision = 0x10000;
    private const int BackupScanBlocks = 2048;
    private static readonly UnicodeEncoding NameEncoding = new(false, false, true);

    /// <summary>
    /// Reports the GPT structural state of an image without writing anything.
    /// <para>报告镜像的 GPT 结构状态，不写入任何内容。</para>
    /// </summary>
    /// <param name="imagePath">
    /// Disk image file; opened read-only.
    /// <para>磁盘镜像文件；以只读方式打开。</para>
    /// </param>
    /// <param name="blockSize">
    /// Logical block size, a power of two from 512 through 65536.
    /// <para>逻辑块大小，须为 512 至 65536 之间 2 的幂。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation checked between bounded reads.
    /// <para>在各次有界读取之间检查的取消令牌。</para>
    /// </param>
    /// <returns>
    /// A report with discovery-order issues and the repairability decision.
    /// <para>按发现顺序记录问题并给出可修复性判定的报告。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="imagePath"/> is blank or <paramref name="blockSize"/> is unsupported.
    /// <para><paramref name="imagePath"/> 为空白，或 <paramref name="blockSize"/> 不受支持。</para>
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// The image file does not exist.
    /// <para>镜像文件不存在。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// The token was cancelled.
    /// <para>令牌已被取消。</para>
    /// </exception>
    public static GptRepairReport Inspect(string imagePath, int blockSize = DefaultBlockSize,
        CancellationToken cancellationToken = default)
    {
        var path = ValidateImageArgument(imagePath, blockSize);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Analyze(file, blockSize, cancellationToken).Report;
    }

    /// <summary>
    /// Rebuilds the primary GPT copy from the backup and publishes the repaired image.
    /// <para>由备份重建 GPT 主副本并发布修复后的镜像。</para>
    /// </summary>
    /// <param name="imagePath">
    /// Damaged disk image; it is only read and never modified.
    /// <para>受损磁盘镜像；仅读取，绝不修改。</para>
    /// </param>
    /// <param name="outputPath">
    /// Repaired image destination; an existing file is replaced only after full validation.
    /// <para>修复后的目标镜像；已存在的文件仅在完整校验通过后被替换。</para>
    /// </param>
    /// <param name="blockSize">
    /// Logical block size, a power of two from 512 through 65536.
    /// <para>逻辑块大小，须为 512 至 65536 之间 2 的幂。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation checked through the copy, the metadata writes and the read-back validation.
    /// <para>在复制、元数据写入与读回校验期间持续检查的取消令牌。</para>
    /// </param>
    /// <returns>
    /// The report for the analyzed image, with <see cref="GptRepairReport.Action"/> set when a repair was published.
    /// <para>对所分析镜像的报告；已发布修复时设置 <see cref="GptRepairReport.Action"/>。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// A path is blank, the paths are equal, or the block size is unsupported.
    /// <para>路径为空白、两个路径相同，或块大小不受支持。</para>
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// The image file does not exist.
    /// <para>镜像文件不存在。</para>
    /// </exception>
    /// <exception cref="IOException">
    /// The output or its temporary publication file cannot be written.
    /// <para>无法写入输出或其临时发布文件。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The rebuilt image failed read-back validation; nothing is published in that case.
    /// <para>重建后的镜像未通过读回校验；此时不会发布任何内容。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// The token was cancelled.
    /// <para>令牌已被取消。</para>
    /// </exception>
    public static GptRepairReport Repair(string imagePath, string outputPath, int blockSize = DefaultBlockSize,
        CancellationToken cancellationToken = default)
    {
        var path = ValidateImageArgument(imagePath, blockSize);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var destination = Path.GetFullPath(outputPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(path, destination, comparison))
        {
            throw new ArgumentException("The repair output path must differ from the input image.", nameof(outputPath));
        }
        Analysis analysis;
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            analysis = Analyze(file, blockSize, cancellationToken);
        }
        var backupHeader = analysis.Backup;
        var backupBlock = analysis.BackupBlock;
        var backupArray = analysis.BackupArray;
        if (!analysis.Repairable || backupHeader is null || backupBlock is null || backupArray is null)
        {
            return analysis.Report;
        }

        var arrayBlocks = (checked((ulong)backupHeader.Count * backupHeader.EntrySize) + (uint)blockSize - 1) / (uint)blockSize;
        if (2 + arrayBlocks > backupHeader.First)
        {
            return analysis.Report with
            {
                Issues = [.. analysis.Report.Issues,
                    new GptValidationIssue(GptValidationIssueCode.BackupGeometryInvalid,
                        "The rebuilt primary array at LBA 2 would overlap the usable partition area.")]
            };
        }

        var blockCount = analysis.BlockCount;
        var primaryBlock = BuildPrimaryBlock(backupHeader, blockCount, blockSize,
            Crc32Ieee.Compute(backupArray));
        FileExtraction.WriteAtomic(destination, output =>
        {
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                       1024 * 1024, FileOptions.SequentialScan))
            {
                Core.Streams.StreamCopy.CopyExactly(input, output, input.Length, cancellationToken);
            }
            output.Position = 0;
            output.Write(BuildProtectiveMbr(blockCount));
            if (blockSize > 512)
            {
                output.Write(new byte[blockSize - 512], 0, blockSize - 512);
            }
            output.Position = blockSize;
            output.Write(primaryBlock);
            output.Position = checked(2L * blockSize);
            output.Write(backupArray);
            output.Position = checked(2L * blockSize + backupArray.Length);
            WriteZeros(output, arrayBlocks * (ulong)blockSize - (ulong)backupArray.Length);
            output.Position = checked((long)(blockCount - 1) * blockSize);
            output.Write(backupBlock);
            output.Flush(flushToDisk: true);
            output.Position = 0;
            try
            {
                GptReader.Read(output, blockSize, blockCount, cancellationToken);
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException)
            {
                throw new InvalidDataException("The repaired GPT failed read-back validation and was not published.", ex);
            }
        }, cancellationToken: cancellationToken);
        return analysis.Report with { Action = GptRepairAction.RebuiltFromBackup };
    }

    private sealed record ParsedHeader(ulong MyLba, ulong AlternateLba, ulong First, ulong Last, Guid DiskId,
        ulong ArrayLba, uint Count, uint EntrySize, uint ArrayCrc, uint HeaderSize);

    private sealed record Analysis(GptRepairReport Report, ulong BlockCount, bool Repairable,
        ParsedHeader? Backup, byte[]? BackupBlock, byte[]? BackupArray);

    private static string ValidateImageArgument(string imagePath, int blockSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        if (blockSize is < 512 or > 65536 || (blockSize & (blockSize - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                "The GPT block size must be a power of two from 512 through 65536.");
        }
        var path = Path.GetFullPath(imagePath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The GPT disk image was not found.", path);
        }
        return path;
    }

    private static Analysis Analyze(Stream source, int blockSize, CancellationToken cancellationToken)
    {
        var issues = new List<GptValidationIssue>();
        var length = source.Length;
        var blockCount = (ulong)(length / blockSize);
        if (blockCount < 6 || blockCount > (ulong)long.MaxValue / (uint)blockSize)
        {
            issues.Add(new GptValidationIssue(GptValidationIssueCode.PrimaryGeometryInvalid,
                "The image capacity is below the minimum GPT geometry."));
            return new Analysis(new GptRepairReport(false, false, false, GptRepairAction.None, issues),
                blockCount, false, null, null, null);
        }

        var primary = TryParseHeader(source, blockSize, 1, blockCount, true, issues, cancellationToken);
        var backup = FindBackupHeader(source, blockSize, blockCount, primary, issues, cancellationToken);
        ParsedHeader? usableBackup = null;
        byte[]? backupBlock = null;
        byte[]? backupArray = null;
        var backupValid = false;
        if (backup is not null && backup.MyLba == blockCount - 1)
        {
            usableBackup = backup;
            backupBlock = ReadBlock(source, blockCount - 1, blockSize, cancellationToken);
            backupValid = ValidateBackupArray(source, blockSize, backup, issues, cancellationToken, out backupArray);
        }

        var primaryArrayOk = primary is not null && ValidatePrimaryArray(source, blockSize, primary,
            issues, cancellationToken);
        if (primary is not null && backupValid && primaryArrayOk && backupArray is not null)
        {
            var primaryArray = TryReadArray(source, blockSize, primary.ArrayLba,
                checked((ulong)primary.Count * primary.EntrySize), cancellationToken);
            if (primaryArray is null || !primaryArray.SequenceEqual(backupArray))
            {
                issues.Add(new GptValidationIssue(GptValidationIssueCode.ArraysDiffer,
                    "The primary and backup partition entry arrays differ."));
                primaryArrayOk = false;
            }
        }
        if (!CheckProtectiveMbr(source, length, cancellationToken))
        {
            issues.Add(new GptValidationIssue(GptValidationIssueCode.ProtectiveMbrInvalid,
                "The protective MBR lacks a 0xEE whole-disk record starting at LBA 1."));
        }

        var primaryValid = primary is not null && primaryArrayOk;
        var repairable = backupValid && !primaryValid;
        return new Analysis(new GptRepairReport(primaryValid, backupValid, repairable, GptRepairAction.None, issues),
            blockCount, repairable, usableBackup, backupValid ? backupBlock : null, backupValid ? backupArray : null);
    }

    private static bool CheckProtectiveMbr(Stream source, long length, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (length < 512)
        {
            return false;
        }
        source.Position = 0;
        Span<byte> sector = new byte[512];
        if (source.Read(sector) < 512)
        {
            return false;
        }
        return sector[510] == 0x55 && sector[511] == 0xAA && sector[450] == 0xEE
            && BinaryPrimitives.ReadUInt32LittleEndian(sector[454..]) == 1;
    }

    private static ParsedHeader? TryParseHeader(Stream source, int blockSize, ulong lba, ulong blockCount,
        bool primary, List<GptValidationIssue> issues, CancellationToken cancellationToken)
    {
        var missingCode = primary ? GptValidationIssueCode.PrimarySignatureMissing : GptValidationIssueCode.BackupMissing;
        var crcCode = primary ? GptValidationIssueCode.PrimaryHeaderCrcMismatch : GptValidationIssueCode.BackupHeaderCrcMismatch;
        var geometryCode = primary ? GptValidationIssueCode.PrimaryGeometryInvalid : GptValidationIssueCode.BackupGeometryInvalid;
        var block = ReadBlockSafe(source, lba, blockSize, cancellationToken);
        if (block is null)
        {
            issues.Add(new GptValidationIssue(missingCode,
                $"No readable block exists at LBA {lba} within the image capacity."));
            return null;
        }
        Span<byte> span = block;
        if (!span[..8].SequenceEqual("EFI PART"u8))
        {
            issues.Add(new GptValidationIssue(missingCode, $"The header at LBA {lba} lacks the EFI PART signature."));
            return null;
        }
        if (BinaryPrimitives.ReadUInt32LittleEndian(span[8..]) != HeaderRevision)
        {
            issues.Add(new GptValidationIssue(geometryCode, $"The header at LBA {lba} carries an unsupported revision."));
            return null;
        }
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);
        if (headerSize is < 92 or > 65536 || headerSize > blockSize)
        {
            issues.Add(new GptValidationIssue(geometryCode,
                $"The header at LBA {lba} declares size {headerSize}."));
            return null;
        }
        var storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(span[16..]);
        span[16..20].Clear();
        if (Crc32Ieee.Compute(span[..(int)headerSize]) != storedCrc)
        {
            issues.Add(new GptValidationIssue(crcCode, $"The header CRC at LBA {lba} does not match."));
            return null;
        }
        var my = BinaryPrimitives.ReadUInt64LittleEndian(span[24..]);
        var alternate = BinaryPrimitives.ReadUInt64LittleEndian(span[32..]);
        var first = BinaryPrimitives.ReadUInt64LittleEndian(span[40..]);
        var last = BinaryPrimitives.ReadUInt64LittleEndian(span[48..]);
        var diskId = new Guid(span[56..72]);
        var arrayLba = BinaryPrimitives.ReadUInt64LittleEndian(span[72..]);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(span[80..]);
        var entrySize = BinaryPrimitives.ReadUInt32LittleEndian(span[84..]);
        var arrayCrc = BinaryPrimitives.ReadUInt32LittleEndian(span[88..]);
        var expectedMy = primary ? 1UL : blockCount - 1;
        var expectedAlternate = primary ? blockCount - 1 : 1UL;
        if (my != expectedMy || alternate != expectedAlternate || diskId == Guid.Empty
            || first > last || last >= blockCount - 1
            || count is 0 or > MaximumEntries || entrySize is < 128 or > MaximumEntrySize
            || (entrySize & (entrySize - 1)) != 0)
        {
            issues.Add(new GptValidationIssue(geometryCode,
                $"The header at LBA {lba} declares inconsistent geometry for a {blockCount}-block image."));
            return null;
        }
        var arrayBytes = (ulong)count * entrySize;
        var arrayBlocks = (arrayBytes + (uint)blockSize - 1) / (uint)blockSize;
        var reservedBlocks = (Math.Max(arrayBytes, 16384UL) + (uint)blockSize - 1) / (uint)blockSize;
        if (arrayBytes > MaximumArrayBytes || first < 2 + reservedBlocks || reservedBlocks >= blockCount - 1
            || last >= blockCount - 1 - reservedBlocks || arrayLba >= blockCount || arrayBlocks > blockCount - arrayLba
            || (primary ? arrayLba < 2 || arrayLba + arrayBlocks > first : arrayLba <= last || arrayLba + arrayBlocks > lba))
        {
            issues.Add(new GptValidationIssue(geometryCode,
                $"The entry array declared at LBA {lba} overlaps usable storage or a header."));
            return null;
        }
        return new ParsedHeader(my, alternate, first, last, diskId, arrayLba, count, entrySize, arrayCrc, headerSize);
    }

    private static ParsedHeader? FindBackupHeader(Stream source, int blockSize, ulong blockCount,
        ParsedHeader? primary, List<GptValidationIssue> issues, CancellationToken cancellationToken)
    {
        var trailingLba = blockCount - 1;
        var trailingIssues = new List<GptValidationIssue>();
        var trailing = TryParseHeader(source, blockSize, trailingLba, blockCount, false,
            trailingIssues, cancellationToken);
        if (trailing is not null)
        {
            return trailing;
        }
        if (primary is not null && primary.AlternateLba != trailingLba && primary.AlternateLba < blockCount
            && primary.AlternateLba != 1)
        {
            issues.Add(new GptValidationIssue(GptValidationIssueCode.BackupMissing,
                $"The primary header declares its alternate at LBA {primary.AlternateLba}, but the capacity-derived trailing LBA is {trailingLba}."));
            return null;
        }
        issues.AddRange(trailingIssues);
        var scanCount = (ulong)Math.Min(BackupScanBlocks, blockCount - 2);
        for (var offset = 1UL; offset < scanCount; offset++)
        {
            if (HasSignatureAt(source, blockSize, trailingLba - offset, cancellationToken))
            {
                issues.Add(new GptValidationIssue(GptValidationIssueCode.BackupMissing,
                    $"An EFI PART signature exists at LBA {trailingLba - offset}, but no valid backup header is at the capacity-derived LBA {trailingLba}."));
                return null;
            }
        }
        return null;
    }

    private static bool HasSignatureAt(Stream source, int blockSize, ulong lba, CancellationToken cancellationToken)
    {
        var block = ReadBlockSafe(source, lba, blockSize, cancellationToken);
        return block is not null && block.AsSpan(0, 8).SequenceEqual("EFI PART"u8);
    }

    private static bool ValidatePrimaryArray(Stream source, int blockSize, ParsedHeader header,
        List<GptValidationIssue> issues, CancellationToken cancellationToken)
    {
        var array = TryReadArray(source, blockSize, header.ArrayLba,
            checked((ulong)header.Count * header.EntrySize), cancellationToken);
        if (array is null)
        {
            issues.Add(new GptValidationIssue(GptValidationIssueCode.PrimaryGeometryInvalid,
                $"The primary entry array at LBA {header.ArrayLba} is not fully readable."));
            return false;
        }
        if (Crc32Ieee.Compute(array) != header.ArrayCrc)
        {
            issues.Add(new GptValidationIssue(GptValidationIssueCode.PrimaryEntryArrayCrcMismatch,
                "The primary entry array CRC does not match the stored value."));
            return false;
        }
        return true;
    }

    private static bool ValidateBackupArray(Stream source, int blockSize, ParsedHeader header,
        List<GptValidationIssue> issues, CancellationToken cancellationToken, out byte[]? array)
    {
        array = TryReadArray(source, blockSize, header.ArrayLba, checked((ulong)header.Count * header.EntrySize),
            cancellationToken);
        if (array is null)
        {
            issues.Add(new GptValidationIssue(GptValidationIssueCode.BackupEntryArrayCrcMismatch,
                $"The backup entry array at LBA {header.ArrayLba} is not fully readable."));
            return false;
        }
        if (Crc32Ieee.Compute(array) != header.ArrayCrc)
        {
            issues.Add(new GptValidationIssue(GptValidationIssueCode.BackupEntryArrayCrcMismatch,
                "The backup entry array CRC does not match the stored value."));
            return false;
        }
        var detail = ValidateEntries(array, header.Count, header.EntrySize, header.First, header.Last);
        if (detail is not null)
        {
            issues.Add(new GptValidationIssue(GptValidationIssueCode.BackupEntriesOutOfRange, detail));
            return false;
        }
        return true;
    }

    private static string? ValidateEntries(byte[] array, uint count, uint entrySize, ulong firstUsable, ulong lastUsable)
    {
        var ids = new HashSet<Guid>();
        var used = new List<(ulong First, ulong Last)>();
        for (var index = 0; index < count; index++)
        {
            var entry = array.AsSpan(index * (int)entrySize, 128);
            if (!entry[..16].ContainsAnyExcept((byte)0))
            {
                continue;
            }
            var id = new Guid(entry[16..32]);
            var first = BinaryPrimitives.ReadUInt64LittleEndian(entry[32..]);
            var last = BinaryPrimitives.ReadUInt64LittleEndian(entry[40..]);
            var attributes = BinaryPrimitives.ReadUInt64LittleEndian(entry[48..]);
            if (entrySize > 128 && array.AsSpan(index * (int)entrySize + 128, (int)entrySize - 128)
                    .ContainsAnyExcept((byte)0))
            {
                return $"Entry {index} carries nonzero reserved extension bytes.";
            }
            if (id == Guid.Empty || !ids.Add(id) || first < firstUsable || last > lastUsable || first > last
                || (attributes & 0x0000FFFFFFFFFFF8UL) != 0)
            {
                return $"Entry {index} has an invalid identity, range or attribute bits.";
            }
            var nameBytes = entry.Slice(56, 72);
            var nameLength = 0;
            while (nameLength < nameBytes.Length && BinaryPrimitives.ReadUInt16LittleEndian(nameBytes[nameLength..]) != 0)
            {
                nameLength += 2;
            }
            try
            {
                _ = NameEncoding.GetString(nameBytes[..nameLength]);
            }
            catch (DecoderFallbackException)
            {
                return $"Entry {index} carries invalid UTF-16 in its name.";
            }
            used.Add((first, last));
        }
        var ordered = used.OrderBy(range => range.First).ToArray();
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index].First <= ordered[index - 1].Last)
            {
                return "Backup partition entries overlap.";
            }
        }
        return null;
    }

    private static byte[]? TryReadArray(Stream source, int blockSize, ulong arrayLba, ulong arrayBytes,
        CancellationToken cancellationToken)
    {
        if (arrayBytes > MaximumArrayBytes)
        {
            return null;
        }
        var offset = checked((long)(arrayLba * (uint)blockSize));
        if (offset < 0 || arrayBytes > (ulong)(source.Length - offset))
        {
            return null;
        }
        var array = new byte[(int)arrayBytes];
        source.Position = offset;
        source.ReadExactly(array);
        cancellationToken.ThrowIfCancellationRequested();
        return array;
    }

    private static byte[] ReadBlock(Stream source, ulong lba, int blockSize, CancellationToken cancellationToken)
    {
        var block = ReadBlockSafe(source, lba, blockSize, cancellationToken);
        return block ?? throw new InvalidDataException("The GPT header block is not readable.");
    }

    private static byte[]? ReadBlockSafe(Stream source, ulong lba, int blockSize, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var offset = (long)(lba * (uint)blockSize);
        if (offset < 0 || blockSize > source.Length - offset)
        {
            return null;
        }
        var block = new byte[blockSize];
        source.Position = offset;
        source.ReadExactly(block);
        return block;
    }

    private static byte[] BuildPrimaryBlock(ParsedHeader backup, ulong blockCount, int blockSize, uint arrayCrc)
    {
        var block = new byte[blockSize];
        "EFI PART"u8.CopyTo(block);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(8), HeaderRevision);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(12), backup.HeaderSize);
        BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(24), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(32), blockCount - 1);
        BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(40), backup.First);
        BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(48), backup.Last);
        backup.DiskId.TryWriteBytes(block.AsSpan(56, 16));
        BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(72), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(80), backup.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(84), backup.EntrySize);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(88), arrayCrc);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(16),
            Crc32Ieee.Compute(block.AsSpan(0, (int)backup.HeaderSize)));
        return block;
    }

    private static byte[] BuildProtectiveMbr(ulong blockCount)
    {
        var mbr = new byte[512];
        mbr[448] = 0x02;
        mbr[450] = 0xEE;
        mbr[451] = 0xFF;
        mbr[452] = 0xFF;
        mbr[453] = 0xFF;
        BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(454), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(mbr.AsSpan(458),
            blockCount - 1 > uint.MaxValue ? uint.MaxValue : (uint)(blockCount - 1));
        mbr[510] = 0x55;
        mbr[511] = 0xAA;
        return mbr;
    }

    private static void WriteZeros(Stream output, ulong count)
    {
        var buffer = new byte[64 * 1024];
        var remaining = count;
        while (remaining > 0)
        {
            var chunk = (int)Math.Min((long)remaining, buffer.Length);
            output.Write(buffer, 0, chunk);
            remaining -= (ulong)chunk;
        }
    }
}