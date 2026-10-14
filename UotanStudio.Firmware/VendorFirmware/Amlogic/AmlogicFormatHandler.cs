using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.Core;
using UotanStudio.Hash.Crc32Register;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Firmware.VendorFirmware.Amlogic;

/// <summary>AMLogic ampack header generations recognised by the parser and writer.
/// <para>解析器与写入器可识别的 AMLogic ampack 头部版本。</para></summary>
public enum AmlogicImageVersion : uint
{
    /// <summary>Version 1, with a 64-byte header and 128-byte item records. <para>版本 1：64 字节头部与 128 字节条目记录。</para></summary>
    V1 = 1,
    /// <summary>Version 2, with a 72-byte header and 576-byte item records. <para>版本 2：72 字节头部与 576 字节条目记录。</para></summary>
    V2 = 2
}

/// <summary>Groups the caller-supplied choices for creating an AMLogic ampack image.
/// <para>集中管理创建 AMLogic ampack 镜像时由调用方指定的选项。</para></summary>
internal sealed class AmlogicPackOptions
{
    /// <summary>The header generation to emit.
    /// <para>要写出的头部版本。</para></summary>
    /// <value><see cref="AmlogicImageVersion.V1"/> or <see cref="AmlogicImageVersion.V2"/>; the default is <see cref="AmlogicImageVersion.V2"/>.
    /// <para><see cref="AmlogicImageVersion.V1"/> 或 <see cref="AmlogicImageVersion.V2"/>；默认值为 <see cref="AmlogicImageVersion.V2"/>。</para></value>
    public AmlogicImageVersion Version { get; init; } = AmlogicImageVersion.V2;

    /// <summary>The byte alignment applied between items.
    /// <para>条目之间应用的字节对齐。</para></summary>
    /// <value>Must be <c>4</c> or <c>8</c>; the default is <c>4</c>.
    /// <para>只能为 <c>4</c> 或 <c>8</c>；默认值为 <c>4</c>。</para></value>
    public uint Alignment { get; init; } = 4;

    /// <summary>The payload entry names that receive SHA-1 VERIFY records.
    /// <para>需要写入 SHA-1 VERIFY 记录的负载条目名。</para></summary>
    /// <value>Case-insensitive file base names selected from the input directory, or <see langword="null"/> when no item is hash-recorded; every listed name must match an input file.
    /// <para>按不区分大小写从输入目录中选取的文件基名；为 <see langword="null"/> 时不记录任何条目的哈希；列出的每个名称都必须匹配某个输入文件。</para></value>
    public IReadOnlyCollection<string>? VerifiedEntries { get; init; }

    /// <summary>Whether the freshly written image is re-parsed and verified before the call returns.
    /// <para>是否在调用返回前重新解析并校验刚写出的镜像。</para></summary>
    /// <value><see langword="true"/> to run full CRC and SHA-1 verification on the output, <see langword="false"/> to skip it; the default is <see langword="true"/>.
    /// <para>为 <see langword="true"/> 时对输出执行完整 CRC 与 SHA-1 校验，为 <see langword="false"/> 时跳过；默认值为 <see langword="true"/>。</para></value>
    public bool VerifyOutput { get; init; } = true;
}

/// <summary>Reads, validates, converts and creates AMLogic USB burning images.
/// <para>读取、校验、转换并创建 AMLogic USB 烧录镜像。</para></summary>
internal sealed class AmlogicFormatHandler : IFormatHandler
{
    private const uint Magic = 0x27B51956;
    private const long MaximumItemBytes = 16L * 1024 * 1024 * 1024;
    private const int MaximumItems = 65_536;
    private const int VerifyDataSize = 48;

    /// <summary>The display name reported for this handler.
    /// <para>此处理器报告的可读格式名。</para></summary>
    /// <value>The fixed string <c>AMLogic ampack</c>.
    /// <para>固定字符串 <c>AMLogic ampack</c>。</para></value>
    public string FormatName => "AMLogic ampack";

    /// <summary>The lowercase extensions this handler advertises.
    /// <para>此处理器声明的小写扩展名列表。</para></summary>
    /// <value><c>img</c>, <c>aml</c> and <c>ampack</c>; detection itself relies on the magic word at byte offset 8, not on the extension.
    /// <para><c>img</c>、<c>aml</c> 与 <c>ampack</c>；识别本身依赖偏移 8 字节处的魔数，而非扩展名。</para></value>
    public IReadOnlyList<string> FileExtensions => ["img", "aml", "ampack"];

    /// <summary>Probes the file for the AMLogic magic word at byte offset 8.
    /// <para>探测文件偏移 8 字节处是否为 AMLogic 魔数。</para></summary>
    /// <param name="filePath">Path to test; missing or unreadable paths return <see langword="false"/>. <para>待测试的路径；不存在或不可读的路径返回 <see langword="false"/>。</para></param>
    /// <returns><see langword="true"/> when the file is at least 12 bytes and its little-endian uint32 at offset 8 equals <c>0x27B51956</c>; <see langword="false"/> for anything else, including read errors.
    /// <para>文件至少 12 字节且偏移 8 处的小端 uint32 等于 <c>0x27B51956</c> 时返回 <see langword="true"/>；其余情况（包括读取错误）返回 <see langword="false"/>。</para></returns>
    public bool CanHandle(string filePath)
    {
        return HeaderProbe.ProbeMagic(filePath, 12, 8, Magic);
    }

    /// <summary>Parses the image header and item table and reports their contents without verifying hashes.
    /// <para>解析镜像头部与条目表并报告其内容，不校验哈希。</para></summary>
    /// <param name="filePath">The AMLogic image to inspect. <para>要检查的 AMLogic 镜像。</para></param>
    /// <param name="cancellationToken">Cancels the parse; cancellation is rethrown, never converted to a failure result. <para>取消解析；取消异常会重新抛出，绝不转换为失败结果。</para></param>
    /// <returns>On success, a version summary, the payload (non-VERIFY) entry names and header metadata (magic offset/value, version, alignment, item count, length, CRC-32); on parse errors, a failure result carrying the exception message.
    /// <para>成功时返回版本摘要、负载（非 VERIFY）条目名与头部元数据（魔数偏移/取值、版本、对齐、条目数、长度、CRC-32）；解析出错时返回携带异常信息的失败结果。</para></returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is signaled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    public async Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var image = await ParseAsync(filePath, cancellationToken).ConfigureAwait(false);
            return FirmwareOpResult.Ok(
                $"AMLogic ampack v{(uint)image.Version} metadata confirmed ({image.Items.Count} items).",
                entries: [.. image.Items.Where(item => !item.IsVerify).Select(GetEntryName)],
                metadata: new Dictionary<string, string>
                {
                    ["MagicOffset"] = "8",
                    ["Magic"] = $"0x{Magic:X8}",
                    ["Version"] = ((uint)image.Version).ToString(),
                    ["Alignment"] = image.Alignment.ToString(),
                    ["ItemCount"] = image.Items.Count.ToString(),
                    ["Length"] = image.Length.ToString(),
                    ["Crc32"] = $"0x{image.Crc32:X8}"
                });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return FirmwareOpResult.Fail(ex.Message);
        }
    }

    /// <summary>Verifies the whole image, then copies the selected payload items out as independent files.
    /// <para>先校验整个镜像，再把选中的负载条目作为独立文件复制出来。</para></summary>
    /// <param name="filePath">The AMLogic image to unpack. <para>要解包的 AMLogic 镜像。</para></param>
    /// <param name="outputDirectory">Directory receiving the extracted files. <para>接收解出文件的目录。</para></param>
    /// <param name="options">Extraction knobs; <see langword="null"/> uses the defaults. Selected entry names must all exist, per-item sizes must fit <c>MaximumOutputBytes</c>, and existing outputs are refused when <c>Overwrite</c> is disabled. <para>解包选项；为 <see langword="null"/> 时使用默认值。选中的条目名必须全部存在，单条目大小必须不超过 <c>MaximumOutputBytes</c>，且 <c>Overwrite</c> 关闭时已存在的输出会使操作失败。</para></param>
    /// <param name="cancellationToken">Cancels between and during item copies; cancellation is rethrown, never converted to a failure result. <para>在条目复制之间与复制期间取消；取消异常会重新抛出，绝不转换为失败结果。</para></param>
    /// <returns>On success, the extracted item names after CRC-32 and VERIFY SHA-1 validation; on any validation, selection or I/O error, a failure result carrying the exception message. Each file is written atomically.
    /// <para>成功时返回在 CRC-32 与 VERIFY SHA-1 校验之后已解出的条目名；任何校验、筛选或 I/O 错误都会返回携带异常信息的失败结果。每个文件都以原子方式写入。</para></returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is signaled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    public async Task<FirmwareOpResult> UnpackAsync(
        string filePath,
        string outputDirectory,
        UnpackOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new UnpackOptions();
        try
        {
            var image = await ParseAsync(filePath, cancellationToken).ConfigureAwait(false);
            await VerifyImageAsync(filePath, image, verifyPartitionHashes: true, cancellationToken).ConfigureAwait(false);

            var selected = options.CreateSelectedEntrySet();
            var payloadItems = image.Items.Where(item => !item.IsVerify &&
                (selected is null || selected.Contains(GetEntryName(item)))).ToArray();
            if (selected is not null)
            {
                var missing = selected.Except(payloadItems.Select(GetEntryName), StringComparer.OrdinalIgnoreCase).ToArray();
                if (missing.Length > 0)
                {
                    throw new InvalidDataException($"AMLogic entries were not found: {string.Join(", ", missing)}.");
                }
            }

            var outputs = payloadItems.Select(item =>
            {
                if (item.Size > options.MaximumOutputBytes)
                {
                    throw new InvalidDataException($"AMLogic item '{GetEntryName(item)}' exceeds the output budget.");
                }

                return (Item: item, Path: FileExtraction.ResolvePath(outputDirectory, GetEntryName(item)));
            }).ToArray();

            if (!options.Overwrite && outputs.Any(output => File.Exists(output.Path)))
            {
                throw new IOException("One or more AMLogic output files already exist and overwrite is disabled.");
            }

            await using var input = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            foreach (var output in outputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await FileExtraction.WriteAtomicAsync(output.Path, async target =>
                {
                    input.Position = output.Item.Offset;
                    await CopyExactlyAsync(input, target, output.Item.Size, hash: null, cancellationToken).ConfigureAwait(false);
                }, options.Overwrite, cancellationToken).ConfigureAwait(false);
            }

            return FirmwareOpResult.Ok(
                $"Extracted {outputs.Length} AMLogic items after CRC and VERIFY validation.",
                entries: [.. outputs.Select(output => GetEntryName(output.Item))]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return FirmwareOpResult.Fail(ex.Message);
        }
    }

    /// <summary>Creates an AMLogic image from a directory using the default v2 pack options.
    /// <para>使用默认的 v2 打包选项，从目录创建 AMLogic 镜像。</para></summary>
    /// <param name="inputPath">Directory whose files become image items. <para>其内文件将成为镜像条目的目录。</para></param>
    /// <param name="outputPath">Image file to write; an existing file is replaced atomically. <para>要写出的镜像文件；已有文件会被原子替换。</para></param>
    /// <param name="cancellationToken">Cancellation token forwarded to the options overload. <para>传递给带选项重载的取消令牌。</para></param>
    /// <returns>The pack result of the options overload invoked with a fresh <see cref="AmlogicPackOptions"/>.
    /// <para>即以全新 <see cref="AmlogicPackOptions"/> 调用带选项重载的打包结果。</para></returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is signaled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
        => PackAsync(inputPath, outputPath, new AmlogicPackOptions(), cancellationToken);

    /// <summary>Creates an AMLogic image from a directory of <c>sub.main</c>-named files, emitting VERIFY records on request.
    /// <para>从包含 <c>sub.main</c> 命名文件的目录创建 AMLogic 镜像，并按需写入 VERIFY 记录。</para></summary>
    /// <param name="inputPath">Directory whose top-level files become items, ordered by ordinal name; <c>image.cfg</c> is excluded and every other file must carry a <c>sub.main</c> name. <para>其顶层文件将成为条目的目录，按序数名称排序；排除 <c>image.cfg</c>，其余每个文件都必须使用 <c>sub.main</c> 命名。</para></param>
    /// <param name="outputPath">Image file to write; the image is published atomically and, when <paramref name="options"/> asks for it, re-verified before the call reports success. <para>要写出的镜像文件；镜像以原子方式发布，且当 <paramref name="options"/> 要求时会在成功返回前重新校验。</para></param>
    /// <param name="options">Version, alignment, verified-entry and output-verification choices; must not be null. <para>版本、对齐、VERIFY 条目与输出校验选择；不能为 null。</para></param>
    /// <param name="cancellationToken">Cancels planning and copying; cancellation is rethrown, never converted to a failure result. <para>取消规划与复制；取消异常会重新抛出，绝不转换为失败结果。</para></param>
    /// <returns>On success, the packed item names with a version and alignment summary; on an invalid input directory, missing verified entry, size or count limit violation, or I/O error, a failure result carrying the exception message.
    /// <para>成功时返回打包条目名以及版本与对齐摘要；输入目录无效、VERIFY 条目缺失、大小或数量越限、I/O 错误时返回携带异常信息的失败结果。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.
    /// <para><paramref name="options"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is signaled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    public async Task<FirmwareOpResult> PackAsync(
        string inputPath,
        string outputPath,
        AmlogicPackOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Directory.Exists(inputPath))
        {
            return FirmwareOpResult.Fail("AMLogic pack input must be a directory.");
        }

        try
        {
            ValidatePackOptions(options);
            HashSet<string> verified = options.VerifiedEntries is null
                ? []
                : new(options.VerifiedEntries, StringComparer.OrdinalIgnoreCase);
            var sources = Directory.EnumerateFiles(inputPath, "*", SearchOption.TopDirectoryOnly)
                .Where(path => !Path.GetFileName(path).Equals("image.cfg", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal)
                .Select(path => CreateFileSource(path, verified.Contains(Path.GetFileName(path))))
                .ToArray();
            if (sources.Length == 0)
            {
                throw new InvalidDataException("AMLogic pack input contains no items.");
            }

            var unknownVerified = verified.Except(sources.Select(GetEntryName), StringComparer.OrdinalIgnoreCase).ToArray();
            if (unknownVerified.Length > 0)
            {
                throw new InvalidDataException($"AMLogic verified entries were not found: {string.Join(", ", unknownVerified)}.");
            }

            await WriteImageAsync(sources, outputPath, options, cancellationToken).ConfigureAwait(false);
            if (options.VerifyOutput)
            {
                var verification = await VerifyAsync(outputPath, verifyPartitionHashes: true, cancellationToken).ConfigureAwait(false);
                if (!verification.Success)
                {
                    throw new InvalidDataException(verification.Summary);
                }
            }

            return FirmwareOpResult.Ok(
                $"Packed {sources.Length} AMLogic items as v{(uint)options.Version} with {options.Alignment}-byte alignment.",
                entries: [.. sources.Select(GetEntryName)]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return FirmwareOpResult.Fail(ex.Message);
        }
    }

    /// <summary>Rewrites a verified AMLogic image under the target version, alignment and verified-entry choices.
    /// <para>按目标版本、对齐与 VERIFY 条目选择，重新写出一个已通过校验的 AMLogic 镜像。</para></summary>
    /// <param name="inputPath">Source image, fully parsed, CRC-32 checked and SHA-1 verified before anything is written. <para>源镜像；写出任何内容之前会完整解析并做 CRC-32 与 SHA-1 校验。</para></param>
    /// <param name="outputPath">Destination image written atomically; VERIFY records are regenerated for payload items whose sub type is covered in the source. <para>原子写出的目标镜像；源中已被 VERIFY 记录覆盖的子类型会重新生成 VERIFY 记录。</para></param>
    /// <param name="options">Target version and alignment; <c>Version</c> must be v1 or v2 and <c>Alignment</c> must be 4 or 8. <c>VerifiedEntries</c> and <c>VerifyOutput</c> are not consulted here — the regenerated VERIFY records follow the source image's own VERIFY items. <para>目标版本与对齐；<c>Version</c> 只能为 v1 或 v2，<c>Alignment</c> 只能为 4 或 8。<c>VerifiedEntries</c> 与 <c>VerifyOutput</c> 在此不参与——重新生成的 VERIFY 记录跟随源镜像自身的 VERIFY 条目。</para></param>
    /// <param name="cancellationToken">Cancels parsing, verification and writing; cancellation is rethrown, never converted to a failure result. <para>取消解析、校验与写出；取消异常会重新抛出，绝不转换为失败结果。</para></param>
    /// <returns>On success, the converted item names with source and target version summary; on any validation or I/O error, a failure result carrying the exception message.
    /// <para>成功时返回转换后的条目名及源/目标版本摘要；校验或 I/O 出错时返回携带异常信息的失败结果。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is null.
    /// <para><paramref name="options"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is signaled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    public async Task<FirmwareOpResult> ConvertAsync(
        string inputPath,
        string outputPath,
        AmlogicPackOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            ValidatePackOptions(options);
            var image = await ParseAsync(inputPath, cancellationToken).ConfigureAwait(false);
            await VerifyImageAsync(inputPath, image, verifyPartitionHashes: true, cancellationToken).ConfigureAwait(false);
            var verifiedSubTypes = image.Items.Where(item => item.IsVerify).Select(item => item.SubType)
                .ToHashSet(StringComparer.Ordinal);
            var sources = image.Items.Where(item => !item.IsVerify)
                .Select(item => new AmlogicSource(inputPath, item.Offset, item.Size, item.MainType, item.SubType,
                    verifiedSubTypes.Contains(item.SubType)))
                .ToArray();
            await WriteImageAsync(sources, outputPath, options, cancellationToken).ConfigureAwait(false);
            return FirmwareOpResult.Ok(
                $"Converted AMLogic v{(uint)image.Version} to v{(uint)options.Version} with {options.Alignment}-byte alignment.",
                entries: [.. sources.Select(GetEntryName)]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return FirmwareOpResult.Fail(ex.Message);
        }
    }

    /// <summary>Runs the full image verification, including the per-item SHA-1 records.
    /// <para>执行完整镜像校验，包含每条目 SHA-1 记录。</para></summary>
    /// <param name="filePath">The AMLogic image to verify. <para>要校验的 AMLogic 镜像。</para></param>
    /// <param name="cancellationToken">Cancellation token forwarded to the two-flag overload. <para>传递给带开关重载的取消令牌。</para></param>
    /// <returns>The result of <c>VerifyAsync(filePath, verifyPartitionHashes: true, …)</c>. <para>即 <c>VerifyAsync(filePath, verifyPartitionHashes: true, …)</c> 的结果。</para></returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is signaled. <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    public Task<FirmwareOpResult> VerifyAsync(string filePath, CancellationToken cancellationToken = default)
        => VerifyAsync(filePath, verifyPartitionHashes: true, cancellationToken);

    /// <summary>Validates the AMLogic container geometry, CRC-32 and, on request, the VERIFY SHA-1 digests.
    /// <para>校验 AMLogic 容器的几何结构、CRC-32，并按要求校验 VERIFY SHA-1 摘要。</para></summary>
    /// <param name="filePath">The AMLogic image to verify. <para>要校验的 AMLogic 镜像。</para></param>
    /// <param name="verifyPartitionHashes">When <see langword="true"/>, each 48-byte VERIFY item must identify exactly one payload item and its <c>sha1sum</c> digest must match the payload bytes. <para>为 <see langword="true"/> 时，每个 48 字节的 VERIFY 条目必须恰好对应一个负载条目，且其 <c>sha1sum</c> 摘要必须与负载字节匹配。</para></param>
    /// <param name="cancellationToken">Cancels between and during table, CRC and hash passes; cancellation is rethrown, never converted to a failure result. <para>在表、CRC 与哈希各遍之间及期间取消；取消异常会重新抛出，绝不转换为失败结果。</para></param>
    /// <returns>On success, a summary naming the version and item count; on a size, magic, alignment, overlap, bounds, CRC or SHA-1 mismatch, a failure result carrying the exception message.
    /// <para>成功时返回注明版本与条目数的摘要；长度、魔数、对齐、重叠、边界、CRC 或 SHA-1 不匹配时返回携带异常信息的失败结果。</para></returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is signaled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    public async Task<FirmwareOpResult> VerifyAsync(
        string filePath,
        bool verifyPartitionHashes,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var image = await ParseAsync(filePath, cancellationToken).ConfigureAwait(false);
            await VerifyImageAsync(filePath, image, verifyPartitionHashes, cancellationToken).ConfigureAwait(false);
            var hashSummary = verifyPartitionHashes ? " and partition SHA-1 records" : string.Empty;
            return FirmwareOpResult.Ok($"AMLogic v{(uint)image.Version} CRC{hashSummary} verified for {image.Items.Count} items.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return FirmwareOpResult.Fail(ex.Message);
        }
    }

    private static async Task<AmlImage> ParseAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
        if (stream.Length < 12)
        {
            throw new InvalidDataException("AMLogic header is truncated.");
        }

        var prefix = new byte[12];
        await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(8)) != Magic)
        {
            throw new InvalidDataException("AMLogic magic is invalid.");
        }

        var versionValue = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(4));
        if (versionValue is not ((uint)AmlogicImageVersion.V1) and not ((uint)AmlogicImageVersion.V2))
        {
            throw new InvalidDataException($"Unsupported AMLogic image version: {versionValue}.");
        }

        var version = (AmlogicImageVersion)versionValue;
        var headerSize = GetHeaderSize(version);
        var itemSize = GetItemRecordSize(version);
        var header = new byte[headerSize];
        prefix.CopyTo(header, 0);
        await stream.ReadExactlyAsync(header.AsMemory(prefix.Length), cancellationToken).ConfigureAwait(false);
        var alignment = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20));
        var count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24));
        if (alignment is not (4 or 8))
        {
            throw new InvalidDataException($"Unsupported AMLogic item alignment: {alignment}.");
        }
        if (count == 0 || count > MaximumItems)
        {
            throw new InvalidDataException("AMLogic item count is outside the limit.");
        }

        var tableLength = checked((long)count * itemSize);
        FileExtraction.ValidateRange(headerSize, tableLength, stream.Length, "AMLogic item table");
        var items = new List<AmlItem>(checked((int)count));
        var record = new byte[itemSize];
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await stream.ReadExactlyAsync(record, cancellationToken).ConfigureAwait(false);
            var offset = version == AmlogicImageVersion.V1
                ? BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(16))
                : BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(8));
            var size = version == AmlogicImageVersion.V1
                ? BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(24))
                : BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(16));
            var nameOffset = version == AmlogicImageVersion.V1 ? 32 : 24;
            var nameSize = version == AmlogicImageVersion.V1 ? 32 : 256;
            var verifyOffset = version == AmlogicImageVersion.V1 ? 96 : 536;
            var main = ReadName(record.AsSpan(nameOffset, nameSize));
            var sub = ReadName(record.AsSpan(nameOffset + nameSize, nameSize));
            var verify = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(verifyOffset));
            items.Add(new AmlItem(index, checked((long)offset), checked((long)size), main, sub,
                verify != 0 || main.Equals("VERIFY", StringComparison.Ordinal)));
        }

        return new AmlImage(
            version,
            BinaryPrimitives.ReadUInt32LittleEndian(header),
            BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(12)),
            alignment,
            headerSize,
            itemSize,
            items,
            stream.Length);
    }

    private static async Task VerifyImageAsync(
        string path,
        AmlImage image,
        bool verifyPartitionHashes,
        CancellationToken cancellationToken)
    {
        if (image.DeclaredSize != checked((ulong)image.Length))
        {
            throw new InvalidDataException($"AMLogic image size declares {image.DeclaredSize}, actual length is {image.Length}.");
        }

        var tableEnd = checked(image.HeaderSize + (long)image.Items.Count * image.ItemRecordSize);
        foreach (var item in image.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Offset < tableEnd || item.Offset % image.Alignment != 0)
            {
                throw new InvalidDataException($"AMLogic item '{GetEntryName(item)}' is misaligned or overlaps metadata.");
            }
            if (item.Size > MaximumItemBytes)
            {
                throw new InvalidDataException($"AMLogic item '{GetEntryName(item)}' exceeds the size limit.");
            }
            FileExtraction.ValidateRange(item.Offset, item.Size, image.Length, $"AMLogic item '{GetEntryName(item)}'");
        }

        var ranges = image.Items.OrderBy(item => item.Offset).ThenBy(item => item.Size).ToArray();
        for (var index = 1; index < ranges.Length; index++)
        {
            var previous = ranges[index - 1];
            var current = ranges[index];
            var exactDuplicate = previous.Offset == current.Offset && previous.Size == current.Size;
            if (!exactDuplicate && current.Offset < checked(previous.Offset + previous.Size))
            {
                throw new InvalidDataException($"AMLogic items '{GetEntryName(previous)}' and '{GetEntryName(current)}' overlap.");
            }
        }

        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var computedCrc = await ComputeCrcAsync(input, image.Alignment, cancellationToken).ConfigureAwait(false);
        if (computedCrc != image.Crc32)
        {
            throw new InvalidDataException($"AMLogic CRC mismatch: stored 0x{image.Crc32:X8}, computed 0x{computedCrc:X8}.");
        }

        if (!verifyPartitionHashes)
        {
            return;
        }

        var payloadBySubType = image.Items.Where(item => !item.IsVerify)
            .GroupBy(item => item.SubType, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        foreach (var verifyItem in image.Items.Where(item => item.IsVerify))
        {
            if (verifyItem.Size != VerifyDataSize)
            {
                throw new InvalidDataException($"AMLogic VERIFY item '{verifyItem.SubType}' must contain exactly {VerifyDataSize} bytes.");
            }
            if (!payloadBySubType.TryGetValue(verifyItem.SubType, out var matches) || matches.Length != 1)
            {
                throw new InvalidDataException($"AMLogic VERIFY item '{verifyItem.SubType}' does not identify exactly one payload item.");
            }

            input.Position = verifyItem.Offset;
            var verifyData = new byte[VerifyDataSize];
            await input.ReadExactlyAsync(verifyData, cancellationToken).ConfigureAwait(false);
            var expected = ParseVerifyDigest(verifyData, verifyItem.SubType);
            input.Position = matches[0].Offset;
            using var sha1 = Sha1.CreateAccumulator();
            await CopyExactlyAsync(input, Stream.Null, matches[0].Size, sha1, cancellationToken).ConfigureAwait(false);
            var actual = sha1.GetHashAndReset();
            if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            {
                throw new InvalidDataException($"AMLogic SHA-1 mismatch for '{GetEntryName(matches[0])}'.");
            }
        }
    }

    private static async Task WriteImageAsync(
        IReadOnlyList<AmlogicSource> sources,
        string outputPath,
        AmlogicPackOptions options,
        CancellationToken cancellationToken)
    {
        if (sources.Count == 0 || sources.Count > MaximumItems)
        {
            throw new InvalidDataException("AMLogic input item count is outside the limit.");
        }

        var itemCount = checked(sources.Count + sources.Count(source => source.Verify));
        if (itemCount > MaximumItems)
        {
            throw new InvalidDataException("AMLogic input item count is outside the limit after VERIFY records are added.");
        }

        var headerSize = GetHeaderSize(options.Version);
        var recordSize = GetItemRecordSize(options.Version);
        var position = Align(checked(headerSize + (long)itemCount * recordSize), options.Alignment);
        var planned = new List<PlannedItem>(itemCount);
        foreach (var source in sources)
        {
            ValidateSource(source, options.Version);
            position = Align(position, options.Alignment);
            planned.Add(new PlannedItem(source, position, source.Size, false));
            position = checked(position + source.Size);
            if (source.Verify)
            {
                position = Align(position, options.Alignment);
                planned.Add(new PlannedItem(source, position, VerifyDataSize, true));
                position = checked(position + VerifyDataSize);
            }
        }

        var imageLength = position;
        await FileExtraction.WriteAtomicAsync(outputPath, async output =>
        {
            output.SetLength(imageLength);
            var header = new byte[headerSize];
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)options.Version);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), Magic);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(12), checked((ulong)imageLength));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), options.Alignment);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), checked((uint)itemCount));
            output.Position = 0;
            await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);

            for (var index = 0; index < planned.Count; index++)
            {
                var record = CreateItemRecord(options.Version, planned[index], index);
                await output.WriteAsync(record, cancellationToken).ConfigureAwait(false);
            }

            var hashes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var item in planned)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.Position = item.Offset;
                if (item.IsVerify)
                {
                    var digest = hashes[item.Source.SubType];
                    var text = Encoding.ASCII.GetBytes($"sha1sum {Convert.ToHexStringLower(digest)}");
                    await output.WriteAsync(text, cancellationToken).ConfigureAwait(false);
                    await output.WriteAsync(new byte[VerifyDataSize - text.Length], cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await using var input = new FileStream(item.Source.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                input.Position = item.Source.SourceOffset;
                using var sha1 = item.Source.Verify ? Sha1.CreateAccumulator() : null;
                await CopyExactlyAsync(input, output, item.Size, sha1, cancellationToken).ConfigureAwait(false);
                if (sha1 is not null)
                {
                    hashes[item.Source.SubType] = sha1.GetHashAndReset();
                }
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            var crc = await ComputeCrcAsync(output, options.Alignment, cancellationToken).ConfigureAwait(false);
            output.Position = 0;
            var crcBytes = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(crcBytes, crc);
            await output.WriteAsync(crcBytes, cancellationToken).ConfigureAwait(false);
        }, overwrite: true, cancellationToken).ConfigureAwait(false);
    }

    private static byte[] CreateItemRecord(AmlogicImageVersion version, PlannedItem item, int id)
    {
        var record = new byte[GetItemRecordSize(version)];
        BinaryPrimitives.WriteUInt32LittleEndian(record, checked((uint)id));
        if (version == AmlogicImageVersion.V1)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(16), checked((ulong)item.Offset));
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(24), checked((ulong)item.Size));
            WriteName(record.AsSpan(32, 32), item.IsVerify ? "VERIFY" : item.Source.MainType);
            WriteName(record.AsSpan(64, 32), item.Source.SubType);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(96), item.IsVerify ? 1U : 0U);
        }
        else
        {
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8), checked((ulong)item.Offset));
            BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(16), checked((ulong)item.Size));
            WriteName(record.AsSpan(24, 256), item.IsVerify ? "VERIFY" : item.Source.MainType);
            WriteName(record.AsSpan(280, 256), item.Source.SubType);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(536), item.IsVerify ? 1U : 0U);
        }
        return record;
    }

    private static AmlogicSource CreateFileSource(string path, bool verify)
    {
        var name = Path.GetFileName(path);
        var separator = name.LastIndexOf('.');
        if (separator <= 0 || separator == name.Length - 1)
        {
            throw new InvalidDataException($"AMLogic item '{name}' must use sub.main naming.");
        }
        return new AmlogicSource(path, 0, new FileInfo(path).Length, name[(separator + 1)..], name[..separator], verify);
    }

    private static void ValidateSource(AmlogicSource source, AmlogicImageVersion version)
    {
        if (source.Size < 0 || source.Size > MaximumItemBytes)
        {
            throw new InvalidDataException($"AMLogic item '{GetEntryName(source)}' exceeds the size limit.");
        }
        var capacity = version == AmlogicImageVersion.V1 ? 32 : 256;
        ValidateName(source.MainType, capacity, "main type");
        ValidateName(source.SubType, capacity, "sub type");
    }

    private static readonly SearchValues<char> ForbiddenNameChars = SearchValues.Create(['\0', '/', '\\']);

    private static void ValidateName(string value, int capacity, string description)
    {
        if (string.IsNullOrWhiteSpace(value) || value.AsSpan().ContainsAny(ForbiddenNameChars) || Encoding.UTF8.GetByteCount(value) >= capacity)
        {
            throw new InvalidDataException($"AMLogic {description} '{value}' is invalid for the selected version.");
        }
    }

    private static void ValidatePackOptions(AmlogicPackOptions options)
    {
        if (options.Version is not AmlogicImageVersion.V1 and not AmlogicImageVersion.V2)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "AMLogic version must be v1 or v2.");
        }
        if (options.Alignment is not (4 or 8))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "AMLogic alignment must be 4 or 8 bytes.");
        }
    }

    private static byte[] ParseVerifyDigest(ReadOnlySpan<byte> data, string subType)
    {
        var terminator = data.IndexOf((byte)0);
        var text = Encoding.ASCII.GetString(terminator >= 0 ? data[..terminator] : data).TrimEnd();
        const string prefix = "sha1sum ";
        if (!text.StartsWith(prefix, StringComparison.Ordinal) || text.Length != prefix.Length + 40)
        {
            throw new InvalidDataException($"AMLogic VERIFY item '{subType}' has an invalid SHA-1 record.");
        }
        try
        {
            return Convert.FromHexString(text[prefix.Length..]);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException($"AMLogic VERIFY item '{subType}' has an invalid SHA-1 digest.", ex);
        }
    }

    private static async Task<uint> ComputeCrcAsync(Stream stream, long offset, CancellationToken cancellationToken)
    {
        stream.Position = offset;
        var buffer = new byte[128 * 1024];
        var crc = uint.MaxValue;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            crc = Crc32Register.Compute(buffer.AsSpan(0, read), crc);
        }
        return crc;
    }

    private static async Task CopyExactlyAsync(
        Stream input,
        Stream output,
        long length,
        DigestAccumulator? hash,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        while (length > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length)), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("AMLogic item data is truncated.");
            }
            hash?.Append(buffer.AsSpan(0, read));
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            length -= read;
        }
    }

    private static int GetHeaderSize(AmlogicImageVersion version) => version == AmlogicImageVersion.V1 ? 64 : 72;
    private static int GetItemRecordSize(AmlogicImageVersion version) => version == AmlogicImageVersion.V1 ? 128 : 576;
    private static long Align(long value, uint alignment) => checked((value + alignment - 1) / alignment * alignment);
    private static string GetEntryName(AmlItem item) => $"{item.SubType}.{item.MainType}";
    private static string GetEntryName(AmlogicSource source) => $"{source.SubType}.{source.MainType}";

    private static string ReadName(ReadOnlySpan<byte> bytes)
    {
        var terminator = bytes.IndexOf((byte)0);
        var value = Encoding.UTF8.GetString(terminator >= 0 ? bytes[..terminator] : bytes);
        ValidateName(value, bytes.Length, "type name");
        return value;
    }

    private static void WriteName(Span<byte> destination, string value)
    {
        destination.Clear();
        var count = Encoding.UTF8.GetBytes(value, destination);
        if (count >= destination.Length)
        {
            throw new InvalidDataException($"AMLogic type name '{value}' is too long.");
        }
    }

    private sealed record AmlImage(
        AmlogicImageVersion Version,
        uint Crc32,
        ulong DeclaredSize,
        uint Alignment,
        int HeaderSize,
        int ItemRecordSize,
        List<AmlItem> Items,
        long Length);

    private sealed record AmlItem(int Id, long Offset, long Size, string MainType, string SubType, bool IsVerify);
    private sealed record AmlogicSource(string Path, long SourceOffset, long Size, string MainType, string SubType, bool Verify);
    private sealed record PlannedItem(AmlogicSource Source, long Offset, long Size, bool IsVerify);
}