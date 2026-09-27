using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Repacking;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.UpdateApp.Build;

/// <summary>
/// Captures and rebuilds byte-faithful <c>update.app</c> containers: entry order, raw per-entry header bytes
/// (unknown fields pass through untouched), non-payload prefix/gap/trailing bytes, and anonymous framed entries
/// are preserved, while payload bytes are copied through unchanged — encrypted payloads are never re-derived.
/// <para>捕获并按字节保真重建 <c>update.app</c> 容器：保留条目顺序、逐条目原始头部字节（未知字段原样透传）、
/// 非载荷的前缀/间隙/尾部字节以及匿名帧区域条目；载荷字节原样拷贝，绝不重新推导加密载荷密钥。</para>
/// </summary>
public static class UpdateAppFaithfulRepacker
{
    private const int CanonicalHeaderSize = 98;
    private const int HeaderChecksumOffset = 92;
    private const int BlockSizeOffset = 94;
    private const int MaximumEntries = 1024;
    private const int MaximumHeaderBytes = 1024 * 1024;
    private const int MaximumInlinePayloadBytes = 1024 * 1024;
    private const long MaximumLayoutBytes = 64 * 1024 * 1024;
    private const int CopyBufferSize = 128 * 1024;

    /// <summary>
    /// Validates that a container is within the faithful-repack bounds without writing any artifacts.
    /// <para>仅校验容器是否处于保真重打包限制内，不写出任何产物。</para>
    /// </summary>
    /// <param name="sourcePath">
    /// Container path to scan; a missing file or framing error declines capture.
    /// <para>待扫描的容器路径；文件缺失或帧结构错误都会拒绝捕获。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Checked while enumerating framed entries. <para>在枚举帧区域条目期间检查。</para>
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the container has entries and every header and non-payload layout region stays
    /// inside its bounds; <see langword="false"/> means only the canonical rebuild applies.
    /// <para>容器有条目且全部头部与非载荷布局区域均在限制内时返回 <see langword="true"/>；<see langword="false"/> 表示仅可用标准重建。</para>
    /// </returns>
    public static bool TryValidateCapture(string sourcePath, CancellationToken cancellationToken)
    {
        try
        {
            var raws = Collect(sourcePath, cancellationToken);
            return raws.Count > 0
                && TryMeasure(raws, new FileInfo(sourcePath).Length, out _, out _, out _);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Captures the faithful reconstruction configuration for a just-extracted container: raw headers enter the
    /// repack JSON and every non-payload byte region enters a bounded layout artifact stored beside the metadata.
    /// <para>为刚解包的容器捕获保真重建配置：原始头部写入重打包 JSON，全部非载荷字节区域写入元数据旁的有界布局产物。</para>
    /// </summary>
    /// <param name="metadata">
    /// Metadata instance receiving the <c>updateApp</c> configuration node and, when limits are exceeded or the
    /// extraction was filtered, the reduced packing capability and notes.
    /// <para>接收 <c>updateApp</c> 配置节点的元数据实例；超出限制或解包被筛选时降级打包能力并写入说明。</para>
    /// </param>
    /// <param name="sourcePath">
    /// Source container that was extracted. <para>已解包的源容器。</para>
    /// </param>
    /// <param name="outputDirectory">
    /// Extraction directory holding the committed <c>&lt;name&gt;.img</c> payload files.
    /// <para>持有已提交的 <c>&lt;name&gt;.img</c> 载荷文件的解包目录。</para>
    /// </param>
    /// <param name="metadataDirectory">
    /// Directory in which the layout artifact and metadata JSON are published.
    /// <para>发布布局产物与元数据 JSON 的目录。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Checked during scanning, artifact writing and hashing; cancellation propagates.
    /// <para>在扫描、产物写入与哈希期间检查；取消会向上抛出。</para>
    /// </param>
    /// <exception cref="InvalidDataException">Layout accounting is inconsistent or a payload size drifted after extraction.
    /// <para>布局记账不一致，或载荷大小在解包后发生变化。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    public static void Capture(RepackMetadata metadata, string sourcePath, string outputDirectory,
        string metadataDirectory, CancellationToken cancellationToken)
    {
        var sourceLength = new FileInfo(sourcePath).Length;
        var raws = Collect(sourcePath, cancellationToken);
        long layoutBytes = 0;
        string? declineReason = null;
        long[] gapLengths = [];
        if (raws.Count == 0 || !TryMeasure(raws, sourceLength, out layoutBytes, out declineReason, out gapLengths))
        {
            metadata.CanPackDirectory = false;
            metadata.PackingNotes += declineReason
                ?? " UPDATE.APP holds no framed entry; only the canonical rebuild applies.";
            return;
        }

        var payloadPaths = new string?[raws.Count];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < raws.Count; index++)
        {
            var entry = raws[index].Entry;
            if (entry.Name.Length == 0)
                continue;
            var finalName = entry.Name;
            for (var suffix = 2; seen.Contains(finalName); suffix++)
                finalName = $"{entry.Name}_{suffix}";
            seen.Add(finalName);
            var candidate = FileExtraction.ResolvePath(outputDirectory, $"{finalName}.img");
            if (!File.Exists(candidate) || new FileInfo(candidate).Length != entry.FileSize)
            {
                metadata.CanPackDirectory = false;
                metadata.PackingNotes += " Faithful repack needs the complete unfiltered extraction with every payload file " +
                    "matching its header size; only the canonical rebuild is available after a filtered extraction.";
                return;
            }

            payloadPaths[index] = candidate;
        }

        var entries = new JsonArray();
        for (var index = 0; index < raws.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (entry, header) = raws[index];
            var item = new JsonObject
            {
                ["header"] = Convert.ToBase64String(header),
                ["gapAfter"] = gapLengths[index]
            };
            if (payloadPaths[index] is { } path)
                item["file"] = Path.GetFileName(path);
            else if (entry.FileSize > 0)
                item["inline"] = ReadInlinePayload(sourcePath, entry, cancellationToken);
            entries.Add(item);
        }

        var artifact = $".updateapp-layout-{Guid.NewGuid():N}.bin";
        var artifactPath = FileExtraction.ResolvePath(metadataDirectory, artifact);
        var digest = WriteLayoutArtifact(sourcePath, raws, gapLengths, artifactPath, cancellationToken);

        metadata.Configuration["updateApp"] = new JsonObject
        {
            ["version"] = 1,
            ["prefixLength"] = raws[0].Entry.HeaderOffset,
            ["sourceLength"] = sourceLength,
            ["layoutBytes"] = layoutBytes,
            ["layoutFile"] = artifact,
            ["layoutSha256"] = Convert.ToHexString(digest),
            ["entries"] = entries
        };
        metadata.PackingNotes += " Faithful repack preserves entry order, raw headers and non-payload bytes; " +
            "payload bytes pass through unchanged and encrypted payloads are never re-derived. " +
            "A payload whose size or content no longer matches the captured header CRC table is rejected; " +
            "use the canonical rebuild for new content.";
    }

    /// <summary>
    /// Rebuilds a container from captured configuration: raw headers, verbatim payload files and the layout
    /// artifact are streamed back in the original order, and any CRC16-table entry payload is re-verified first.
    /// <para>依据捕获的配置重建容器：按原顺序回流式写出原始头部、原样载荷文件与布局产物；带 CRC16 表的条目先重新校验载荷。</para>
    /// </summary>
    /// <param name="config">
    /// The <c>updateApp</c> configuration node written by <see cref="Capture"/>. <para>由 <see cref="Capture"/> 写入的 <c>updateApp</c> 配置节点。</para>
    /// </param>
    /// <param name="inputPath">
    /// Extraction directory containing the payload files. <para>包含载荷文件的解包目录。</para>
    /// </param>
    /// <param name="outputPath">
    /// Destination container path, published atomically. <para>目标容器路径，以原子方式发布。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Checked throughout planning and writing; cancellation leaves no partial output.
    /// <para>在规划与写入全程检查；取消时不会留下部分输出。</para>
    /// </param>
    /// <returns>
    /// The framed entry count written to the new container. <para>写入新容器的帧区域条目数。</para>
    /// </returns>
    /// <exception cref="InvalidDataException">The configuration is malformed or out of bounds, a payload file is
    /// missing or changed size, or a header or per-block CRC16 check fails.
    /// <para>配置格式错误或超限、载荷文件缺失或大小已变，或头部/逐块 CRC16 校验失败。</para></exception>
    /// <exception cref="IOException">The artifact or payload cannot be read, or publication fails.
    /// <para>无法读取产物或载荷，或发布失败。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    public static int Repack(JsonObject config, string inputPath, string outputPath, CancellationToken cancellationToken)
    {
        if (config["version"]?.GetValue<int>() != 1 || config["entries"] is not JsonArray items
            || items.Count is < 1 or > MaximumEntries)
        {
            throw new InvalidDataException("Invalid UPDATE.APP reconstruction metadata.");
        }

        var prefixLength = config["prefixLength"]?.GetValue<long>() ?? throw new InvalidDataException("Missing UPDATE.APP prefix length.");
        var sourceLength = config["sourceLength"]?.GetValue<long>() ?? throw new InvalidDataException("Missing UPDATE.APP source length.");
        var declaredLayout = config["layoutBytes"]?.GetValue<long>() ?? throw new InvalidDataException("Missing UPDATE.APP layout size.");
        ArgumentOutOfRangeException.ThrowIfNegative(prefixLength);
        ArgumentOutOfRangeException.ThrowIfNegative(sourceLength);

        var plans = new List<Plan>(items.Count);
        var layoutBytes = prefixLength;
        var totalLength = prefixLength;
        foreach (var node in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = node as JsonObject ?? throw new InvalidDataException("Missing UPDATE.APP entry metadata.");
            var header = DecodeHeader(item["header"]);
            var fileSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24));
            byte[]? inline = null;
            string? payloadPath = null;
            if (item["file"] is JsonNode fileNode)
            {
                var fileName = fileNode.GetValue<string>();
                if (fileName.Length is < 1 or > 260 || fileName.IndexOfAny(['/', '\\', '\0', ':']) >= 0)
                    throw new InvalidDataException("Unsafe UPDATE.APP payload file name.");
                payloadPath = FileExtraction.ResolvePath(inputPath, fileName);
            }
            else if (item["inline"] is JsonNode inlineNode)
            {
                var encoded = inlineNode.GetValue<string>();
                if (encoded.Length > (MaximumInlinePayloadBytes + 2) / 3 * 4 + 4)
                    throw new InvalidDataException("Inlined UPDATE.APP payload exceeds the capture bound.");
                inline = Convert.FromBase64String(encoded);
                if (inline.Length != fileSize)
                    throw new InvalidDataException("Inlined UPDATE.APP payload does not match its header length.");
            }
            else if (fileSize != 0)
            {
                throw new InvalidDataException("A non-empty UPDATE.APP entry carries no payload source.");
            }

            var gap = item["gapAfter"]?.GetValue<long>() ?? throw new InvalidDataException("Missing UPDATE.APP gap length.");
            ArgumentOutOfRangeException.ThrowIfNegative(gap);
            layoutBytes = checked(layoutBytes + gap);
            totalLength = checked(totalLength + header.LongLength + fileSize + gap);
            VerifyPayload(header, payloadPath, inline, cancellationToken);
            plans.Add(new Plan(header, payloadPath, inline, gap));
        }

        if (totalLength != sourceLength || layoutBytes != declaredLayout)
            throw new InvalidDataException("UPDATE.APP reconstruction metadata does not account for the full source image.");
        if (layoutBytes > MaximumLayoutBytes)
            throw new InvalidDataException("UPDATE.APP non-payload layout exceeds the 64 MiB rebuild limit.");

        var metadataPath = RepackMetadata.ResolveActualMetadataPath(inputPath)
            ?? throw new InvalidDataException("UPDATE.APP reconstruction metadata is missing.");
        if (string.Equals(Path.GetFullPath(outputPath), metadataPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("UPDATE.APP output cannot replace its metadata.");
        var metadataDirectory = Path.GetDirectoryName(Path.GetFullPath(metadataPath))!;
        var artifactName = config["layoutFile"]?.GetValue<string>()
            ?? throw new InvalidDataException("Missing UPDATE.APP layout artifact name.");
        if (artifactName.Length is < 1 or > 128 || artifactName.IndexOfAny(['/', '\\', '\0', ':']) >= 0)
            throw new InvalidDataException("Unsafe UPDATE.APP layout artifact name.");
        var artifactPath = FileExtraction.ResolvePath(metadataDirectory, artifactName);
        var expectedDigest = Convert.FromHexString(config["layoutSha256"]?.GetValue<string>()
            ?? throw new InvalidDataException("Missing UPDATE.APP layout digest."));
        if (expectedDigest.Length != 32)
            throw new InvalidDataException("Invalid UPDATE.APP layout digest length.");
        using (var artifactProbe = File.OpenRead(artifactPath))
        {
            if (artifactProbe.Length != layoutBytes)
                throw new InvalidDataException("UPDATE.APP layout artifact size drifted from the capture.");
            if (!SHA256.HashData(artifactProbe).AsSpan().SequenceEqual(expectedDigest))
                throw new InvalidDataException("UPDATE.APP layout artifact failed its SHA-256 check.");
        }

        FileExtraction.WriteAtomic(outputPath, output =>
        {
            using var artifact = new FileStream(artifactPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                CopyBufferSize, FileOptions.SequentialScan);
            CopyStream(artifact, output, prefixLength, cancellationToken);
            foreach (var plan in plans)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.Write(plan.Header);
                if (plan.PayloadPath is not null)
                {
                    using var payload = new FileStream(plan.PayloadPath, FileMode.Open, FileAccess.Read,
                        FileShare.Read, CopyBufferSize, FileOptions.SequentialScan);
                    CopyStream(payload, output, payload.Length, cancellationToken);
                }
                else if (plan.Inline is not null)
                {
                    output.Write(plan.Inline);
                }

                CopyStream(artifact, output, plan.GapAfter, cancellationToken);
            }

            if (output.Position != sourceLength)
                throw new InvalidDataException("UPDATE.APP reconstruction length drifted from the captured source.");
        }, cancellationToken: cancellationToken);
        return plans.Count;
    }

    private static List<(UpdateAppEntry Entry, byte[] Header)> Collect(string sourcePath, CancellationToken cancellationToken)
    {
        var raws = new List<(UpdateAppEntry, byte[])>();
        foreach (var raw in UpdateAppProcessor.GetRawEntries(sourcePath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (raws.Count >= MaximumEntries)
                throw new InvalidDataException($"UPDATE.APP contains more than {MaximumEntries} framed entries.");
            raws.Add((raw.Entry, raw.HeaderBytes));
        }

        return raws;
    }

    private static bool TryMeasure(List<(UpdateAppEntry Entry, byte[] Header)> raws, long sourceLength,
        out long layoutBytes, out string? declineReason, out long[] gapLengths)
    {
        layoutBytes = 0;
        declineReason = null;
        gapLengths = [];
        try
        {
            var gaps = new long[raws.Count];
            layoutBytes = checked(raws[0].Entry.HeaderOffset);
            for (var index = 0; index < raws.Count; index++)
            {
                var (entry, header) = raws[index];
                if (header.LongLength > MaximumHeaderBytes)
                {
                    declineReason = " A raw UPDATE.APP entry header exceeds the 1 MiB faithful-capture bound.";
                    return false;
                }

                if (entry.Name.Length == 0 && entry.FileSize > MaximumInlinePayloadBytes)
                {
                    declineReason = " An anonymous UPDATE.APP payload exceeds the 1 MiB inline bound.";
                    return false;
                }

                var end = checked(entry.HeaderOffset + entry.HeaderSize + (long)entry.FileSize);
                var gap = index + 1 < raws.Count
                    ? checked(raws[index + 1].Entry.HeaderOffset - end)
                    : checked(sourceLength - end);
                if (gap < 0)
                {
                    declineReason = " UPDATE.APP entries overlap inside the container.";
                    return false;
                }

                gaps[index] = gap;
                layoutBytes = checked(layoutBytes + gap);
            }

            if (layoutBytes > MaximumLayoutBytes)
            {
                declineReason = " UPDATE.APP non-payload layout exceeds the 64 MiB faithful-capture limit; " +
                    "only the canonical rebuild is available for this container.";
                return false;
            }

            gapLengths = gaps;
            return true;
        }
        catch (OverflowException)
        {
            declineReason = " UPDATE.APP entry accounting overflowed the capture bounds.";
            return false;
        }
    }

    private static string ReadInlinePayload(string sourcePath, UpdateAppEntry entry, CancellationToken cancellationToken)
    {
        using var source = File.OpenRead(sourcePath);
        source.Position = entry.DataOffset;
        var buffer = new byte[entry.FileSize];
        source.ReadExactly(buffer);
        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToBase64String(buffer);
    }

    private static byte[] WriteLayoutArtifact(string sourcePath, List<(UpdateAppEntry Entry, byte[] Header)> raws,
        long[] gapLengths, string artifactPath, CancellationToken cancellationToken)
    {
        byte[] digest = [];
        FileExtraction.WriteAtomic(artifactPath, output =>
        {
            using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                CopyBufferSize, FileOptions.SequentialScan);
            CopyStream(source, output, raws[0].Entry.HeaderOffset, cancellationToken);
            for (var index = 0; index < raws.Count; index++)
            {
                var (entry, _) = raws[index];
                source.Position = checked(entry.HeaderOffset + entry.HeaderSize + (long)entry.FileSize);
                CopyStream(source, output, gapLengths[index], cancellationToken);
            }

            output.Position = 0;
            digest = SHA256.HashData(output);
        }, cancellationToken: cancellationToken);
        return digest;
    }

    private static byte[] DecodeHeader(JsonNode? node)
    {
        var encoded = node?.GetValue<string>() ?? throw new InvalidDataException("Missing UPDATE.APP header metadata.");
        if (encoded.Length > (MaximumHeaderBytes + 2) / 3 * 4 + 4)
            throw new InvalidDataException("UPDATE.APP header metadata is oversized.");
        var header = Convert.FromBase64String(encoded);
        if (header.Length is < 76 or > MaximumHeaderBytes)
            throw new InvalidDataException("UPDATE.APP header length is out of bounds.");
        if (!header.AsSpan(0, 4).SequenceEqual(UpdateAppProcessor.Magic))
            throw new InvalidDataException("UPDATE.APP header lost its entry signature.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) != (uint)header.Length)
            throw new InvalidDataException("UPDATE.APP header size does not match its captured bytes.");
        return header;
    }

    private static void VerifyPayload(byte[] header, string? payloadPath, byte[]? inline, CancellationToken cancellationToken)
    {
        var fileSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24));
        if (header.Length < CanonicalHeaderSize)
            return;
        var blockSize = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(BlockSizeOffset));
        if (blockSize == 0)
            return;

        var tableBytes = checked(header.Length - CanonicalHeaderSize);
        if ((tableBytes & 1) != 0)
            throw new InvalidDataException("UPDATE.APP checksum table has an odd byte length.");
        var blockCount = fileSize == 0 ? 0 : checked((int)(((ulong)fileSize + blockSize - 1) / blockSize));
        if (tableBytes / 2 != blockCount)
            throw new InvalidDataException("UPDATE.APP checksum table length does not match the payload.");

        var expectedHeaderCrc = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(HeaderChecksumOffset));
        var scratch = (byte[])header.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(scratch.AsSpan(HeaderChecksumOffset), 0);
        if (ComputeCrc16(scratch) != expectedHeaderCrc)
            throw new InvalidDataException("UPDATE.APP header CRC mismatch in captured metadata.");

        using var stream = payloadPath is not null
            ? (Stream)new FileStream(payloadPath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.SequentialScan)
            : new MemoryStream(inline ?? new byte[(int)fileSize]);
        if (stream.Length != fileSize)
            throw new InvalidDataException("UPDATE.APP payload file changed size; run the canonical rebuild instead.");
        var buffer = new byte[Math.Min(blockSize, CopyBufferSize)];
        for (var block = 0; block < blockCount; block++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length, (long)fileSize - (long)block * blockSize);
            stream.Position = (long)block * blockSize;
            var read = 0;
            while (read < count)
            {
                var step = stream.Read(buffer, read, count - read);
                if (step == 0)
                    throw new InvalidDataException("UPDATE.APP payload ended unexpectedly.");
                read += step;
            }

            var expected = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(CanonicalHeaderSize + block * 2));
            if (ComputeCrc16(buffer.AsSpan(0, count)) != expected)
                throw new InvalidDataException($"UPDATE.APP payload CRC mismatch for block {block}; " +
                    "the stored bytes no longer match the captured checksum table.");
        }
    }

    private static void CopyStream(Stream source, Stream output, long count, CancellationToken cancellationToken)
    {
        if (count == 0)
            return;
        var buffer = new byte[Math.Min(CopyBufferSize, count)];
        var remaining = count;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0)
                throw new EndOfStreamException("UPDATE.APP layout ended unexpectedly.");
            output.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private static ushort ComputeCrc16(ReadOnlySpan<byte> bytes)
    {
        var crc = (ushort)0xFFFF;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (ushort)((crc & 1) != 0 ? (crc >> 1) ^ 0x8408 : crc >> 1);
        }

        return (ushort)(crc ^ 0xFFFF);
    }

    private readonly record struct Plan(byte[] Header, string? PayloadPath, byte[]? Inline, long GapAfter);
}