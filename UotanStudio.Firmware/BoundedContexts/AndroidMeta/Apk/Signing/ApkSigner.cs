using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Apk.Signing;

/// <summary>
/// Orchestrates APK signing with user-provided key material: strips stale signatures,
/// writes v1 JAR artifacts, computes the chunked content digest and inserts the APK
/// Signing Block carrying v2/v3 signatures, patching the EOCD central-directory offset.
/// <para>编排使用用户提供密钥材料的 APK 签名流程：剥离旧签名、写入 v1 JAR 工件、计算分块内容摘要、
/// 插入携带 v2/v3 签名的 APK 签名分块并修正 EOCD 中央目录偏移。</para>
/// </summary>
public static class ApkSigner
{
    /// <summary>
    /// Signs an APK file with the given custom key material and writes the result to the
    /// output path (atomic replace). The input file is never modified.
    /// <para>使用给定的自定义密钥材料签署 APK 文件并把结果写入输出路径（原子替换）。不修改输入文件。</para>
    /// </summary>
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
            var working = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            try
            {
                // Step 1: strip stale signatures (v1 entries + existing signing block) when re-signing.
                // 第 1 步：重新签名时剥离旧签名（v1 条目 + 既有签名分块）。
                if (resolvedOptions.StripExistingSignatures)
                {
                    ApkEntryStripper.Rewrite(inputApk, working, new ApkEntryStripper.RewriteRequest
                    {
                        StripV1SignatureEntries = true,
                    }, cancellationToken);
                }
                else
                {
                    File.Copy(inputApk, working, overwrite: true);
                }

                // Step 2: v1 JAR artifacts (MANIFEST.MF, .SF, CMS), appended as STORED entries.
                // 第 2 步：v1 JAR 工件（MANIFEST.MF、.SF、CMS），以 STORED 条目追加。
                if (resolvedOptions.EnableV1)
                {
                    AppendV1Artifacts(working, keyMaterial, resolvedOptions, cancellationToken);
                }

                // Step 3: v2/v3 signing block over the final ZIP layout.
                // 第 3 步：对最终 ZIP 布局计算并插入 v2/v3 签名分块。
                if (resolvedOptions.EnableV2 || resolvedOptions.EnableV3)
                {
                    InsertSigningBlock(working, tempPath, keyMaterial, resolvedOptions, cancellationToken);
                }
                else
                {
                    File.Copy(working, tempPath, overwrite: true);
                }

                cancellationToken.ThrowIfCancellationRequested();
                File.Move(tempPath, outputApk, overwrite: true);
            }
            finally
            {
                try { File.Delete(working); } catch { /* best effort 尽力清理 */ }
            }
        }
        catch
        {
            try { File.Delete(tempPath); } catch { /* best effort 尽力清理 */ }
            throw;
        }
    }

    private static void AppendV1Artifacts(
        string apkPath,
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

        var entries = new List<(string Name, byte[] Content)>();
        long totalUncompressed = 0;
        long manifestBudget = 0;
        using (var stream = new FileStream(apkPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false))
        {
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/'))
                {
                    // Directory entries carry no digest in JAR manifests.
                    // 目录条目在 JAR 清单中不携带摘要。
                    continue;
                }

                if (entry.FullName.Contains('\r') || entry.FullName.Contains('\n') || entry.FullName.Contains('\0'))
                    throw new InvalidDataException("Invalid JAR entry name. JAR 条目名称无效。");
                totalUncompressed = checked(totalUncompressed + entry.Length);
                manifestBudget = checked(manifestBudget + System.Text.Encoding.UTF8.GetByteCount(entry.FullName) * 2L + 256);
                if (totalUncompressed > 64L * 1024 * 1024 * 1024 || manifestBudget > 32 * 1024 * 1024)
                    throw new InvalidDataException("APK signing input exceeds the decompression or manifest budget. APK 签名输入超过解压或清单预算。");
                using var entryStream = entry.Open();
                var digest = HashEntry(entryStream, entry.Length, cancellationToken);
                entries.Add((entry.FullName, digest));
            }
        }

        // JAR manifest entry order must match the archive order for deterministic output.
        // JAR 清单条目顺序与归档顺序一致，保证输出确定。
        entries.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));

        var manifest = ApkV1Signer.BuildManifest(entries);
        var signatureFile = ApkV1Signer.BuildSignatureFile(manifest, protectedSchemes);
        var signatureBlock = ApkV1Signer.SignSignatureFile(signatureFile, keyMaterial);
        var extension = ApkV1Signer.SignatureBlockExtension(keyMaterial.Key);
        var signerName = string.IsNullOrWhiteSpace(options.SignerName)
            ? ApkV1Signer.DefaultSignerName
            : options.SignerName;

        ApkEntryStripper.Rewrite(apkPath, apkPath + ".v1", new ApkEntryStripper.RewriteRequest
        {
            StripV1SignatureEntries = true,
            EntriesToAppend =
            [
                ("META-INF/MANIFEST.MF", manifest),
                ($"META-INF/{signerName}.SF", signatureFile),
                ($"META-INF/{signerName}{extension}", signatureBlock),
            ],
        }, cancellationToken);
        File.Move(apkPath + ".v1", apkPath, overwrite: true);
    }

    private static byte[] HashEntry(Stream input, long expectedLength, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long remaining = expectedLength;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = input.Read(buffer.AsSpan(0, (int)Math.Min(buffer.Length, remaining)));
            if (read == 0) throw new EndOfStreamException("Truncated APK entry. APK 条目被截断。");
            hash.AppendData(buffer.AsSpan(0, read));
            remaining -= read;
        }
        if (input.ReadByte() != -1) throw new InvalidDataException("APK entry exceeds its declared size. APK 条目超过声明长度。");
        return hash.GetHashAndReset();
    }

    private static void InsertSigningBlock(
        string inputApk,
        string outputApk,
        ApkSignerKeyMaterial keyMaterial,
        ApkSigningOptions options,
        CancellationToken cancellationToken)
    {
        using var input = new FileStream(inputApk, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        var sections = ApkZipSections.Locate(input);

        // The ZIP content section runs from 0 to the (future) signing block start, which is
        // the current central directory offset.
        // ZIP 内容区段从 0 到（未来的）签名块起始处，即当前中央目录偏移。
        var zipContentLength = sections.CentralDirectoryOffset;

        // EOCD raw bytes.
        // EOCD 原始字节。
        var eocd = new byte[sections.EocdSize];
        input.Seek(sections.EocdOffset, SeekOrigin.Begin);
        input.ReadExactly(eocd);

        var algorithm = ApkSignatureAlgorithmSelector.Select(keyMaterial.Key, options.MinSdkVersion);
        var hashName = ApkSignatureConstants.HashNameFor(algorithm);

        // AOSP ApkSigningBlockUtils: hash EOCD with the signing-block START offset.
        // AOSP 规定摘要中的 EOCD 指向签名块起点，摘要不依赖签名块长度。
        var contentDigest = ApkContentDigest.ComputeApkDigest(
            input, zipContentLength, sections.CentralDirectoryOffset, zipContentLength,
            sections.CentralDirectorySize, eocd, hashName, cancellationToken);

        var pairs = BuildSignaturePairs(keyMaterial, options, algorithm, contentDigest);
        var block = ApkSigningBlock.Build(pairs);
        var newCentralDirectoryOffset = checked((uint)(zipContentLength + block.Length));

        // Final layout: [zip content | signing block | central directory | patched EOCD].
        // 最终布局：[ZIP 内容 | 签名分块 | 中央目录 | 修正后的 EOCD]。
        var patchedEocd = (byte[])eocd.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(patchedEocd.AsSpan(16, 4), (uint)newCentralDirectoryOffset);

        using var output = new FileStream(outputApk, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.SequentialScan);
        CopyRange(input, output, 0, zipContentLength, cancellationToken);
        output.Write(block);
        CopyRange(input, output, sections.CentralDirectoryOffset, sections.CentralDirectorySize, cancellationToken);
        output.Write(patchedEocd);
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

    private static void CopyRange(Stream input, Stream output, long offset, long length, CancellationToken cancellationToken)
    {
        input.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[81920];
        long remaining = length;
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
}