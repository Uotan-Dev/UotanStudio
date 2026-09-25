using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.OnePlusOps.Crypto;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.OnePlusOps.Extraction;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.OnePlusOps.Logging;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.OnePlusOps.Models;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Repacking;
using UotanStudio.Firmware.SharedKernel.Streams;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.OnePlusOps;

/// <summary>
/// Format entry point for OnePlus OPS packages: detects the container, reads its header and entry table,
/// extracts each partition payload next to an optional decrypted sidecar, and rebuilds an OPS file from an
/// extracted directory or from an existing package.
/// <para>OnePlus OPS 包的格式入口：识别容器、读取其头部与条目表、解出每个分区的负载并按需附带解密侧文件，
/// 并从解包目录或既有包重建 OPS 文件。</para>
/// </summary>
/// <remarks>
/// Every untrusted package is parsed with bounded work: the entry count is read only when it lies between
/// <c>1</c> and <c>10000</c>, and each payload is sized by the 32-bit entry length recorded in the table.
/// Parsing or I/O failures arrive as a failure <see cref="FirmwareOpResult"/>, while cancellation stays an
/// exception so callers can tell the two apart.
/// <para>对不可信包的解析全程有界：只有条目数落在 <c>1</c> 到 <c>10000</c> 之间才会读取条目表，
/// 每段负载的大小由条目表中记录的 32 位长度决定。解析或 I/O 失败以失败的 <see cref="FirmwareOpResult"/> 返回，
/// 取消仍以异常抛出，便于调用方区分两者。</para>
/// </remarks>
/// <example>
/// <code>
/// var handler = new OnePlusOpsFormatHandler();
/// if (handler.CanHandle(opsPath))
/// {
///     var info = await handler.ReadMetadataAsync(opsPath, cancellationToken);
/// }
/// </code>
/// </example>
/// <param name="logger">Sink for parsing diagnostics; <see langword="null"/> keeps the handler silent.
/// <para>解析诊断的输出端；为 <see langword="null"/> 时不输出任何日志。</para></param>
public sealed class OnePlusOpsFormatHandler(IOnePlusLogger? logger = null) : IFormatHandler, IRepackMetadataProvider
{
    void IRepackMetadataProvider.ConfigureRepackMetadata(RepackMetadata metadata, string sourcePath, string outputDirectory)
    {
        metadata.CanPackDirectory = true;
    }

    private readonly IOnePlusLogger _logger = logger ?? NullLogger.Instance;

    /// <summary>
    /// Registration name of this format.
    /// <para>本格式的注册名称。</para></summary>
    /// <value>
    /// The fixed string <c>OnePlus OPS Firmware</c>, used as the detector key and written into repack
    /// metadata as the owning format.
    /// <para>固定字符串 <c>OnePlus OPS Firmware</c>，既作为检测器的键，也作为所属格式写入重打包元数据。</para>
    /// </value>
    public string FormatName => "OnePlus OPS Firmware";

    /// <summary>
    /// File extensions claimed by this format.
    /// <para>本格式声明的文件扩展名。</para></summary>
    /// <value>
    /// A single lower-case entry <c>ops</c> without the leading dot; the extension alone never qualifies a
    /// file, because detection also checks the magic bytes.
    /// <para>仅含一项小写条目 <c>ops</c>，不含前导点；单凭扩展名从不判定文件合格，因为识别还会检查魔数字节。</para>
    /// </value>
    public IReadOnlyList<string> FileExtensions => ["ops"];

    /// <summary>
    /// Determines whether the file matches this format.
    /// <para>判断文件是否匹配此格式。</para>
    /// </summary>
    /// <param name="filePath">Candidate path; the extension is compared case-insensitively and only the first
    /// four bytes, <c>OPS</c> followed by a zero byte, are read from disk.
    /// <para>候选路径；扩展名按忽略大小写比较，磁盘上仅读取前四个字节，即 <c>OPS</c> 加一个零字节。</para></param>
    /// <returns><see langword="true"/> only when the path is non-blank, the file exists, it ends in
    /// <c>.ops</c> and the magic matches; a blank path, a missing file, a wrong extension or any read error
    /// all yield <see langword="false"/>.
    /// <para>仅当路径非空白、文件存在、以 <c>.ops</c> 结尾且魔数匹配时返回 <see langword="true"/>；
    /// 空白路径、文件缺失、扩展名不符或任意读取错误均返回 <see langword="false"/>。</para></returns>
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        var ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
        if (ext != "ops")
        {
            return false;
        }

        try
        {
            using var fs = File.OpenRead(filePath);
            Span<byte> magic = stackalloc byte[4];
            return fs.Read(magic) >= 4 && magic[0] == (byte)'O' && magic[1] == (byte)'P' && magic[2] == (byte)'S' && magic[3] == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the firmware metadata asynchronously.
    /// <para>异步读取固件元数据。</para>
    /// </summary>
    /// <remarks>
    /// Only the 52-byte header and the entry table are consumed; partition payloads are never touched, so a
    /// truncated data section does not fail the read. An entry count of <c>0</c> or above <c>10000</c> is
    /// reported as an empty entry list.
    /// <para>只消费 52 字节头部与条目表，从不读取分区负载，因此数据段被截断也不会让本次读取失败。
    /// 条目数为 <c>0</c> 或超过 <c>10000</c> 时报告为空条目列表。</para>
    /// </remarks>
    /// <param name="filePath">OPS package to open; the file must exist and be readable, and a blank path is
    /// reported as a failure result.
    /// <para>待打开的 OPS 包；文件必须存在且可读，空白路径会报告为失败结果。</para></param>
    /// <param name="cancellationToken">Checked before any work and handed to the thread-pool item; a signalled
    /// token surfaces as an <see cref="OperationCanceledException"/> on the returned task and is not converted
    /// into a failure result.
    /// <para>在任何工作前检查并交给线程池项；已触发的令牌以返回任务上的 <see cref="OperationCanceledException"/> 呈现，绝不转换为失败结果。</para></param>
    /// <returns>A success result whose <see cref="FirmwareOpResult.Entries"/> hold the entry names and whose
    /// <see cref="FirmwareOpResult.Metadata"/> hold <c>Version</c>, <c>HeaderSize</c>, <c>FileSize</c>,
    /// <c>Flags</c> and <c>EntryCount</c>; a failure result carrying the exception message when the header
    /// magic is invalid, the stream ends early or the file cannot be opened.
    /// <para>成功结果的 <see cref="FirmwareOpResult.Entries"/> 保存条目名称，
    /// <see cref="FirmwareOpResult.Metadata"/> 保存 <c>Version</c>、<c>HeaderSize</c>、<c>FileSize</c>、
    /// <c>Flags</c> 与 <c>EntryCount</c>；头部魔数无效、流提前结束或文件无法打开时返回携带异常消息的失败结果。</para></returns>
    public Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var fs = File.OpenRead(filePath);
                var decryptor = new OpsDecryptor(fs, _logger);
                var header = decryptor.ParseHeader();
                var entries = ReadEntries(decryptor, header);

                var summary = $"OPS firmware parsed.\nVersion: {header.Version}\n" +
                              $"Entries: {entries.Count}\nFile size: {header.FileSize} bytes";
                var metadata = new Dictionary<string, string>
                {
                    ["Version"] = header.Version.ToString(),
                    ["HeaderSize"] = header.HeaderSize.ToString(),
                    ["FileSize"] = header.FileSize.ToString(),
                    ["Flags"] = header.Flags.ToString(),
                    ["EntryCount"] = entries.Count.ToString()
                };

                return FirmwareOpResult.Ok(summary, entries: [.. entries.Select(e => e.Name)], metadata: metadata);
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail(ex.Message);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Extracts the firmware contents asynchronously.
    /// <para>异步提取固件内容。</para>
    /// </summary>
    /// <remarks>
    /// Each entry is written verbatim as stored, under a name whose invalid file-name characters are replaced
    /// by <c>_</c>; an entry with a blank name becomes <c>entry_&lt;id&gt;</c>. When
    /// <see cref="TryDecrypt(byte[])"/> recognises a key, the plaintext is written next to it as
    /// <c>&lt;name&gt;.decrypted</c>. After a successful extraction the repack metadata record is published, so
    /// the directory can be packed again.
    /// <para>每个条目按存储原样写出，文件名中的非法字符替换为 <c>_</c>；名称为空的条目写成 <c>entry_&lt;id&gt;</c>。
    /// 当 <see cref="TryDecrypt(byte[])"/> 识别出密钥时，明文以 <c>&lt;名称&gt;.decrypted</c> 写在同目录。
    /// 解包成功后会发布重打包元数据记录，使该目录可以再次打包。</para>
    /// </remarks>
    /// <param name="filePath">OPS package to read; the input file is never modified.
    /// <para>待读取的 OPS 包；输入文件不会被修改。</para></param>
    /// <param name="outputDirectory">Destination directory, created when missing; it also receives the repack
    /// metadata record.
    /// <para>目标目录，缺失时创建；重打包元数据记录也写入该目录。</para></param>
    /// <param name="options">Only <see cref="UnpackOptions.Overwrite"/> is consulted: with overwrite disabled
    /// an existing destination file is kept and still listed as written. The OPS entry table carries no
    /// checksum and no per-entry selection is applied, so the other options have no effect.
    /// <see langword="null"/> uses the <see cref="UnpackOptions"/> defaults.
    /// <para>仅读取 <see cref="UnpackOptions.Overwrite"/>：关闭覆盖时已存在的目标文件会保留并仍被列为已写出。
    /// OPS 条目表不含校验和，也不做逐条目选择，因此其余选项无效。传 <see langword="null"/> 时使用
    /// <see cref="UnpackOptions"/> 默认值。</para></param>
    /// <param name="cancellationToken">Checked before the extraction and again before every entry; a signalled
    /// token propagates as <see cref="OperationCanceledException"/> and leaves whatever was already written in
    /// place without publishing metadata.
    /// <para>在解包前以及每个条目处理前检查；已触发的令牌以 <see cref="OperationCanceledException"/> 抛出，
    /// 已写出的文件保留原位且不发布元数据。</para></param>
    /// <returns>A success result listing the written file paths in <see cref="FirmwareOpResult.Entries"/> and
    /// reporting <c>Version</c>, <c>EntryCount</c> and the comma-joined <c>DecryptedEntries</c> in
    /// <see cref="FirmwareOpResult.Metadata"/>; a failure result carrying the exception message when the package
    /// cannot be parsed or a write fails.
    /// <para>成功结果在 <see cref="FirmwareOpResult.Entries"/> 中列出已写出的文件路径，并在
    /// <see cref="FirmwareOpResult.Metadata"/> 中报告 <c>Version</c>、<c>EntryCount</c> 以及以逗号拼接的
    /// <c>DecryptedEntries</c>；包无法解析或写入失败时返回携带异常消息的失败结果。</para></returns>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was signalled. <para><paramref name="cancellationToken"/> 已被触发。</para></exception>
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
        => UotanStudio.Firmware.SharedKernel.Repacking.RepackMetadata.UnpackAsync(this, filePath, outputDirectory, options,
            cancellationToken, () => UnpackCoreAsync(filePath, outputDirectory, options, cancellationToken));

    private Task<FirmwareOpResult> UnpackCoreAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Directory.CreateDirectory(outputDirectory);
                var overwrite = options?.Overwrite ?? true;

                using var fs = File.OpenRead(filePath);
                var decryptor = new OpsDecryptor(fs, _logger);
                var header = decryptor.ParseHeader();
                var entries = ReadEntries(decryptor, header);

                var written = new List<string>();
                var decrypted = new List<string>();

                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = SanitizeFileName(string.IsNullOrWhiteSpace(entry.Name) ? $"entry_{entry.Id}" : entry.Name);
                    var outPath = Path.Combine(outputDirectory, name);
                    if (!overwrite && File.Exists(outPath))
                    {
                        written.Add(outPath);
                        continue;
                    }

                    var raw = decryptor.ReadEntryData(entry.Offset, entry.Size);
                    File.WriteAllBytes(outPath, raw);
                    written.Add(outPath);

                    var dec = TryDecrypt(raw);
                    if (dec != null)
                    {
                        var decPath = Path.Combine(outputDirectory, name + ".decrypted");
                        File.WriteAllBytes(decPath, dec);
                        written.Add(decPath);
                        decrypted.Add(name);
                    }
                }

                var metadata = new Dictionary<string, string>
                {
                    ["Version"] = header.Version.ToString(),
                    ["EntryCount"] = entries.Count.ToString(),
                    ["DecryptedEntries"] = string.Join(",", decrypted)
                };

                return FirmwareOpResult.Ok(
                    $"Unpacked {entries.Count} OPS partitions to {outputDirectory} (decrypted: {decrypted.Count})",
                    entries: written,
                    metadata: metadata);
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail(ex.Message);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Packs the input into firmware asynchronously.
    /// <para>异步将输入打包为固件。</para>
    /// </summary>
    /// <remarks>
    /// A directory input becomes one entry per file, ordered by ordinal path, skipping repack-metadata files
    /// and <c>.decrypted</c> sidecars; entry names are truncated to the 20-byte table field. A file input is
    /// parsed as an OPS package and its entry payloads are copied byte for byte. Neither path encrypts or
    /// decrypts payload bytes, and each payload is streamed in bounded copies rather than buffered whole.
    /// <para>目录输入按序数路径顺序为每个文件生成一个条目，跳过重打包元数据文件与 <c>.decrypted</c> 侧文件；
    /// 条目名截断到 20 字节的表字段。文件输入按 OPS 包解析并逐字节复制其条目负载。两条路径都不对负载加解密，
    /// 每段负载以有界复制写出而非整体缓冲。</para>
    /// </remarks>
    /// <param name="inputPath">Extraction directory or existing OPS package; a blank path, or a path that is
    /// neither an existing file nor an existing directory, is reported as a failure result.
    /// <para>解包目录或既有 OPS 包；空白路径，或既非已存在文件也非已存在目录的路径，会报告为失败结果。</para></param>
    /// <param name="outputPath">Destination file, created fresh: an existing file at this path is replaced
    /// during the write, and the header's recorded total size is patched once all payloads are appended.
    /// <para>目标文件，以新建方式写出：该路径上的已有文件会在写入时被替换，头部的总长度字段在所有负载追加完成后回填。</para></param>
    /// <param name="cancellationToken">Checked before any work; a signalled token surfaces as an
    /// <see cref="OperationCanceledException"/> on the returned task, never as a failure result.
    /// <para>在任何工作前检查；已触发的令牌以返回任务上的 <see cref="OperationCanceledException"/> 呈现，绝不转换为失败结果。</para></param>
    /// <returns>A success result whose summary names the produced path; a failure result carrying the exception
    /// message when the input cannot be read, an entry exceeds the 32-bit size field, or the output cannot be
    /// written.
    /// <para>成功结果的摘要写有产出路径；输入不可读、条目超出 32 位长度字段或输出无法写入时返回携带异常消息的失败结果。</para></returns>
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (string.IsNullOrWhiteSpace(inputPath) || (!File.Exists(inputPath) && !Directory.Exists(inputPath)))
                {
                    return FirmwareOpResult.Fail("Input path does not exist.");
                }

                if (Directory.Exists(inputPath))
                {
                    PackFromDirectory(inputPath, outputPath);
                    return FirmwareOpResult.Ok($"Packed OPS firmware from directory {inputPath} to {outputPath}");
                }

                var source = File.OpenRead(inputPath);
                try
                {
                    var decryptor = new OpsDecryptor(source, _logger);
                    var header = decryptor.ParseHeader();
                    var entries = ReadEntries(decryptor, header);
                    WriteOps(outputPath, entries, source);
                }
                finally
                {
                    source.Dispose();
                }

                return FirmwareOpResult.Ok($"Repacked OPS firmware to {outputPath}");
            }
            catch (Exception ex)
            {
                return FirmwareOpResult.Fail(ex.Message);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Tries to decrypt partition data with every key in the OnePlusKeyTable.
    /// A non-matching key is rejected on a probe window, so a full-image pass runs only for the key whose
    /// probe matched; cryptographic errors from one key are swallowed and the search continues with the next.
    /// <para>尝试使用 OnePlusKeyTable 中的全部密钥解密分区数据。不匹配的密钥在试探窗口上即被拒绝，
    /// 因此只有试探命中的密钥才执行全量解密；单个密钥抛出的加密错误会被吞掉，搜索继续转向下一个密钥。</para>
    /// </summary>
    /// <param name="data">Candidate partition payload. It must be at least one AES block long and an exact
    /// multiple of <c>16</c> bytes, because the AES-ECB path decrypts whole blocks without padding; the buffer
    /// is read only.
    /// <para>候选分区负载。长度至少一个 AES 块且必须是 <c>16</c> 字节的整数倍，因为 AES-ECB 路径按整块解密且不带填充；缓冲仅读取。</para></param>
    /// <returns>The plaintext from the first accepted candidate: an AES-ECB pass with the matching key, whose
    /// whole result is byte-reversed when only the reversed form produced the recognised magic, or the fixed
    /// SBox stream-cipher result; <see langword="null"/> when the input is <see langword="null"/> or too short,
    /// when it already starts with a recognised partition magic, or when no key and the SBox path produce
    /// accepted plaintext.
    /// <para>第一个被接受候选产出的明文：使用匹配密钥的 AES-ECB 结果（仅反转形式命中可识别魔数时整体反转其输出），
    /// 或固定 SBox 流密码结果；输入为 <see langword="null"/> 或过短、开头已具备可识别的分区魔数，
    /// 或所有密钥与 SBox 路径都未产出可接受的明文时返回 <see langword="null"/>。</para></returns>
    public static byte[]? TryDecrypt(byte[] data)
    {
        if (data == null || data.Length < 16 || data.Length % OpsConstants.AES_BLOCK_SIZE != 0)
        {
            return null;
        }

        if (LooksLikeDecrypted(data))
        {
            return null;
        }

        if (data.Length <= DecryptProbeLength)
        {
            return TryDecryptSmall(data);
        }

        var probeLength = DecryptProbeLength;
        foreach (var key in OnePlusKeyTable.GetAllKeys())
        {
            try
            {
                var head = DecryptAesEcbSegment(data, 0, probeLength, key);
                if (LooksLikeDecrypted(head))
                {
                    return DecryptAesEcbStatic(data, key);
                }

                var tail = DecryptAesEcbSegment(data, data.Length - probeLength, probeLength, key, reverseOutput: true);
                if (LooksLikeDecrypted(tail))
                {
                    return Reverse(DecryptAesEcbStatic(data, key));
                }
            }
            catch
            {

            }
        }

        try
        {
            if (LooksLikeDecrypted(SBoxDecryptSegment(data, 0, probeLength)))
            {
                return SBoxDecryptStatic(data);
            }
        }
        catch
        {

        }

        return null;
    }

    /// <summary>
    /// Probe window in bytes; covers every magic offset checked by
    /// <see cref="LooksLikeDecrypted"/> (the largest is 0x404 + 4).
    /// <para>试探窗口（字节），覆盖 <see cref="LooksLikeDecrypted"/> 检查的全部魔数偏移（最大 0x404+4）。</para>
    /// </summary>
    private const int DecryptProbeLength = 0x440;

    /// <summary>
    /// Full-buffer decrypt path for small payloads: the probe window would span the entire
    /// buffer anyway, so every candidate key is tried against the whole image.
    /// <para>小数据的整块解密路径：试探窗口本会覆盖整个缓冲，因此对完整数据逐一试探每个候选密钥。</para>
    /// </summary>
    private static byte[]? TryDecryptSmall(byte[] data)
    {
        foreach (var key in OnePlusKeyTable.GetAllKeys())
        {
            try
            {
                byte[]? decrypted = null;
                var candidate = DecryptAesEcbStatic(data, key);
                if (LooksLikeDecrypted(candidate))
                {
                    decrypted = candidate;
                }
                else if (LooksLikeDecrypted(Reverse(candidate)))
                {
                    decrypted = Reverse(candidate);
                }

                if (decrypted != null)
                {
                    return decrypted;
                }
            }
            catch
            {

            }
        }

        try
        {
            var sboxDecrypted = SBoxDecryptStatic(data);
            if (LooksLikeDecrypted(sboxDecrypted))
            {
                return sboxDecrypted;
            }
        }
        catch
        {

        }

        return null;
    }

    private static List<OpsEntry> ReadEntries(OpsDecryptor decryptor, OpsHeader header)
    {
        var count = header.EntryCount;
        if (count is 0 or > 10000)
        {
            return [];
        }

        return decryptor.ParseEntries(count);
    }

    private static void PackFromDirectory(string directory, string outputPath)
    {
        var files = Directory.GetFiles(directory)
            .Where(path => !RepackMetadata.IsMetadataFile(path))
            .Where(f => !f.EndsWith(".decrypted", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var entries = new List<OpsEntry>();
        var dataFiles = new string[files.Count];
        for (var i = 0; i < files.Count; i++)
        {
            var name = Path.GetFileName(files[i]);
            var length = new FileInfo(files[i]).Length;
            if (length > uint.MaxValue)
            {
                throw new InvalidDataException($"Entry '{name}' exceeds the OPS 32-bit entry size limit.");
            }

            entries.Add(new OpsEntry
            {
                Id = (uint)i,
                Name = name.Length > OpsConstants.ENTRY_NAME_SIZE ? name[..OpsConstants.ENTRY_NAME_SIZE] : name,
                Size = (uint)length
            });
            dataFiles[i] = files[i];
        }

        WriteOps(outputPath, entries, dataFiles: dataFiles);
    }

    /// <summary>
    /// Writes an OPS file. Payload bytes come from, in priority order: the per-entry
    /// files in <paramref name="dataFiles"/>, the matching offsets of
    /// <paramref name="source"/>, or the in-memory <see cref="OpsEntry.Data"/>.
    /// <para>写出 OPS 文件。条目数据按优先级取自：<paramref name="dataFiles"/> 中的
    /// 逐条目文件、<paramref name="source"/> 的对应偏移，或内存中的
    /// <see cref="OpsEntry.Data"/>。</para>
    /// </summary>
    private static void WriteOps(string outputPath, List<OpsEntry> entries, Stream? source = null, string[]? dataFiles = null)
    {
        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(fs, Encoding.ASCII, leaveOpen: false);

        writer.Write(Encoding.ASCII.GetBytes(OpsConstants.OPS_MAGIC));
        writer.Write(1u);
        writer.Write((uint)OpsConstants.HEADER_SIZE);
        writer.Write(0u);
        writer.Write((uint)entries.Count);
        writer.Write(0u);
        for (var i = OpsConstants.HEADER_SIZE - 24; i > 0; i--)
        {
            writer.Write((byte)0);
        }

        var entryTableOffset = writer.BaseStream.Position;
        var dataOffsets = new long[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            dataOffsets[i] = -1;
            writer.Write(entry.Id);
            writer.Write(0u);
            writer.Write(entry.Data is { Length: > 0 } data ? (uint)data.Length : entry.Size);
            var nameBytes = new byte[OpsConstants.ENTRY_NAME_SIZE];
            var nameSrc = Encoding.ASCII.GetBytes(entry.Name ?? string.Empty);
            Array.Copy(nameSrc, nameBytes, Math.Min(nameSrc.Length, nameBytes.Length));
            writer.Write(nameBytes);
        }

        for (var i = 0; i < entries.Count; i++)
        {
            Align(writer, OpsConstants.SECTOR_SIZE);
            dataOffsets[i] = writer.BaseStream.Position;
            var entry = entries[i];
            if (dataFiles is not null && dataFiles[i] is { } dataFile)
            {
                using var dataStream = File.OpenRead(dataFile);
                StreamCopy.CopyExactly(dataStream, writer.BaseStream, entry.Size);
            }
            else if (source is not null)
            {
                source.Seek(entry.Offset, SeekOrigin.Begin);
                StreamCopy.CopyExactly(source, writer.BaseStream, entry.Size);
            }
            else
            {
                writer.Write(entry.Data ?? []);
            }
        }

        Align(writer, OpsConstants.SECTOR_SIZE);

        writer.BaseStream.Seek(entryTableOffset, SeekOrigin.Begin);
        for (var i = 0; i < entries.Count; i++)
        {
            writer.BaseStream.Seek(entryTableOffset + ((long)i * OpsConstants.ENTRY_HEADER_SIZE) + 4, SeekOrigin.Begin);
            writer.Write((uint)dataOffsets[i]);
        }

        writer.BaseStream.Seek(12, SeekOrigin.Begin);
        writer.Write((uint)writer.BaseStream.Length);

        writer.Flush();
    }

    private static void Align(BinaryWriter writer, int alignment)
    {
        var pos = writer.BaseStream.Position;
        var remainder = (int)(pos % alignment);
        if (remainder == 0)
        {
            return;
        }

        var padding = alignment - remainder;
        for (var i = 0; i < padding; i++)
        {
            writer.Write((byte)0);
        }
    }

    private static byte[] DecryptAesEcbStatic(byte[] data, byte[] key)
    {
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Mode = System.Security.Cryptography.CipherMode.ECB;
        aes.Padding = System.Security.Cryptography.PaddingMode.None;
        aes.Key = key;
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(data, 0, data.Length);
    }

    /// <summary>
    /// Decrypts a single AES block-aligned segment of <paramref name="data"/> without
    /// allocating a full-image plaintext; used by the probe path of
    /// <see cref="TryDecrypt"/>. Optionally reverses the decrypted segment so tail
    /// probes match the reversed-data convention.
    /// <para>仅解密 <paramref name="data"/> 的一个块对齐分段，不为整镜像分配明文；
    /// 供 <see cref="TryDecrypt"/> 的试探路径使用。可选地在解密后反转分段，
    /// 以匹配尾部试探的反转数据约定。</para>
    /// </summary>
    private static byte[] DecryptAesEcbSegment(byte[] data, int start, int length, byte[] key, bool reverseOutput = false)
    {
        if (start < 0 || length < 0 || start > data.Length - length)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Mode = System.Security.Cryptography.CipherMode.ECB;
        aes.Padding = System.Security.Cryptography.PaddingMode.None;
        aes.Key = key;
        using var decryptor = aes.CreateDecryptor();
        var output = new byte[length];
        decryptor.TransformBlock(data, start, length, output, 0);
        if (reverseOutput)
        {
            Array.Reverse(output);
        }

        return output;
    }

    /// <summary>
    /// Decrypts a single 256-byte-period-aligned segment with the SBox stream cipher
    /// without allocating a full-image plaintext; used by the probe path of
    /// <see cref="TryDecrypt"/>.
    /// <para>仅用 SBox 流密码解密一个按 256 字节周期对齐的分段，不为整镜像分配明文；
    /// 供 <see cref="TryDecrypt"/> 的试探路径使用。</para>
    /// </summary>
    private static byte[] SBoxDecryptSegment(byte[] data, int start, int length)
    {
        if (start < 0 || length < 0 || start > data.Length - length)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        var alignedStart = start - (start % 256);
        length = start + length - alignedStart;
        var segment = new byte[length];
        Array.Copy(data, alignedStart, segment, 0, length);
        var sbox = SBoxTable();
        for (var i = 0; i < length; i++)
        {
            segment[i] ^= sbox[(alignedStart + i) % 256];
        }

        return segment;
    }

    /// <summary>
    /// Builds the default SBox table shared by the static decrypt paths.
    /// <para>构建静态解密路径共用的默认 SBox 表。</para>
    /// </summary>
    private static byte[] SBoxTable()
    {
        var key = Encoding.UTF8.GetBytes("OnePlus#@!$^&*()_+");
        var sbox = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            sbox[i] = (byte)i;
        }

        for (int i = 255, j = 0; i > 0; i--)
        {
            j = (j + key[i % key.Length] + sbox[i]) & 0xFF;
            (sbox[i], sbox[j]) = (sbox[j], sbox[i]);
        }

        return sbox;
    }

    private static byte[] SBoxDecryptStatic(byte[] data)
    {
        var key = Encoding.UTF8.GetBytes("OnePlus#@!$^&*()_+");
        var sbox = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            sbox[i] = (byte)i;
        }

        for (int i = 255, j = 0; i > 0; i--)
        {
            j = (j + key[i % key.Length] + sbox[i]) & 0xFF;
            (sbox[i], sbox[j]) = (sbox[j], sbox[i]);
        }

        var output = new byte[data.Length];
        for (var i = 0; i < data.Length; i++)
        {
            output[i] = (byte)(data[i] ^ sbox[i % 256]);
        }

        return output;
    }

    private static byte[] Reverse(byte[] data)
    {
        var copy = (byte[])data.Clone();
        Array.Reverse(copy);
        return copy;
    }

    /// <summary>
    /// Checks whether the data matches a known partition image magic.
    /// <para>检查数据是否命中已知分区镜像魔数。</para>
    /// </summary>
    internal static bool LooksLikeDecrypted(byte[] data)
    {
        if (data == null || data.Length < 8)
        {
            return false;
        }

        if (StartsWithAscii(data, "ANDROID!"))
        {
            return true;
        }

        if (StartsWithAscii(data, "VNDRBOOT"))
        {
            return true;
        }

        if (StartsWithAscii(data, "EFI PART"))
        {
            return true;
        }

        if (StartsWithAscii(data, "BOOTIMG!"))
        {
            return true;
        }

        if (StartsWithAscii(data, "CHROMEOS"))
        {
            return true;
        }

        if (StartsWithAscii(data, "U-Boot"))
        {
            return true;
        }

        if (StartsWithAscii(data, "EROFS"))
        {
            return true;
        }

        if (StartsWithAscii(data, "squashfs"))
        {
            return true;
        }

        var magic32 = BinaryPrimitives.ReadUInt32LittleEndian(data);

        if (magic32 == 0xED26FF3A)
        {
            return true;
        }

        if (magic32 == 0x58881688)
        {
            return true;
        }

        if (magic32 == 0x42484444)
        {
            return true;
        }

        if (data.Length >= 0x43A && BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x438, 2)) == 0xEF53)
        {
            return true;
        }

        return data.Length >= 0x404 && BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0x400, 4)) == 0xF2F52010;
    }

    private static bool StartsWithAscii(byte[] data, string magic)
    {
        if (data.Length < magic.Length)
        {
            return false;
        }

        for (var i = 0; i < magic.Length; i++)
        {
            if (data[i] != (byte)magic[i])
            {
                return false;
            }
        }
        return true;
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return name;
    }
}