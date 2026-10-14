using System.Buffers.Binary;
using System.Security.Cryptography;
using UotanStudio.Firmware.Core.Streams;
using UotanStudio.Firmware.Executable.Elf;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Firmware.Executable.Qualcomm;

/// <summary>Performs bounded, offline Qualcomm ELF/MBN inspection without accessing devices.
/// <para>有界地离线分析 Qualcomm ELF/MBN，不访问设备。</para></summary>
public static class QualcommImageAnalyzer
{
    private const int MaximumAnalyzedComponents = 32;

    /// <summary>Inspects a file on a worker thread; the file remains unchanged.
    /// <para>在工作线程检查文件；不会修改文件。</para></summary>
    /// <param name="path">Path of the file to inspect. <para>待检查的文件路径。</para></param>
    /// <param name="options">Resource limits and caller-owned root fingerprints. <para>资源限制与调用方根指纹。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>Independent format, cryptography, trust and device-use states.
    /// <para>独立的格式、密码学、信任与设备可用性状态。</para></returns>
    public static Task<QualcommAnalysisResult> AnalyzeAsync(
        string path, QualcommAnalysisOptions? options = null, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.RandomAccess);
            return Analyze(stream, options, cancellationToken);
        }, cancellationToken);

    /// <summary>Inspects a seekable stream and restores its position without closing it.
    /// <para>检查可寻址流，恢复原位置且不关闭流。</para></summary>
    /// <param name="stream">Readable seekable input. <para>可读、可寻址输入。</para></param>
    /// <param name="options">Resource limits and caller-owned root fingerprints. <para>资源限制与调用方根指纹。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>An offline analysis result. <para>离线分析结果。</para></returns>
    public static QualcommAnalysisResult Analyze(
        Stream stream, QualcommAnalysisOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("A readable, seekable stream is required.", nameof(stream));
        }

        options ??= new();
        if (options.MaximumCertificateBytes is < 1 or > 16 * 1024 * 1024
            || options.MaximumCertificateCount is < 1 or > 64
            || options.TrustedRootCertificateSha256 is null
            || options.TrustedRootCertificateSha256.Any(value =>
                value is null || value.Length != 64 || !value.All(Uri.IsHexDigit))
            || options.TrustedRootCertificateSha384 is null
            || options.TrustedRootCertificateSha384.Any(value =>
                value is null || value.Length != 96 || !value.All(Uri.IsHexDigit)))
        {
            throw new ArgumentException("Invalid Qualcomm analysis limits or root fingerprint.", nameof(options));
        }

        var originalPosition = stream.Position;
        try
        {
            var fileSize = stream.Length;
            var result = AnalyzeContent(stream, options, cancellationToken);
            var versions = result.Elf is { } elf
                ? QualcommVersionScanner.Read(stream, elf.Segments, cancellationToken) : [];
            if (stream.Length != fileSize)
            {
                throw new InvalidDataException("Source length changed during Qualcomm analysis.");
            }

            return result with
            {
                FileSize = fileSize,
                MelfComponents = result.Format == QualcommImageFormat.Elf ? result.MelfComponents : [],
                SigningMetadata = [.. result.SigningMetadata,
                    .. result.Certificates.Where(certificate => certificate.Index == 0 && certificate.Attributes.Count > 0)
                        .Select(QualcommMetadataReader.FromCertificate)],
                VersionStrings = versions
            };
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    private static QualcommAnalysisResult AnalyzeContent(
        Stream stream, QualcommAnalysisOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (stream.Length < 8)
        {
            return new() { Diagnostics = ["Input is too short to identify."] };
        }

        Span<byte> prefix = stackalloc byte[8];
        QualcommMbnLayout.ReadAt(stream, 0, prefix, token);
        if (prefix[..4].SequenceEqual("\u007fELF"u8))
        {
            return AnalyzeElfContainer(stream, options, token);
        }

        if (stream.Length >= 80
            && BinaryPrimitives.ReadUInt32LittleEndian(prefix) == 0x844bdcd1
            && BinaryPrimitives.ReadUInt32LittleEndian(prefix[4..]) == 0x73d71034)
        {
            return AnalyzeSbl(stream, options, token);
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(prefix[4..]) is 3 or 5 or 6 or 7)
        {
            return AnalyzeMbn(stream, options, token);
        }

        return new() { Diagnostics = ["No recognized Qualcomm ELF or MBN header."] };
    }

    /// <summary>
    /// Analyzes an ELF input and, when it belongs to a multi-component container, each discovered component
    /// inside its own bounded window.
    /// <para>分析 ELF 输入；当其属于多组件容器时，在每个候选组件自己的有界窗口内分析该组件。</para>
    /// </summary>
    /// <remarks>
    /// Components are authenticated against their window's bytes only; the offset-0 analysis stays the
    /// returned top-level result and component results are reported independently. Candidates strictly inside
    /// an earlier component's hashed range are recorded as nested evidence instead of being re-analyzed, and
    /// discovering several components never claims that every component authenticated or speaks for OEM or
    /// device acceptance.
    /// <para>组件仅针对其窗口内的字节完成认证；偏移 0 处的分析仍是返回值的顶层字段，组件结果独立报告。
    /// 落在更早组件已成功摘要范围内的候选项只记录为嵌套证据而不再次分析；发现多个组件并不等于所有组件均已
    /// 通过认证，也不代表 OEM 或设备的接受。</para>
    /// </remarks>
    /// <param name="stream">Readable, seekable container positioned anywhere.
    /// <para>任意位置处的可读、可寻址容器。</para></param>
    /// <param name="options">Resource limits and caller-owned root fingerprints.
    /// <para>资源限制与调用方根指纹。</para></param>
    /// <param name="token">Cancellation token checked before every component.
    /// <para>每个组件分析前检查的取消令牌。</para></param>
    /// <returns>The offset-0 result carrying per-component evidence.
    /// <para>携带逐组件证据的偏移 0 处结果。</para></returns>
    private static QualcommAnalysisResult AnalyzeElfContainer(
        Stream stream, QualcommAnalysisOptions options, CancellationToken token)
    {
        var primary = AnalyzeElf(stream, options, token);
        var components = QualcommMelfComponents.Scan(stream, token);
        if (components.Count == 0)
        {
            return primary;
        }

        var results = new List<QualcommComponentAuthenticationResult>(components.Count);
        List<(long Start, long End)> hashed = new(components.Count);
        List<string> diagnostics = new(components.Count);
        var combined = QualcommVerificationStatus.NotChecked;
        var analyzed = 0;
        for (var index = 0; index < components.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var component = components[index];
            if (hashed.Any(range => component.Offset > range.Start && component.Offset < range.End))
            {
                results.Add(new QualcommComponentAuthenticationResult
                {
                    ComponentIndex = index,
                    Offset = component.Offset,
                    Length = component.Length,
                    IsNestedAuthenticated = true,
                    Diagnostics = ["ELF magic lies inside a byte range an earlier component hashes, so it was not analyzed independently."]
                });
                continue;
            }

            if (analyzed == MaximumAnalyzedComponents)
            {
                diagnostics.Add($"Only the first {MaximumAnalyzedComponents} ELF components were analyzed; "
                    + $"{components.Count - index} remaining candidates were not authenticated.");
                combined = QualcommAnalysisResult.Combine(combined, QualcommVerificationStatus.Unsupported);
                break;
            }

            analyzed++;
            var (analysis, stage, componentDiagnostics) = AnalyzeComponent(stream, component, options, token);

            combined = QualcommAnalysisResult.Combine(combined, analysis is null
                ? QualcommVerificationStatus.Invalid
                : QualcommAnalysisResult.Combine(
                    QualcommAnalysisResult.Combine(analysis.HashStatus, analysis.SignatureStatus),
                    analysis.CertificateChainStatus));
            results.Add(new QualcommComponentAuthenticationResult
            {
                ComponentIndex = index,
                Offset = component.Offset,
                Length = component.Length,
                HashTableStatus = analysis?.HashStatus ?? QualcommVerificationStatus.NotChecked,
                SignatureStatus = analysis?.SignatureStatus ?? QualcommVerificationStatus.NotChecked,
                CertificateChainStatus = analysis?.CertificateChainStatus ?? QualcommVerificationStatus.NotChecked,
                TrustedRootStatus = analysis?.TrustedRootStatus ?? QualcommVerificationStatus.NotChecked,
                FailureStage = stage,
                Diagnostics = componentDiagnostics
            });
            if (analysis?.HashStatus == QualcommVerificationStatus.Valid && analysis.Elf is { } elf)
            {
                foreach (var segment in elf.Segments)
                {
                    if (segment.Type == 1 && segment.FileSize > 0)
                    {
                        var start = checked(component.Offset + (long)segment.Offset);
                        hashed.Add((start, checked(start + (long)segment.FileSize)));
                    }
                }
            }
        }

        return primary with
        {
            MelfComponents = components,
            ComponentAuthentication = results.AsReadOnly(),
            MelfAuthenticationStatus = combined,
            Diagnostics = [.. primary.Diagnostics, .. diagnostics]
        };
    }

    /// <summary>
    /// Analyzes one ELF component inside the window that the following candidate boundary defines.
    /// A container that stores another ELF inside a component's own hashed payload makes that boundary clip
    /// the enclosing component's declared ranges, so a structural failure is retried once with the window
    /// widened to the end of the input. The widened result is reported with its own diagnostic; widening never
    /// turns a failed check into a passed one, it only lets the component be measured against bytes that belong
    /// to it rather than being cut off.
    /// <para>在下一个候选边界所定义的窗口内分析单个 ELF 组件。当容器把另一个 ELF 存放在某组件自身
    /// 已摘要的载荷内时，该边界会裁掉外层组件声明的范围，因此结构失败会把窗口放宽到输入末尾并重试一次。
    /// 放宽后的结果会附带自己的诊断；放宽绝不会把失败的检查变成通过，只是让组件按其真正拥有的字节而非
    /// 被截断的字节参与度量。</para>
    /// </summary>
    /// <param name="stream">Container stream. <para>容器流。</para></param>
    /// <param name="component">Component discovered by the scan. <para>扫描发现的组件。</para></param>
    /// <param name="options">Resource limits and caller-owned root fingerprints.
    /// <para>资源限制与调用方根指纹。</para></param>
    /// <param name="token">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The component analysis, its failure stage and diagnostics; the analysis is
    /// <see langword="null"/> when the component structure cannot be read at all.
    /// <para>组件分析结果、失败阶段与诊断；当组件结构完全无法读取时分析结果为空。</para></returns>
    private static (QualcommAnalysisResult? Analysis, string Stage, IReadOnlyList<string> Diagnostics)
        AnalyzeComponent(Stream stream, QualcommMelfComponent component, QualcommAnalysisOptions options,
            CancellationToken token)
    {
        try
        {
            using var window = new BoundedReadStream(stream, component.Offset, component.Length, true);
            var analysis = AnalyzeElf(window, options, token);
            return (analysis, ComponentFailureStage(analysis), analysis.Diagnostics);
        }
        catch (Exception error) when (error is InvalidDataException or ExecutableException)
        {
            if (component.Offset + component.Length >= stream.Length)
            {
                return (null, "ElfStructure", [error.Message]);
            }

            try
            {
                using var widened = new BoundedReadStream(stream, component.Offset,
                    stream.Length - component.Offset, true);
                var retried = AnalyzeElf(widened, options, token);
                return (retried, ComponentFailureStage(retried),
                    [.. retried.Diagnostics,
                        "A nested component boundary clipped this component's declared ranges, so the window was widened to the end of the input."]);
            }
            catch (Exception widenedError) when (widenedError is InvalidDataException or ExecutableException)
            {
                return (null, "ElfStructure", [widenedError.Message]);
            }
        }
    }

    /// <summary>
    /// Names the first authentication stage a component failed.
    /// ElfStructure covers a component whose declared ranges do not fit its own window, HashSegment a container
    /// without one usable MI_PBT hash segment, HashTable a failed or unsupported digest-table comparison,
    /// Signature an image signature that did not verify, and CertificateChain an embedded chain that did not
    /// build. The stage is descriptive only; it never promotes a failed check into a passed one.
    /// <para>指明组件最先失败的认证阶段。ElfStructure 表示组件声明的范围超出自身窗口，
    /// HashSegment 表示容器没有单个可用的 MI_PBT 摘要段，HashTable 表示摘要表比较失败或不受支持，
    /// Signature 表示镜像签名未通过验证，CertificateChain 表示内嵌证书链未能构建。
    /// 该阶段仅作描述，绝不会把失败的检查提升为通过。</para>
    /// </summary>
    /// <param name="analysis">Component analysis result. <para>组件分析结果。</para></param>
    /// <returns>The failure stage name. <para>失败阶段名称。</para></returns>
    private static string ComponentFailureStage(QualcommAnalysisResult analysis)
    {
        if (analysis.Elf is null)
        {
            return "ElfStructure";
        }

        if (analysis.Mbn is null && analysis.HashStatus != QualcommVerificationStatus.NotPresent)
        {
            return "HashSegment";
        }

        if (analysis.HashStatus is QualcommVerificationStatus.Invalid or QualcommVerificationStatus.Unsupported)
        {
            return "HashTable";
        }

        if (analysis.SignatureStatus is QualcommVerificationStatus.Invalid or QualcommVerificationStatus.Unsupported)
        {
            return "Signature";
        }

        return analysis.CertificateChainStatus is QualcommVerificationStatus.Invalid or QualcommVerificationStatus.Unsupported
            ? "CertificateChain"
            : "None";
    }

    private static QualcommAnalysisResult AnalyzeElf(Stream stream, QualcommAnalysisOptions options, CancellationToken token)
    {
        var elf = ElfReader.Read(stream, token);
        if (!elf.Header.IsLittleEndian)
        {
            return new()
            {
                Format = QualcommImageFormat.Elf,
                Elf = elf,
                HashStatus = QualcommVerificationStatus.Unsupported,
                Diagnostics = ["Qualcomm MI_PBT verification requires a little-endian ELF."]
            };
        }

        var hashSegments = elf.Segments.Where(segment => (segment.Flags >> 24 & 7) == 2).ToArray();
        if (hashSegments.Length == 0)
        {
            return new()
            {
                Format = QualcommImageFormat.Elf,
                Elf = elf,
                SupportedCommands = QualcommProgrammerCommands.Find(stream, elf, token)
            };
        }

        if (hashSegments.Length != 1 || hashSegments[0].FileSize == 0
            || hashSegments[0].FileSize > 64 * 1024 * 1024)
        {
            return new()
            {
                Format = QualcommImageFormat.Elf,
                Elf = elf,
                HashStatus = QualcommVerificationStatus.Invalid,
                Diagnostics = ["The MI_PBT hash segment is missing, duplicated or too large."]
            };
        }

        var hashSegment = hashSegments[0];
        QualcommMbnLayout layout;
        try
        {
            layout = QualcommMbnLayout.Read(stream, checked((long)hashSegment.Offset),
                checked((long)hashSegment.FileSize), token);
        }
        catch (Exception error) when (error is InvalidDataException or ExecutableException)
        {
            return new()
            {
                Format = QualcommImageFormat.Elf,
                Elf = elf,
                HashStatus = QualcommVerificationStatus.Invalid,
                Diagnostics = [error.Message]
            };
        }

        var digestResults = VerifyHashes(stream, elf, hashSegment, layout, token);
        var authorities = QualcommAuthentication.Verify(stream, (long)hashSegment.Offset,
            layout, options, token);
        return new()
        {
            Format = QualcommImageFormat.Elf,
            Elf = elf,
            Mbn = MbnInfo(layout, digestResults.Algorithm),
            Regions = QualcommMetadataReader.Regions((long)hashSegment.Offset, layout),
            SigningMetadata = QualcommMetadataReader.Read(stream, (long)hashSegment.Offset, layout, token),
            HashStatus = digestResults.Status,
            SegmentDigests = digestResults.Digests,
            QualcommSignature = authorities.Qualcomm,
            OemSignature = authorities.Oem,
            MetadataRootHashStatus = authorities.MetadataRootHashStatus,
            Certificates = authorities.Certificates,
            SupportedCommands = QualcommProgrammerCommands.Find(stream, elf, token),
            Diagnostics = digestResults.Diagnostic is null ? authorities.Diagnostics :
                [digestResults.Diagnostic, .. authorities.Diagnostics]
        };
    }

    private static QualcommAnalysisResult AnalyzeMbn(Stream stream, QualcommAnalysisOptions options, CancellationToken token)
    {
        try
        {
            var layout = QualcommMbnLayout.Read(stream, 0, stream.Length, token);
            var authorities = QualcommAuthentication.Verify(stream, 0, layout, options, token);
            return new()
            {
                Format = QualcommImageFormat.Mbn,
                Mbn = MbnInfo(layout, layout.HashAlgorithm == 3 ? "SHA-384" : ""),
                Regions = QualcommMetadataReader.Regions(0, layout),
                SigningMetadata = QualcommMetadataReader.Read(stream, 0, layout, token),
                HashStatus = QualcommVerificationStatus.NotChecked,
                QualcommSignature = authorities.Qualcomm,
                OemSignature = authorities.Oem,
                MetadataRootHashStatus = authorities.MetadataRootHashStatus,
                Certificates = authorities.Certificates,
                Diagnostics = ["Standalone MBN hash tables require an associated ELF to verify segment content.",
                    .. authorities.Diagnostics]
            };
        }
        catch (Exception error) when (error is InvalidDataException or ExecutableException)
        {
            return new() { Diagnostics = [error.Message] };
        }
    }

    private static QualcommAnalysisResult AnalyzeSbl(Stream stream, QualcommAnalysisOptions options, CancellationToken token)
    {
        Span<byte> header = stackalloc byte[80];
        QualcommMbnLayout.ReadAt(stream, 0, header, token);
        ulong source = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
        ulong total = BinaryPrimitives.ReadUInt32LittleEndian(header[28..]);
        ulong code = BinaryPrimitives.ReadUInt32LittleEndian(header[32..]);
        ulong signature = BinaryPrimitives.ReadUInt32LittleEndian(header[40..]);
        ulong certificate = BinaryPrimitives.ReadUInt32LittleEndian(header[48..]);
        var rootSelection = BinaryPrimitives.ReadUInt32LittleEndian(header[52..]);
        var rootCount = BinaryPrimitives.ReadUInt32LittleEndian(header[56..]);
        if (source < 80 || code + signature + certificate > total || source > (ulong)stream.Length
            || total > (ulong)stream.Length - source || certificate > (uint)options.MaximumCertificateBytes
            || signature > 8192 || rootCount > 64
            || rootCount > 0 && (rootSelection == 0 || rootSelection > rootCount)
            || rootCount == 0 && rootSelection != 0)
        {
            return new()
            {
                Format = QualcommImageFormat.SblMbn,
                HashStatus = QualcommVerificationStatus.Invalid,
                Diagnostics = ["SBL payload or authentication range is invalid."]
            };
        }

        var authority = QualcommAuthentication.VerifySbl(stream, (long)(source + code),
            (int)signature, (long)(source + code + signature), (int)certificate,
            (long)(source + code), rootCount > 0 ? (int)rootSelection - 1 : null,
            options, token);
        return new()
        {
            Format = QualcommImageFormat.SblMbn,
            Regions = [
                new("SblHeader", 0, 80), new("SblCode", (long)source, (long)code),
                new("OemSignature", (long)(source + code), (long)signature),
                new("OemCertificates", (long)(source + code + signature), (long)certificate)],
            HashStatus = QualcommVerificationStatus.NotPresent,
            OemSignature = authority.Result,
            Certificates = authority.Certificates,
            SupportedCommands = QualcommProgrammerCommands.FindUnmapped(stream, (long)source, (long)code, token),
            Diagnostics = authority.Diagnostics
        };
    }

    private static QualcommMbnInfo MbnInfo(QualcommMbnLayout layout, string algorithm)
        => new(layout.Version, layout.ImageId, layout.HashOffset, layout.HashLength, algorithm, layout.SoftwareId);

    private static (QualcommVerificationStatus Status, string Algorithm, IReadOnlyList<QualcommSegmentDigest> Digests, string? Diagnostic)
        VerifyHashes(Stream stream, ElfInfo elf, ElfSegment hashSegment, QualcommMbnLayout layout, CancellationToken token)
    {
        if (layout.Version == 7 && layout.HashAlgorithm != 3)
        {
            return (QualcommVerificationStatus.Unsupported, "", [], "Unsupported MBN v7 hash algorithm.");
        }

        ulong count = 0;
        Span<byte> expected = stackalloc byte[48];
        foreach (var segment in elf.Segments)
        {
            var pageMode = (segment.Flags >> 20 & 1) != 0;
            if (pageMode)
            {
                var skipped = (4096 - segment.VirtualAddress % 4096) % 4096;
                if (segment.FileSize < skipped || (segment.FileSize - skipped) % 4096 != 0)
                {
                    return (QualcommVerificationStatus.Invalid, "", [], "Invalid MI_PBT page alignment.");
                }

                count += (segment.FileSize - skipped) / 4096;
            }
            else if (segment.Type != 6)
            {
                count++;
            }

            if (count > 262144)
            {
                return (QualcommVerificationStatus.Unsupported, "", [], "MI_PBT digest count exceeds the analysis limit.");
            }
        }

        var trailingV6 = layout.Version == 6 && layout.HashLength > 20
            && (ulong)(layout.HashLength - 20) == count * 48;
        int digestSize;
        var algorithm = "";
        if (layout.Version is 6 or 7)
        {
            digestSize = 48;
            algorithm = "SHA-384";
            if ((ulong)layout.HashLength != count * 48 && !trailingV6)
            {
                return (QualcommVerificationStatus.Invalid, algorithm, [], "MI_PBT SHA-384 digest count differs from the table length.");
            }
        }
        else if ((ulong)layout.HashLength == count * 32 && count > 0)
        {
            digestSize = 32;
            algorithm = "SHA-256";
        }
        else if ((ulong)layout.HashLength == count * 20 && count > 0)
        {
            digestSize = 20;
            algorithm = "SHA-1";
        }
        else
        {
            return (QualcommVerificationStatus.Invalid, "", [], "MI_PBT digest count or algorithm is ambiguous.");
        }

        var digests = new List<QualcommSegmentDigest>();
        var index = 0L;
        foreach (var segment in elf.Segments)
        {
            var pageMode = (segment.Flags >> 20 & 1) != 0;
            if (!pageMode && segment.Type == 6)
            {
                continue;
            }

            var skipped = pageMode ? (4096 - segment.VirtualAddress % 4096) % 4096 : 0;
            var pages = pageMode ? (segment.FileSize - skipped) / 4096 : 1;
            for (ulong page = 0; page < pages; page++)
            {
                token.ThrowIfCancellationRequested();
                QualcommMbnLayout.ReadAt(stream, checked((long)hashSegment.Offset + layout.HashOffset + index * digestSize),
                    expected[..digestSize], token);
                index++;
                var segmentType = segment.Flags >> 24 & 7;
                var access = segment.Flags >> 21 & 7;
                var shouldHash = segment.FileSize > 0 && segmentType != 2 && access is not (3 or 4);
                var status = QualcommVerificationStatus.Valid;
                if (shouldHash)
                {
                    byte[] actual;
                    if (segmentType == 7 && !pageMode)
                    {
                        using var hash = CreateMbnDigest(algorithm);
                        AppendRange(hash, stream, 0, elf.Header.HeaderSize, token);
                        AppendRange(hash, stream, (long)elf.Header.ProgramHeaderOffset,
                            checked((long)elf.Header.ProgramHeaderEntrySize * elf.Header.ProgramHeaderCount), token);
                        actual = hash.GetHashAndReset();
                    }
                    else
                    {
                        using var hash = CreateMbnDigest(algorithm);
                        AppendRange(hash, stream, checked((long)(segment.Offset + skipped + page * 4096)),
                            pageMode ? 4096 : checked((long)segment.FileSize), token);
                        actual = hash.GetHashAndReset();
                    }

                    if (!CryptographicOperations.FixedTimeEquals(expected[..digestSize], actual))
                    {
                        status = QualcommVerificationStatus.Invalid;
                    }
                }
                else if (expected[..digestSize].IndexOfAnyExcept((byte)0) >= 0)
                {
                    status = QualcommVerificationStatus.Invalid;
                }

                digests.Add(new(segment.Index, pageMode ? checked((int)page) : null, status));
                if (status == QualcommVerificationStatus.Invalid)
                {
                    return (status, algorithm, digests.AsReadOnly(), $"MI_PBT digest mismatch at segment {segment.Index}, page {page}.");
                }
            }
        }

        return (trailingV6 ? QualcommVerificationStatus.Unsupported : QualcommVerificationStatus.Valid,
            algorithm, digests.AsReadOnly(),
            trailingV6 ? "MBN v6 carries a trailing 20-byte digest outside the segment table; it was not independently verified." : null);
    }

    internal static DigestAccumulator CreateMbnDigest(string name) => name switch
    {
        "SHA-1" => Sha1.CreateAccumulator(),
        "SHA-384" => Sha384.CreateAccumulator(),
        _ => Sha256.CreateAccumulator()
    };

    internal static void AppendRange(DigestAccumulator hash, Stream stream, long offset, long length, CancellationToken token) =>
        AppendBytes(hash.Append, stream, offset, length, token);

    private static void AppendBytes(Action<ReadOnlySpan<byte>> append, Stream stream, long offset, long length, CancellationToken token)
    {
        if (offset < 0 || length < 0 || offset > stream.Length || length > stream.Length - offset)
        {
            throw new ExecutableCorruptedException("Qualcomm signed range exceeds the file.");
        }

        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            stream.Position = offset;
            while (length > 0)
            {
                token.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, 0, (int)Math.Min(length, buffer.Length));
                if (read == 0)
                {
                    throw new EndOfStreamException();
                }

                append(buffer.AsSpan(0, read));
                length -= read;
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}