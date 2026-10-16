using System.Buffers;
using System.Buffers.Binary;
using System.Formats.Asn1;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using UotanStudio.Firmware.BlockImage.Lp;
using UotanStudio.Firmware.BootSecurity.Avb;
using UotanStudio.Firmware.Core.Streams;
using UotanStudio.Firmware.OtaDelta.CareMap;
using UotanStudio.Firmware.OtaDelta.Dat;
using UotanStudio.Firmware.OtaDelta.Diff;
using UotanStudio.Hash.Crc32Ieee;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Services.Delivery;

/// <summary>Metadata and user-owned certificate for ZIP delivery. <para>ZIP 交付的元数据与用户自有签名证书。</para></summary>
internal sealed record DeliveryPackageOptions
{
    /// <summary>Whether to output an A/B OTA package layout (requires Payload mode). <para>是否按 A/B OTA 包布局输出（需配合 Payload 模式）。</para></summary>
    public bool Ota { get; init; }
    /// <summary>Path of the outer signing certificate. <para>外层签名证书路径。</para></summary>
    public string CertificatePath { get; init; } = "";
    /// <summary>Path of the outer signing private key. <para>外层签名私钥路径。</para></summary>
    public string SigningKeyPath { get; init; } = "";
    /// <summary>OTA metadata <c>post-build</c> fingerprint. <para>OTA 元数据 <c>post-build</c> 指纹。</para></summary>
    public string BuildFingerprint { get; init; } = "";
    /// <summary>OTA metadata <c>post-build-incremental</c> value. <para>OTA 元数据 <c>post-build-incremental</c> 值。</para></summary>
    public string Incremental { get; init; } = "";
    /// <summary>OTA metadata <c>post-sdk-level</c> value. <para>OTA 元数据 <c>post-sdk-level</c> 值。</para></summary>
    public uint SdkLevel { get; init; }
    /// <summary>Path of the original ROM's Edify updater for Recovery DAT packages only; empty discovers META-INF alongside the selected DAT files. <para>原 ROM 的 Edify 更新器路径；仅用于 Recovery DAT 包，为空时沿所选 DAT 文件发现 META-INF。</para></summary>
    public string RecoveryUpdaterPath { get; init; } = "";
}

/// <summary><para>以流式构建完整 A/B OTA 与自定义 Recovery 包，并用 Android 整文件格式对 ZIP 评论签名。</para>
/// Streams full A/B OTA and custom Recovery packages; signs ZIP comments using Android's whole-file format.</summary>
internal static partial class DeliveryPackageBuilder
{
    /// <summary>
    /// Validates the ZIP delivery plan: certificate/key pairing, OTA metadata, Recovery constraints, and DAT group integrity.
    /// <para>校验 ZIP 交付计划：签名证书/私钥配对、OTA 元数据、Recovery 包约束与 DAT 分组完整性。</para>
    /// </summary>
    internal static void Validate(DeliveryPlan plan)
    {
        var options = plan.Package;
        if (!plan.PackPayload && !options.Ota && !plan.Recovery) return;
        if ((options.CertificatePath.Length == 0) != (options.SigningKeyPath.Length == 0))
            throw new InvalidDataException("外层签名需要同时指定证书与私钥。");
        if (options.Ota && !plan.PackPayload)
            throw new InvalidDataException("OTA ZIP requires Payload mode.");
        foreach (var value in new[] { options.BuildFingerprint, options.Incremental })
            if (value.Length > 1024 || value.Any(char.IsControl)) throw new InvalidDataException("OTA 元数据包含无效字符。");
        if (plan.Recovery && !plan.PackPayload && !plan.PackSuper && (!plan.OrganizeDirectory || plan.PackPayload || plan.AskWipe || plan.Reboot
            || plan.Slot == "all" || plan.Files.Any(f => f.Generated || f.IncludeInSuper || f.Fastbootd || f.Partition is "super" or "super_empty"
                || !IsRecoveryArtifact(f.FileName))))
            throw new NotSupportedException("Recovery ZIP 仅支持物理 IMG 分区或成组的 Android DAT 文件；不支持动态分区、容器、双槽同时刷写、自动清除或重启。");
        if (plan.Recovery && !plan.PackPayload && !plan.PackSuper)
            ValidateDatArtifacts(Selected(plan));
        if (plan.Recovery && !plan.PackPayload && !plan.PackSuper && Selected(plan).Length == 0) throw new InvalidDataException("Recovery 包没有默认选中的镜像。");
    }

    /// <summary>
    /// Builds and writes the ZIP delivery: an A/B OTA (with metadata and ranges), a Recovery package (DAT/IMG members), or the Recovery entry files of an organized Super delivery.
    /// <para>构建并写出 ZIP 交付：A/B OTA（含元数据与 ranges）、Recovery 包（含 DAT/IMG 成员）或组织式 Super 的 Recovery 入口文件。</para>
    /// </summary>
    internal static async Task BuildAsync(DeliveryPlan plan, string stage, string images, CancellationToken token,
        IProgress<DeliveryProgress>? progress = null, IDeliverySigningKeyProvider? signingKeys = null)
    {
        Validate(plan);
        if (plan.PackPayload && plan.OrganizeDirectory)
        {
            var path = Path.Combine(stage, DeliveryOutputService.ZipFileName(plan, "ota.zip"));
            var metadata = "ota-type=AB\n";
            if (plan.DeviceCodename.Length > 0) metadata += $"pre-device={plan.DeviceCodename}\n";
            if (plan.Package.BuildFingerprint.Length > 0) metadata += $"post-build={plan.Package.BuildFingerprint}\n";
            if (plan.Package.Incremental.Length > 0) metadata += $"post-build-incremental={plan.Package.Incremental}\n";
            if (plan.Package.SdkLevel > 0) metadata += $"post-sdk-level={plan.Package.SdkLevel}\n";
            if (plan.Payload.SecurityPatchLevel.Length > 0) metadata += $"post-security-patch-level={plan.Payload.SecurityPatchLevel}\n";
            if (plan.Payload.MaxTimestamp > 0) metadata += $"post-timestamp={plan.Payload.MaxTimestamp}\n";
            string Properties(string streaming, string all) => "ota-streaming-property-files=" + streaming.PadRight(512) + "\nota-property-files=" + all.PadRight(512) + "\n";
            var careMap = BuildCareMap(plan.Container, token);
            async Task WriteZip(string text)
            {
                token.ThrowIfCancellationRequested();
                await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, true);
                using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                await WriteStoredAsync(zip, "payload.bin", Path.Combine(images, "payload.bin"), token);
                await WriteStoredAsync(zip, "payload_properties.txt", Path.Combine(images, "payload_properties.txt"), token);
                if (careMap is not null)
                {
                    await WriteStoredAsync(zip, CareMap.ZipEntryName, new MemoryStream(careMap), token);
                }

                await WriteStoredAsync(zip, MetadataEntryName, new MemoryStream(Encoding.UTF8.GetBytes(text)), token);
                if (plan.Package.CertificatePath.Length > 0)
                {
                    using var cert = LoadCertificate(plan.Package.CertificatePath);
                    await WriteStoredAsync(zip, "META-INF/com/android/otacert", new MemoryStream(Encoding.UTF8.GetBytes(cert.ExportCertificatePem())), token);
                }
            }
            await WriteZip(metadata + Properties("", ""));
            var ranges = ReadStoredRanges(path);
            var payload = ranges["payload.bin"];
            var properties = ranges["payload_properties.txt"];
            var meta = ranges[MetadataEntryName];
            using var input = File.OpenRead(Path.Combine(images, "payload.bin"));
            var header = new byte[24]; input.ReadExactly(header);
            var metadataLength = checked(24UL + BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(12)) + BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20)));
            var streaming = $"payload.bin:{payload.Offset}:{payload.Size},payload_properties.txt:{properties.Offset}:{properties.Size},metadata:{meta.Offset}:{meta.Size}";
            var all = $"payload_metadata.bin:{payload.Offset}:{metadataLength}," + streaming;
            if (all.Length > 512) throw new InvalidDataException("OTA property ranges exceed reserved metadata space.");
            var suffix = Encoding.UTF8.GetBytes(Properties(streaming, all));
            var reserved = Encoding.UTF8.GetBytes(Properties("", ""));
            if (suffix.Length != reserved.Length) throw new InvalidDataException("OTA property ranges exceed reserved metadata space.");
            await using (var patch = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                patch.Position = meta.Offset + Encoding.UTF8.GetByteCount(metadata);
                patch.Write(suffix);
            }
            RepairStoredEntryChecksum(path, MetadataEntryName);
            var actual = ReadStoredRanges(path);
            if (ranges.Any(p => actual[p.Key] != p.Value)) throw new InvalidDataException("OTA ZIP entry offsets changed while finalizing metadata.");
            if (plan.Package.SigningKeyPath.Length > 0) await SignAsync(path, plan.Package, token, signingKeys);
        }
        if (plan.Recovery && !plan.PackPayload && !plan.PackSuper)
        {
            var path = Path.Combine(stage, DeliveryOutputService.ZipFileName(plan, "recovery.zip"));
            var dat = HasDat(plan);
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite))
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                var hashes = new StringBuilder();
                foreach (var file in Selected(plan))
                {
                    token.ThrowIfCancellationRequested();
                    var source = Path.Combine(images, file.FileName);
                    var compressedPatch = file.FileName.EndsWith(".patch.dat.br", StringComparison.OrdinalIgnoreCase);
                    string? temporary = null;
                    try
                    {
                        if (compressedPatch) source = temporary = await ExpandPatchAsync(source, stage, token);
                        await using var input = IsDatArtifact(file.FileName) ? File.OpenRead(source) : MetadataReader.OpenImage(source);
                        var member = "images/" + (compressedPatch ? file.FileName[..^3]
                            : file.FileName.EndsWith(".br", StringComparison.OrdinalIgnoreCase) ? file.FileName[..^3] + ".br" : file.FileName);
                        await AddHashedMemberAsync(zip, member, input, hashes, token, progress);
                    }
                    finally { if (temporary is not null) File.Delete(temporary); }
                }
                if (dat)
                {
                    foreach (var set in DatSets(plan).Where(s => s.Patch is null))
                    {
                        using var empty = new MemoryStream();
                        await AddHashedMemberAsync(zip, "images/" + set.PatchName, empty, hashes, token, progress);
                    }
                }
                if (Selected(plan).Any(f => f.FileName.EndsWith(".zst", StringComparison.OrdinalIgnoreCase)))
                {
                    foreach (var name in new[] { "zstd", "zstd-arm64", "zstd-x86_64", "zstd-loong64" })
                    {
                        var source = Path.Combine(stage, "bin", "android", name);
                        if (!File.Exists(source)) continue;
                        await using var input = File.OpenRead(source);
                        await AddHashedMemberAsync(zip, "bin/android/" + name, input, hashes, token, progress);
                    }
                }
                using var edify = new MemoryStream(Encoding.UTF8.GetBytes(plan.Scripts.GetValueOrDefault("updater-script", RecoveryUpdaterScript(plan))));
                await AddHashedMemberAsync(zip, DeliveryRecoveryUpdater.ScriptPath, edify, hashes, token, progress);
                await AddTextAsync(zip, "checksums.sha256", hashes.ToString(), token);
                var updater = Entry(zip, "META-INF/com/google/android/update-binary");
                updater.ExternalAttributes = 0x81ed << 16;
                await using (var entry = updater.Open())
                {
                    if (dat)
                    {
                        await using var engine = File.OpenRead(DeliveryRecoveryUpdater.Resolve(plan));
                        await engine.CopyToAsync(entry, 65536, token);
                    }
                    else
                        await entry.WriteAsync(Encoding.UTF8.GetBytes(plan.Scripts.GetValueOrDefault("update-binary", RecoveryScript(plan))), token);
                }
            }
            if (plan.Package.SigningKeyPath.Length > 0) await SignAsync(path, plan.Package, token, signingKeys);
        }
        if (plan.PackSuper && plan.OrganizeDirectory && !plan.Super.Empty)
        {
            var meta = Path.Combine(stage, "META-INF", "com", "google", "android");
            Directory.CreateDirectory(meta);
            await File.WriteAllTextAsync(Path.Combine(meta, "update-binary"), plan.Scripts.GetValueOrDefault("update-binary", RecoveryScript(plan)), new UTF8Encoding(false), token);
            await File.WriteAllTextAsync(Path.Combine(meta, "updater-script"), "#Powered by UotanStudio\n", new UTF8Encoding(false), token);
            DeliveryUnixPermissions.SetExecutable(Path.Combine(meta, "update-binary"));
        }
    }

    /// <summary>
    /// Walks the central directory and returns the data offset and uncompressed size of every stored entry. Sizes and
    /// local header offsets come from the central directory records with ZIP64-widened fields restored in the order the
    /// extra block fixes, so entries whose local header carries a data descriptor instead of sizes (any entry of 4 GiB
    /// or more) resolve as well; entries that are not stored cannot be read by raw offset and are still rejected.
    /// <para>遍历中央目录，返回每个 stored 条目的数据起点与原始长度。大小与本地头偏移取自中央目录记录，ZIP64
    /// 加宽的字段按扩展块内的固定顺序还原，因此条目本地头改用数据描述符承载大小（4 GiB 及以上的条目）时同样成立；
    /// 非 stored 条目无法按偏移直读，仍会被拒绝。</para>
    /// </summary>
    /// <param name="path">Archive to inspect. <para>要检查的归档路径。</para></param>
    /// <returns>Data offset and uncompressed size keyed by entry name. <para>按条目名索引的数据起点与原始长度。</para></returns>
    /// <exception cref="InvalidDataException">A directory record is missing or malformed, or it names a non-stored entry.
    /// <para>目录记录缺失或无效，或指向非 stored 条目。</para></exception>
    internal static Dictionary<string, (long Offset, ulong Size)> ReadStoredRanges(string path)
    {
        using var input = File.OpenRead(path);
        var directoryStart = LocateCentralDirectory(input, out var entryCount);
        var result = new Dictionary<string, (long, ulong)>(StringComparer.Ordinal);
        var header = new byte[46];
        input.Position = directoryStart;
        for (var index = 0; index < entryCount; index++)
        {
            input.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x02014b50)
                throw new InvalidDataException("OTA requires a well-formed ZIP central directory.");
            if (BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(10)) != 0)
                throw new InvalidDataException("OTA requires stored ZIP entries.");
            var name = new byte[BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28))]; input.ReadExactly(name);
            var extra = new byte[BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30))]; input.ReadExactly(extra);
            input.Position = checked(input.Position + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32)));

            ulong size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24));
            long localHeaderStart = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(42));
            if ((size == uint.MaxValue || localHeaderStart == uint.MaxValue) && !RestoreZip64Sizes(header, extra, ref size, ref localHeaderStart))
                throw new InvalidDataException("ZIP64 entry is missing its sizes.");

            result.Add(Encoding.UTF8.GetString(name), (LocalDataStart(input, localHeaderStart), size));
        }
        return result;
    }

    /// <summary>
    /// Restores the widened uncompressed size and local header offset from the ZIP64 extra field in its fixed order,
    /// reading only the items whose 32-bit counterparts in the central directory record hold their placeholders.
    /// <para>按 ZIP64 扩展字段的固定顺序还原被加宽的原始长度与本地头偏移，只读取中央目录记录中对应 32 位字段为占位值的项。</para>
    /// </summary>
    /// <param name="header">Central directory record. <para>中央目录记录。</para></param>
    /// <param name="extra">Entry extra block. <para>条目扩展块。</para></param>
    /// <param name="size">Uncompressed size, replaced when it was widened. <para>原始长度，被加宽时替换。</para></param>
    /// <param name="localHeaderStart">Local header offset, replaced when it was widened. <para>本地头偏移，被加宽时替换。</para></param>
    /// <returns><see langword="true"/> when the ZIP64 field was found. <para>找到 ZIP64 字段时为 <see langword="true"/>。</para></returns>
    /// <exception cref="InvalidDataException">The extra block is malformed or truncated.
    /// <para>扩展块无效或被截断。</para></exception>
    private static bool RestoreZip64Sizes(ReadOnlySpan<byte> header, ReadOnlySpan<byte> extra, ref ulong size, ref long localHeaderStart)
    {
        var compressedWidened = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(20)) == uint.MaxValue;
        var offsetWidened = localHeaderStart == uint.MaxValue;
        for (var offset = 0; offset + 4 <= extra.Length;)
        {
            var type = BinaryPrimitives.ReadUInt16LittleEndian(extra.Slice(offset));
            var length = BinaryPrimitives.ReadUInt16LittleEndian(extra.Slice(offset + 2));
            if (offset + 4 + length > extra.Length)
                throw new InvalidDataException("Invalid ZIP extra field.");
            if (type != 1)
            {
                offset += 4 + length;
                continue;
            }

            var end = offset + 4 + length;
            var cursor = offset + 4;
            if (size == uint.MaxValue) size = ReadZip64Value(extra, ref cursor, end);
            if (compressedWidened) cursor += 8;
            if (offsetWidened) localHeaderStart = checked((long)ReadZip64Value(extra, ref cursor, end));
            return true;
        }
        return false;
    }

    /// <summary>
    /// Reads one eight-byte ZIP64 value at the extra-block cursor and advances the cursor.
    /// <para>从扩展块游标处读取一个 8 字节 ZIP64 值并前移游标。</para>
    /// </summary>
    private static ulong ReadZip64Value(ReadOnlySpan<byte> extra, ref int cursor, int end)
    {
        if (cursor + 8 > end) throw new InvalidDataException("ZIP64 entry is missing its sizes.");
        var value = BinaryPrimitives.ReadUInt64LittleEndian(extra.Slice(cursor));
        cursor += 8;
        return value;
    }

    /// <summary>
    /// Reads an entry's local header and returns the start of its data, which the local name and extra lengths fix
    /// regardless of whether the local header itself carries the sizes. The archive position is restored afterwards so
    /// the caller can keep walking the central directory sequentially.
    /// <para>读取条目本地头并返回其数据区起点，本地头的名称与扩展字段长度决定该偏移，与本地头是否承载大小无关。
    /// 读取后归档流位置还原到调用前的位置，调用方可以继续顺序遍历中央目录。</para>
    /// </summary>
    /// <param name="archive">Archive stream, left at its previous position. <para>归档流，调用后仍停在原位置。</para></param>
    /// <param name="localHeaderStart">Absolute offset of the local header. <para>本地头的绝对偏移。</para></param>
    /// <returns>Absolute offset of the entry data. <para>条目数据的绝对偏移。</para></returns>
    /// <exception cref="InvalidDataException">The local header signature is missing. <para>本地头签名缺失。</para></exception>
    private static long LocalDataStart(Stream archive, long localHeaderStart)
    {
        var resume = archive.Position;
        var header = new byte[30];
        archive.Position = localHeaderStart;
        archive.ReadExactly(header);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0x04034b50)
            throw new InvalidDataException("OTA ZIP entry local header is missing.");
        archive.Position = resume;
        return checked(localHeaderStart + header.Length
            + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26))
            + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28)));
    }

    private const string MetadataEntryName = "META-INF/com/android/metadata";

    /// <summary>
    /// Repairs the CRC-32 of a stored entry whose bytes were overwritten in place: the entry is read back, the
    /// checksum recomputed, and the value written to both the local file header (offset +14) and its central
    /// directory record (offset +16). For ZIP64 archives the central directory start comes from the ZIP64
    /// end-of-central-directory record and the entry's true sizes and local header offset come from its ZIP64 extra
    /// field; CRC-32 fields are never widened by ZIP64, so only those eight bytes change.
    /// <para>就地改写 stored 条目内容后修复其 CRC-32：读回条目字节重算校验值，同时写回本地文件头（偏移 +14）与
    /// 中央目录记录（偏移 +16）。ZIP64 归档下中央目录起点取自 ZIP64 结束记录，条目的真实大小与本地头偏移取自其
    /// ZIP64 扩展字段；CRC-32 字段本身不被 ZIP64 加宽，因此只有这 8 个字节改变。</para>
    /// </summary>
    /// <param name="path">Archive patched in place. <para>被就地修复的归档路径。</para></param>
    /// <param name="entryName">Stored entry whose content was rewritten. <para>内容被就地改写的存储条目名。</para></param>
    /// <exception cref="InvalidDataException">
    /// The directory records are missing or malformed, the entry is absent, or the entry is not a seekable stored
    /// entry within the repairable size limit.
    /// <para>目录记录缺失或无效、条目不存在，或条目不是上限内可寻址的存储条目时抛出。</para>
    /// </exception>
    internal static void RepairStoredEntryChecksum(string path, string entryName)
    {
        using var archive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 65536);
        var directoryStart = LocateCentralDirectory(archive, out var entryCount);
        var entry = LocateStoredEntry(archive, directoryStart, entryCount, entryName);

        var data = new byte[checked((int)entry.UncompressedSize)];
        archive.Position = entry.DataStart;
        archive.ReadExactly(data);
        var checksum = Crc32Ieee.Compute(data);

        WriteUInt32At(archive, entry.CentralDirectoryStart + 16, checksum);
        WriteUInt32At(archive, entry.LocalHeaderStart + 14, checksum);
    }

    /// <summary>
    /// Locates the central directory start and entry count from a bounded tail window, falling back to the ZIP64
    /// locator and ZIP64 end-of-central-directory record when the classic fields hold their placeholders.
    /// <para>从尾部有界窗口定位中央目录起点与条目数，经典字段为占位值时经 ZIP64 定位器与 ZIP64 结束记录取回真实值。</para>
    /// </summary>
    private static long LocateCentralDirectory(Stream archive, out int entryCount)
    {
        const int EndOfCentralDirectoryLength = 22;
        const int Zip64LocatorLength = 20;

        var tailLength = checked((int)Math.Min(archive.Length, EndOfCentralDirectoryLength + (long)ushort.MaxValue));
        var tail = new byte[tailLength];
        archive.Position = archive.Length - tailLength;
        archive.ReadExactly(tail);

        var recordStart = -1;
        for (var offset = tailLength - EndOfCentralDirectoryLength; offset >= 0; offset--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(offset)) == 0x06054b50)
            {
                recordStart = offset;
                break;
            }
        }

        if (recordStart < 0)
        {
            throw new InvalidDataException("ZIP end-of-central-directory record is missing.");
        }

        var recordedCount = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(recordStart + 10));
        var recordedStart = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(recordStart + 16));
        if (recordedCount != ushort.MaxValue && recordedStart != uint.MaxValue)
        {
            entryCount = recordedCount;
            return recordedStart;
        }

        var locatorStart = recordStart - Zip64LocatorLength;
        if (locatorStart < 0 || BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(locatorStart)) != 0x07064b50)
        {
            throw new InvalidDataException("ZIP64 end-of-central-directory locator is missing.");
        }

        var zip64Start = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(tail.AsSpan(locatorStart + 8)));
        var zip64 = new byte[56];
        archive.Position = zip64Start;
        archive.ReadExactly(zip64);
        if (BinaryPrimitives.ReadUInt32LittleEndian(zip64) != 0x06064b50)
        {
            throw new InvalidDataException("ZIP64 end-of-central-directory record is missing.");
        }

        var totalEntries = BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(32));
        if (totalEntries > int.MaxValue)
        {
            throw new InvalidDataException("ZIP central directory declares too many entries.");
        }

        entryCount = (int)totalEntries;
        return checked((long)BinaryPrimitives.ReadUInt64LittleEndian(zip64.AsSpan(48)));
    }

    /// <summary>
    /// Walks the central directory to resolve the named stored entry's local header start, record start, data start
    /// and uncompressed length, restoring ZIP64-widened fields from the entry extra block and accepting only
    /// seekable stored entries.
    /// <para>遍历中央目录，按名称解析目标存储条目的本地头起点、记录起点、数据起点与原始长度；ZIP64 加宽的字段按
    /// 扩展字段内的固定顺序还原，且只接受可寻址的 stored 条目。</para>
    /// </summary>
    private static (long LocalHeaderStart, long CentralDirectoryStart, long DataStart, long UncompressedSize) LocateStoredEntry(
        Stream archive, long directoryStart, int entryCount, string entryName)
    {
        const int RecordLength = 46;
        const int LocalHeaderLength = 30;
        const long MaximumRepairableEntryBytes = 1L << 20;

        var position = directoryStart;
        for (var index = 0; index < entryCount; index++)
        {
            var record = ReadAt(archive, position, RecordLength);
            if (BinaryPrimitives.ReadUInt32LittleEndian(record) != 0x02014b50)
            {
                throw new InvalidDataException("ZIP central directory record signature is invalid.");
            }

            var flags = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(8));
            var method = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(10));
            var compressedSize = (long)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(20));
            var uncompressedSize = (long)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(24));
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(28));
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(30));
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(32));
            var localHeaderStart = (long)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(42));

            if (uncompressedSize == uint.MaxValue || compressedSize == uint.MaxValue || localHeaderStart == uint.MaxValue)
            {
                var extra = ReadAt(archive, position + RecordLength + nameLength, extraLength);
                (uncompressedSize, compressedSize, localHeaderStart) = ReadZip64Extra(extra, uncompressedSize, compressedSize, localHeaderStart);
            }

            var name = Encoding.UTF8.GetString(ReadAt(archive, position + RecordLength, nameLength));
            if (string.Equals(name, entryName, StringComparison.Ordinal))
            {
                if (method != 0 || (flags & 8) != 0 || compressedSize != uncompressedSize
                    || uncompressedSize > MaximumRepairableEntryBytes)
                {
                    throw new InvalidDataException("Checksum repair requires a seekable stored ZIP entry within the repairable size limit.");
                }

                var local = ReadAt(archive, localHeaderStart, LocalHeaderLength);
                if (BinaryPrimitives.ReadUInt32LittleEndian(local) != 0x04034b50)
                {
                    throw new InvalidDataException("ZIP local file header signature is invalid.");
                }

                var dataStart = checked(localHeaderStart + LocalHeaderLength
                    + BinaryPrimitives.ReadUInt16LittleEndian(local.AsSpan(26))
                    + BinaryPrimitives.ReadUInt16LittleEndian(local.AsSpan(28)));
                if (dataStart + uncompressedSize > archive.Length)
                {
                    throw new InvalidDataException("ZIP entry extends past the end of the archive.");
                }

                return (localHeaderStart, position, dataStart, uncompressedSize);
            }

            position = checked(position + RecordLength + nameLength + extraLength + commentLength);
        }

        throw new InvalidDataException($"ZIP entry '{entryName}' is missing from the central directory.");
    }

    /// <summary>
    /// Restores the widened values from a ZIP64 entry extra block in its fixed order (uncompressed size, compressed
    /// size, local header offset), consuming a slot only when that field still holds its placeholder.
    /// <para>从 ZIP64 条目扩展字段按固定顺序还原被加宽的值（原始长度、压缩长度、本地头偏移），只读取实际为占位值的槽位。</para>
    /// </summary>
    private static (long UncompressedSize, long CompressedSize, long LocalHeaderStart) ReadZip64Extra(
        ReadOnlySpan<byte> extra, long uncompressedSize, long compressedSize, long localHeaderStart)
    {
        for (var offset = 0; offset + 4 <= extra.Length;)
        {
            var type = BinaryPrimitives.ReadUInt16LittleEndian(extra.Slice(offset));
            var length = BinaryPrimitives.ReadUInt16LittleEndian(extra.Slice(offset + 2));
            if (length > extra.Length - offset - 4)
            {
                throw new InvalidDataException("Invalid ZIP extra field.");
            }

            if (type == 1)
            {
                var values = extra.Slice(offset + 4, length);
                var cursor = 0;
                if (uncompressedSize == uint.MaxValue)
                {
                    uncompressedSize = ReadZip64Slot(values, ref cursor);
                }

                if (compressedSize == uint.MaxValue)
                {
                    compressedSize = ReadZip64Slot(values, ref cursor);
                }

                if (localHeaderStart == uint.MaxValue)
                {
                    localHeaderStart = ReadZip64Slot(values, ref cursor);
                }

                return (uncompressedSize, compressedSize, localHeaderStart);
            }

            offset += 4 + length;
        }

        throw new InvalidDataException("ZIP64 entry is missing its extra field.");
    }

    private static long ReadZip64Slot(ReadOnlySpan<byte> values, ref int cursor)
    {
        if (cursor + 8 > values.Length)
        {
            throw new InvalidDataException("ZIP64 extra field is truncated.");
        }

        var value = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(values.Slice(cursor)));
        cursor += 8;
        return value;
    }

    private static byte[] ReadAt(Stream stream, long position, int length)
    {
        var buffer = new byte[length];
        stream.Position = position;
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static void WriteUInt32At(Stream stream, long position, uint value)
    {
        var buffer = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        stream.Position = position;
        stream.Write(buffer);
    }

    private static DeliveryFile[] Selected(DeliveryPlan plan) => [.. plan.Files.Where(f => !f.Optional || f.Default)];

    /// <summary><para>为进入 Payload 的容器镜像生成 <c>care_map.pb</c> 字节；只收录受支持且带 AVB footer 的分区，
    /// 没有任何可校验分区时返回 null，让包保持与不带 verity 时一致的成员集合。</para>
    /// Builds the <c>care_map.pb</c> bytes for container images, recording only supported partitions that
    /// carry an AVB footer; returns null when nothing is eligible so the member set matches a non-verity package.
    /// The scan reads the same source files the payload consumed, so the recorded ranges describe the bytes
    /// actually shipped rather than a staging copy.
    /// 扫描读取的对象就是 Payload 消费过的那批源文件，因此记录到的区间描述的是实际交付的字节，而不是暂存副本。</summary>
    /// <param name="container">Partitions staged for the payload. <para>已暂存进 Payload 的分区。</para></param>
    /// <param name="token">Cancels the scan. <para>取消扫描。</para></param>
    /// <returns>The serialized care map, or null. <para>序列化后的 care map，或 null。</para></returns>
    private static byte[]? BuildCareMap(List<DeliveryFile> container, CancellationToken token)
    {
        var careMap = new CareMap();
        foreach (var file in container)
        {
            token.ThrowIfCancellationRequested();
            if (file.Partition.Length == 0 || !CareMapBuilder.IsSupportedPartition(file.Partition))
            {
                continue;
            }

            var imagePath = file.SourcePath;
            if (!File.Exists(imagePath))
            {
                continue;
            }

            var verified = AvbImageFootprint.TryRead(imagePath, token);
            if (verified is null)
            {
                continue;
            }

            var record = CareMapBuilder.TryBuildFromImage(file.Partition, imagePath, verified.Value, token);
            if (record is not null)
            {
                careMap.Partitions.Add(record);
            }
        }

        return careMap.Partitions.Count == 0 ? null : careMap.ToBytes();
    }

    /// <summary><para>判断文件名是否为 DAT/BR 刷写工件（.transfer.list/.new.dat[.br]/.patch.dat[.br]）。</para>
    /// Determines whether a filename is a DAT/BR flashing artifact (.transfer.list/.new.dat[.br]/.patch.dat[.br]).</summary>
    internal static bool IsDatArtifact(string fileName) => DatFileSet.IsArtifact(fileName);

    /// <summary><para>判断文件名是否为可写入 Recovery 的工件（.img 或 DAT 工件）。</para>
    /// Determines whether a filename is a Recovery-flashable artifact (an .img or a DAT artifact).</summary>
    internal static bool IsRecoveryArtifact(string fileName) =>
        fileName.EndsWith(".img", StringComparison.OrdinalIgnoreCase) || IsDatArtifact(fileName);

    /// <summary>Determines whether two filenames belong to the same DAT partition set. <para>判断两个文件名是否同属一个 DAT 分区组。</para></summary>
    internal static bool SameDatSet(string first, string second) => IsDatArtifact(first) && IsDatArtifact(second)
        && string.Equals(DatStem(first), DatStem(second), StringComparison.OrdinalIgnoreCase);

    /// <summary><para>去掉 DAT 工件后缀返回分区基名；非工件返回原名。</para>
    /// Strips the DAT artifact suffix returning the partition stem; non-artifacts return the name unchanged.</summary>
    internal static string DatStem(string fileName) => DatFileSet.Stem(fileName);

    private static void ValidateDatArtifacts(IReadOnlyList<DeliveryFile> files)
    {
        foreach (var group in files.Where(f => IsDatArtifact(f.FileName)).GroupBy(f => DatStem(f.FileName), StringComparer.OrdinalIgnoreCase))
        {
            var names = group.Select(f => f.FileName).ToArray();
            var transfers = names.Count(DatFileSet.IsTransferList);
            var newData = names.Count(DatFileSet.IsNewData);
            var patches = names.Count(DatFileSet.IsPatchData);
            if (group.Select(f => f.Partition).Distinct(StringComparer.Ordinal).Count() != 1
                || transfers != 1 || newData != 1 || patches > 1)
                throw new InvalidDataException($"DAT 文件组须包含唯一的 transfer.list 与 new.dat，最多一个 patch.dat，且分区名一致：{group.Key}");
        }
    }

    private static async Task AddHashedMemberAsync(ZipArchive zip, string member, Stream input, StringBuilder hashes,
        CancellationToken token, IProgress<DeliveryProgress>? progress)
    {
        var length = input.Length;
        void Report(long count) => progress?.Report(new(85 + count * 4.0 / Math.Max(1, length), "Packaging ZIP",
            (count, length), "Delivery_ProgressPackage", Detail: member));
        Report(0);
        using var hashAlgorithm = Sha256.CreateAccumulator();
        await using var output = Entry(zip, member).Open();
        var buffer = new byte[1024 * 1024];
        long written = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            hashAlgorithm.Append(buffer.AsSpan(0, read));
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            written = checked(written + read);
            Report(written);
        }

        Report(written);
        hashes.Append(Convert.ToHexStringLower(hashAlgorithm.GetHashAndReset())).Append("  ").Append(member).Append('\n');
    }

    private static async Task<string> ExpandPatchAsync(string source, string stage, CancellationToken token)
    {
        var temporary = Path.Combine(stage, ".patch-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var input = File.OpenRead(source);
            await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
            await using var capped = new CappedWriteStream(output, 64L * 1024 * 1024 * 1024, token);
            using var strict = new StrictBrotliReadStream(input);
            await strict.CopyToAsync(capped, token);
            return temporary;
        }
        catch { File.Delete(temporary); throw; }
    }
    private static ZipArchiveEntry Entry(ZipArchive zip, string name)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
        entry.LastWriteTime = new DateTimeOffset(2009, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return entry;
    }

    /// <summary>
    /// Writes a whole file as a stored entry whose sizes are readable from its local file header. The copy follows the
    /// chunked loop shape of <see cref="AddHashedMemberAsync"/>, checking the cancellation token per block, so a
    /// <c>payload.bin</c> beyond 4 GiB stays cancellable while it transfers. No digest is folded into the loop: the OTA
    /// archive keeps no per-member checksum list, and payload.bin's hash is already delivered through
    /// payload_properties.txt.
    /// <para>把整个文件作为 stored 条目写入归档，条目大小在本地文件头中直接可读。复制按
    /// <see cref="AddHashedMemberAsync"/> 的分块循环形状进行，逐块检查取消令牌，因此超过 4 GiB 的
    /// <c>payload.bin</c> 在传输中途仍可取消。这里不把摘要顺带算出来：OTA 归档的 stored 成员没有逐成员校验清单，
    /// payload.bin 的摘要已经由 payload_properties.txt 交付。</para>
    /// </summary>
    private static async Task WriteStoredAsync(ZipArchive zip, string name, string path, CancellationToken token)
    {
        await using var source = File.OpenRead(path);
        await WriteStoredAsync(zip, name, source, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes stream content as a stored entry, releasing the entry stream only after the data so sizes and CRC-32 are
    /// final; the transfer moves in 1 MiB blocks and honours cancellation block by block.
    /// <para>把流内容作为 stored 条目写入归档，写入完成后才释放条目流以确定大小与 CRC-32；转移按 1 MiB 分块并
    /// 逐块响应取消。</para>
    /// </summary>
    private static async Task WriteStoredAsync(ZipArchive zip, string name, Stream source, CancellationToken token)
    {
        await using var entry = Entry(zip, name).Open();
        await source.CopyToAsync(entry, 1024 * 1024, token).ConfigureAwait(false);
    }
    private static async Task AddTextAsync(ZipArchive zip, string name, string text, CancellationToken token)
    {
        await using var output = Entry(zip, name).Open();
        await output.WriteAsync(Encoding.UTF8.GetBytes(text), token);
    }

    internal static X509Certificate2 LoadCertificate(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > 65536) throw new InvalidDataException("签名证书不存在或超过 64 KiB。");
        var bytes = File.ReadAllBytes(path);
        return Encoding.ASCII.GetString(bytes).Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal)
            ? X509Certificate2.CreateFromPem(Encoding.ASCII.GetString(bytes)) : X509CertificateLoader.LoadCertificate(bytes);
    }

    /// <summary>
    /// Signs a ZIP comment using Android's whole-file format with the outer certificate/key; the key must match the certificate and the ZIP must have no existing comment.
    /// <para>按 Android 整文件格式用外层证书/私钥对 ZIP 做评论签名；证书与私钥必须匹配且 ZIP 无既有评论。</para>
    /// </summary>
    internal static async Task SignAsync(string path, DeliveryPackageOptions options, CancellationToken token,
        IDeliverySigningKeyProvider? signingKeys = null)
    {
        using var cert = LoadCertificate(options.CertificatePath);
        using var key = DeliverySigning.Load(signingKeys, options.SigningKeyPath);
        using var publicKey = cert.GetRSAPublicKey() ?? throw new InvalidDataException("外层证书需要 RSA 公钥。");
        if (!key.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(publicKey.ExportSubjectPublicKeyInfo()))
            throw new InvalidDataException("外层签名私钥与证书不匹配。");
        await using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1024 * 1024, true);
        if (file.Length < 22) throw new InvalidDataException("Invalid ZIP.");
        file.Position = file.Length - 22;
        var tail = new byte[22]; await file.ReadExactlyAsync(tail, token);
        if (BinaryPrimitives.ReadUInt32LittleEndian(tail) != 0x06054b50 || tail[20] != 0 || tail[21] != 0)
            throw new InvalidDataException("ZIP already has a comment.");
        file.Position = 0;
        using var digest = Sha256.CreateAccumulator();
        var buffer = new byte[1024 * 1024]; var remaining = file.Length - 2;
        while (remaining > 0)
        {
            var count = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token);
            if (count == 0) throw new EndOfStreamException();
            digest.Append(buffer.AsSpan(0, count)); remaining -= count;
        }
        var signature = key.SignHash(digest.GetHashAndReset(), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var cms = EncodeCms(cert, signature);
        var message = Encoding.ASCII.GetBytes("signed by UotanStudio\0");
        var total = checked(message.Length + cms.Length + 6);
        if (total > ushort.MaxValue) throw new InvalidDataException("ZIP signature exceeds comment size.");
        var comment = new byte[total]; message.CopyTo(comment, 0); cms.CopyTo(comment, message.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(comment.AsSpan(total - 6), checked((ushort)(cms.Length + 6)));
        comment[total - 4] = 255; comment[total - 3] = 255;
        BinaryPrimitives.WriteUInt16LittleEndian(comment.AsSpan(total - 2), (ushort)total);
        if (comment.AsSpan().IndexOf(new byte[] { 0x50, 0x4b, 0x05, 0x06 }) >= 0) throw new CryptographicException("Spurious ZIP end marker in signature.");
        file.Position = file.Length - 2;
        var length = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(length, (ushort)total);
        await file.WriteAsync(length, token); await file.WriteAsync(comment, token);
    }

    private static byte[] EncodeCms(X509Certificate2 cert, byte[] signature)
    {
        const string sha256 = "2.16.840.1.101.3.4.2.1", data = "1.2.840.113549.1.7.1";
        var writer = new AsnWriter(AsnEncodingRules.DER);
        var context = new Asn1Tag(TagClass.ContextSpecific, 0, true);
        void Algorithm(string oid) { writer.PushSequence(); writer.WriteObjectIdentifier(oid); writer.WriteNull(); writer.PopSequence(); }
        writer.PushSequence(); writer.WriteObjectIdentifier("1.2.840.113549.1.7.2"); writer.PushSequence(context);
        writer.PushSequence(); writer.WriteInteger(1); writer.PushSetOf(); Algorithm(sha256); writer.PopSetOf();
        writer.PushSequence(); writer.WriteObjectIdentifier(data); writer.PopSequence();
        writer.PushSetOf(context); writer.WriteEncodedValue(cert.RawData); writer.PopSetOf(context);
        writer.PushSetOf(); writer.PushSequence(); writer.WriteInteger(1);
        writer.PushSequence(); writer.WriteEncodedValue(cert.IssuerName.RawData);
        var serial = cert.GetSerialNumber(); Array.Reverse(serial); writer.WriteIntegerUnsigned(serial); writer.PopSequence();
        Algorithm(sha256); Algorithm("1.2.840.113549.1.1.1"); writer.WriteOctetString(signature);
        writer.PopSequence(); writer.PopSetOf(); writer.PopSequence(); writer.PopSequence(context); writer.PopSequence();
        return writer.Encode();
    }

}