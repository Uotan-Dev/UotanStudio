using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using UotanStudio.Firmware.Core.IO;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Firmware.AndroidMeta.Apk.Signing;

/// <summary>
/// Orchestrates APK signing with user-provided key material: strips stale signatures,
/// writes v1 JAR artifacts, computes the chunked content digest and inserts the APK
/// Signing Block carrying v2/v3 signatures, patching the EOCD central-directory offset.
/// <para>编排使用用户提供密钥材料的 APK 签名流程：剥离旧签名、写入 v1 JAR 工件、计算分块内容摘要、
/// 插入携带 v2/v3 签名的 APK 签名分块并修正 EOCD 中央目录偏移。</para>
/// </summary>
public static class ApkSigner
{
    private const int CopyBufferBytes = 81920;
    private const int MaximumHashWorkers = 8;

    /// <summary>Signs an APK file with the given custom key material and writes the result to the output path (atomic replace). The input file is never modified.
    /// <para>使用给定的自定义密钥材料签署 APK 文件并把结果写入输出路径（原子替换）。不修改输入文件。</para></summary>
    /// <param name="inputApk">Path of an unsigned or already-signed APK; the file itself is never modified.
    /// <para>已存在（未签名或已签过名）的 APK 路径；该文件本身不会被修改。</para></param>
    /// <param name="outputApk">Destination path, written atomically via a temporary file and overwrite; parent directories are created as needed.
    /// <para>目标路径，经临时文件与覆盖写入原子落盘，并按需创建父目录。</para></param>
    /// <param name="keyMaterial">Signing key and certificate chain supplied by the user.
    /// <para>用户提供的签名私钥与证书链。</para></param>
    /// <param name="options">Scheme selection, v3 SDK range, JAR signer name and stripping policy; <see langword="null"/> uses all defaults (v1+v2+v3 enabled, stripping on).
    /// <para>方案开关、v3 SDK 范围、JAR 签名名称与剥离策略； 为 <see langword="null"/> 时全部采用默认值（启用 v1+v2+v3 并剥离旧签名）。</para></param>
    /// <param name="cancellationToken">Cancels between entry and section copies; a cancelled run deletes the temporary output.
    /// <para>在条目与区段复制之间取消；取消时会删除临时输出文件。</para></param>
    /// <exception cref="FileNotFoundException"><paramref name="inputApk"/> does not exist.
    /// <para><paramref name="inputApk"/> 不存在。</para></exception>
    /// <exception cref="ArgumentException">The output path is blank or equals the input, no scheme is enabled, the JAR signer name breaks the 1-8 ASCII/<c>_</c>/<c>-</c> rule, or the v3 SDK range is below 28 or inverted.
    /// <para>输出路径为空或与输入相同、未启用任何方案、JAR 签名名称不满足 1-8 位 ASCII 字母/数字/<c>_</c>/<c>-</c> 规则，或 v3 SDK 范围小于 28、上界小于下界。</para></exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.
    /// <para>令牌被取消。</para></exception>
    /// <example>
    /// <code>
    /// ApkSigner.Sign("app-unsigned.apk", "app-signed.apk", keyMaterial);
    /// </code>
    /// </example>
    public static void Sign(
        string inputApk,
        string outputApk,
        ApkSignerKeyMaterial keyMaterial,
        ApkSigningOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(inputApk))
        {
            throw new FileNotFoundException($"APK file not found: {inputApk}. 未找到 APK 文件：{inputApk}", inputApk);
        }

        if (string.IsNullOrWhiteSpace(outputApk))
        {
            throw new ArgumentException("Output path must be provided. 必须提供输出路径。", nameof(outputApk));
        }

        var resolvedOptions = options ?? new ApkSigningOptions();
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputApk));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (!resolvedOptions.EnableV1 && !resolvedOptions.EnableV2 && !resolvedOptions.EnableV3)
            throw new ArgumentException("Enable at least one APK signature scheme. 至少启用一种 APK 签名方案。", nameof(options));
        if (resolvedOptions.EnableV1 && (resolvedOptions.SignerName.Length is < 1 or > 8
            || resolvedOptions.SignerName.Any(static c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-')))
            throw new ArgumentException("JAR signer name must contain 1-8 ASCII letters, digits, '-' or '_'. JAR 签名名称无效。", nameof(options));
        if (resolvedOptions.V3MinSdk < 28 || resolvedOptions.V3MaxSdk < resolvedOptions.V3MinSdk)
            throw new ArgumentException("Invalid v3 SDK range. v3 SDK 范围无效。", nameof(options));
        if (string.Equals(Path.GetFullPath(inputApk), Path.GetFullPath(outputApk),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Input and output APK paths must differ. 输入和输出 APK 路径必须不同。", nameof(outputApk));
        cancellationToken.ThrowIfCancellationRequested();
        var tempPath = Path.Combine(directory!, Path.GetRandomFileName());
        try
        {
            if (resolvedOptions.EnableV1)
            {
                WriteV1SignedApk(inputApk, tempPath, keyMaterial, resolvedOptions, cancellationToken);
            }
            else if (resolvedOptions.StripExistingSignatures)
            {
                WriteStrippedApkWithSigningBlock(inputApk, tempPath, keyMaterial, resolvedOptions, cancellationToken);
            }
            else
            {
                InsertSigningBlock(inputApk, tempPath, keyMaterial, resolvedOptions, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            AtomicPublish.Publish(tempPath, outputApk);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { }
            throw;
        }
    }

    private static void WriteV1SignedApk(
        string inputApk,
        string outputApk,
        ApkSignerKeyMaterial keyMaterial,
        ApkSigningOptions options,
        CancellationToken cancellationToken)
    {
        var protectedSchemes = new List<int>();
        if (options.EnableV2)
        {
            protectedSchemes.Add(2);
        }

        if (options.EnableV3)
        {
            protectedSchemes.Add(3);
        }

        var entries = CollectEntryDigests(inputApk, options, cancellationToken);
        var (manifest, signatureFile) = ApkV1Signer.BuildManifestAndSignatureFile(entries, protectedSchemes);
        var signatureBlock = ApkV1Signer.SignSignatureFile(signatureFile, keyMaterial);
        var extension = ApkV1Signer.SignatureBlockExtension(keyMaterial.Key);
        var signerName = string.IsNullOrWhiteSpace(options.SignerName)
            ? ApkV1Signer.DefaultSignerName
            : options.SignerName;

        ApkEntryStripper.RewriteRequest request = new()
        {
            StripV1SignatureEntries = true,
            EntriesToAppend =
            [
                ("META-INF/MANIFEST.MF", manifest),
                ($"META-INF/{signerName}.SF", signatureFile),
                ($"META-INF/{signerName}{extension}", signatureBlock),
            ],
        };

        if (options.EnableV2 || options.EnableV3)
        {
            var algorithm = ApkSignatureAlgorithmSelector.Select(keyMaterial.Key, options.MinSdkVersion);
            ApkEntryStripper.RewriteWithSigningBlock(
                inputApk,
                outputApk,
                request,
                ApkSignatureConstants.HashNameFor(algorithm),
                digest => BuildSigningBlock(keyMaterial, options, algorithm, digest),
                cancellationToken);
        }
        else
        {
            ApkEntryStripper.Rewrite(inputApk, outputApk, request, cancellationToken);
        }
    }

    private static void WriteStrippedApkWithSigningBlock(
        string inputApk,
        string outputApk,
        ApkSignerKeyMaterial keyMaterial,
        ApkSigningOptions options,
        CancellationToken cancellationToken)
    {
        var algorithm = ApkSignatureAlgorithmSelector.Select(keyMaterial.Key, options.MinSdkVersion);
        ApkEntryStripper.RewriteWithSigningBlock(
            inputApk,
            outputApk,
            new ApkEntryStripper.RewriteRequest { StripV1SignatureEntries = true },
            ApkSignatureConstants.HashNameFor(algorithm),
            digest => BuildSigningBlock(keyMaterial, options, algorithm, digest),
            cancellationToken);
    }

    private static byte[] BuildSigningBlock(
        ApkSignerKeyMaterial keyMaterial,
        ApkSigningOptions options,
        ApkSignatureConstants.SignatureAlgorithm algorithm,
        byte[] contentDigest)
    {
        return ApkSigningBlock.Build(BuildSignaturePairs(keyMaterial, options, algorithm, contentDigest));
    }

    private static List<(string Name, byte[] Sha256Digest)> CollectEntryDigests(
        string apkPath,
        ApkSigningOptions options,
        CancellationToken cancellationToken)
    {
        var descriptors = new List<EntryDescriptor>();
        long totalUncompressed = 0;
        long manifestBudget = 0;
        using (var stream = new FileStream(apkPath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferBytes, FileOptions.SequentialScan))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false))
        {
            var archiveIndex = 0;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/'))
                {
                    archiveIndex++;
                    continue;
                }

                if (entry.FullName.Contains('\r') || entry.FullName.Contains('\n') || entry.FullName.Contains('\0'))
                    throw new InvalidDataException("Invalid JAR entry name. JAR 条目名称无效。");
                if (options.StripExistingSignatures && ApkEntryStripper.IsV1SignatureEntry(entry.FullName))
                {
                    archiveIndex++;
                    continue;
                }

                totalUncompressed = checked(totalUncompressed + entry.Length);
                manifestBudget = checked(manifestBudget + System.Text.Encoding.UTF8.GetByteCount(entry.FullName) * 2L + 256);
                if (totalUncompressed > 64L * 1024 * 1024 * 1024 || manifestBudget > 32 * 1024 * 1024)
                    throw new InvalidDataException("APK signing input exceeds the decompression or manifest budget. APK 签名输入超过解压或清单预算。");
                descriptors.Add(new EntryDescriptor(archiveIndex, entry.FullName, entry.Length));
                archiveIndex++;
            }
        }

        var digests = HashEntries(apkPath, descriptors, cancellationToken);
        var entries = new List<(string Name, byte[] Sha256Digest)>(descriptors.Count);
        for (var i = 0; i < descriptors.Count; i++)
        {
            entries.Add((descriptors[i].Name, digests[i]));
        }

        entries.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return entries;
    }

    private static byte[][] HashEntries(
        string apkPath,
        IReadOnlyList<EntryDescriptor> descriptors,
        CancellationToken cancellationToken)
    {
        var digests = new byte[descriptors.Count][];
        if (descriptors.Count == 0)
        {
            return digests;
        }

        var workers = Math.Min(Environment.ProcessorCount, MaximumHashWorkers);
        if (workers <= 1 || descriptors.Count == 1)
        {
            using var stream = new FileStream(apkPath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferBytes, FileOptions.SequentialScan);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            HashEntryRange(archive, descriptors, 0, descriptors.Count, digests, cancellationToken);
            return digests;
        }

        try
        {
            Parallel.For(0, workers, new ParallelOptions
            {
                MaxDegreeOfParallelism = workers,
                CancellationToken = cancellationToken,
            }, worker =>
            {
                var start = (int)((long)worker * descriptors.Count / workers);
                var end = (int)((long)(worker + 1) * descriptors.Count / workers);
                if (start >= end)
                {
                    return;
                }

                using var stream = new FileStream(apkPath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferBytes, FileOptions.SequentialScan);
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
                HashEntryRange(archive, descriptors, start, end, digests, cancellationToken);
            });
        }
        catch (AggregateException exception)
        {
            ExceptionDispatchInfo.Capture(exception.Flatten().InnerExceptions[0]).Throw();
            throw;
        }

        return digests;
    }

    private static void HashEntryRange(
        ZipArchive archive,
        IReadOnlyList<EntryDescriptor> descriptors,
        int start,
        int end,
        byte[][] digests,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        using var hash = Sha256.CreateAccumulator();
        try
        {
            for (var i = start; i < end; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var descriptor = descriptors[i];
                var entry = archive.Entries[descriptor.ArchiveIndex];
                if (!string.Equals(entry.FullName, descriptor.Name, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("APK entries changed while signing. 签名期间 APK 条目发生变化。");
                }

                using var entryStream = entry.Open();
                var remaining = descriptor.Length;
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = entryStream.Read(buffer.AsSpan(0, (int)Math.Min(buffer.Length, remaining)));
                    if (read == 0) throw new EndOfStreamException("Truncated APK entry. APK 条目被截断。");
                    hash.Append(buffer.AsSpan(0, read));
                    remaining -= read;
                }
                if (entryStream.ReadByte() != -1) throw new InvalidDataException("APK entry exceeds its declared size. APK 条目超过声明长度。");

                var digest = new byte[hash.HashLengthInBytes];
                hash.GetHashAndReset(digest);
                digests[i] = digest;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void InsertSigningBlock(
        string inputApk,
        string outputApk,
        ApkSignerKeyMaterial keyMaterial,
        ApkSigningOptions options,
        CancellationToken cancellationToken)
    {
        using var input = new FileStream(inputApk, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferBytes, FileOptions.SequentialScan);
        var sections = ApkZipSections.Locate(input);

        var zipContentLength = sections.CentralDirectoryOffset;

        var eocd = new byte[sections.EocdSize];
        input.Seek(sections.EocdOffset, SeekOrigin.Begin);
        input.ReadExactly(eocd);

        var algorithm = ApkSignatureAlgorithmSelector.Select(keyMaterial.Key, options.MinSdkVersion);
        var hashName = ApkSignatureConstants.HashNameFor(algorithm);

        var contentDigest = ApkContentDigest.ComputeApkDigest(
            input, zipContentLength, sections.CentralDirectoryOffset, zipContentLength,
            sections.CentralDirectorySize, eocd, hashName, cancellationToken);

        var pairs = BuildSignaturePairs(keyMaterial, options, algorithm, contentDigest);
        var block = ApkSigningBlock.Build(pairs);
        var newCentralDirectoryOffset = checked((uint)(zipContentLength + block.Length));

        var patchedEocd = (byte[])eocd.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(patchedEocd.AsSpan(16, 4), newCentralDirectoryOffset);

        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        try
        {
            using var output = new FileStream(outputApk, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferBytes, FileOptions.SequentialScan);
            CopyRange(input, output, 0, zipContentLength, buffer, cancellationToken);
            output.Write(block);
            CopyRange(input, output, sections.CentralDirectoryOffset, sections.CentralDirectorySize, buffer, cancellationToken);
            output.Write(patchedEocd);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static List<(uint Id, byte[] Value)> BuildSignaturePairs(
        ApkSignerKeyMaterial keyMaterial,
        ApkSigningOptions options,
        ApkSignatureConstants.SignatureAlgorithm algorithm,
        byte[] contentDigest)
    {
        var pairs = new List<(uint Id, byte[] Value)>();
        if (options.EnableV2)
        {
            var signer = ApkV2V3SchemeSigner.BuildV2Signer(keyMaterial.Key, keyMaterial.CertificateChain, algorithm, contentDigest, options.EnableV3);
            pairs.Add((ApkSignatureConstants.V2BlockId, signer));
        }

        if (options.EnableV3)
        {
            var signer = ApkV2V3SchemeSigner.BuildV3Signer(
                keyMaterial.Key, keyMaterial.CertificateChain, algorithm, contentDigest,
                options.V3MinSdk, options.V3MaxSdk);
            pairs.Add((ApkSignatureConstants.V3BlockId, signer));
        }

        return pairs;
    }

    private static void CopyRange(Stream input, Stream output, long offset, long length, byte[] buffer, CancellationToken cancellationToken)
    {
        input.Seek(offset, SeekOrigin.Begin);
        var remaining = length;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = input.Read(buffer.AsSpan(0, (int)Math.Min(buffer.Length, remaining)));
            if (read == 0)
            {
                throw new EndOfStreamException("Unexpected EOF while copying APK sections. 复制 APK 区段时流意外结束。");
            }

            output.Write(buffer.AsSpan(0, read));
            remaining -= read;
        }
    }

    private readonly record struct EntryDescriptor(int ArchiveIndex, string Name, long Length);
}