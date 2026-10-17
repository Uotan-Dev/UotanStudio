using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.AndroidMeta.Apk.Signing;
using UotanStudio.Firmware.Core.IO;
using UotanStudio.Hash.Crc32Ieee;

namespace UotanStudio.Firmware.AndroidMeta.Apk;

/// <summary>
/// Rewrites an APK's ZIP layer by parsing the central directory directly: drops stale v1 signature entries and appends new STORED entries, preserving all other entries byte-for-byte.
/// <para>直接解析中央目录重写 APK 的 ZIP 层：剔除旧 v1 签名条目并追加新的 STORED 条目，其余条目逐字节保留。</para>
/// </summary>
public static class ApkEntryStripper
{
    private const uint LocalHeaderSignature = 0x04034b50;
    private const uint CentralHeaderSignature = 0x02014b50;
    private const uint DataDescriptorSignature = 0x08074b50;
    private const ushort Utf8Flag = 0x0800;
    private const ushort DataDescriptorFlag = 0x0008;
    private const long MaximumCentralDirectoryBytes = 256L * 1024 * 1024;
    private const int CopyBufferBytes = 81920;
    private const int MaximumAlignmentPadding = 16384;

    /// <summary>
    /// Describes one rewrite pass over an APK's ZIP layer.
    /// <para>描述对 APK ZIP 层的一次重写。</para>
    /// </summary>
    public sealed class RewriteRequest
    {
        /// <summary>Drops META-INF v1 signature entries.<para>剔除 META-INF 的 v1 签名条目。</para></summary>
        /// <value>When <see langword="true"/> matched entries and their central records are omitted from the output. <para>为 <see langword="true"/> 时命中条目及其中央记录不写入输出。</para></value>
        public bool StripV1SignatureEntries { get; init; }

        /// <summary>Stored entries to append after the existing ones.<para>追加在既有条目之后的 STORED 条目。</para></summary>
        /// <value>Name and content pairs written in enumeration order as method-0 STORED entries with UTF-8 names. <para>按枚举顺序以 method 0 STORED、UTF-8 文件名写入的名称与内容对。</para></value>
        public IReadOnlyList<(string Name, byte[] Content)> EntriesToAppend { get; init; } = [];
    }

    /// <summary>
    /// Rewrites the input APK into the output path (atomic replace) per the request.
    /// <para>按请求把输入 APK 重写到输出路径（原子替换）。</para>
    /// </summary>
    /// <param name="inputApk">Existing APK to read; opened for reading and closed before returning. <para>待读取的已存在 APK；以读方式打开并在返回前关闭。</para></param>
    /// <param name="outputApk">Destination path; an existing file is replaced only after the rewrite succeeds. <para>目标路径；仅在重写成功后替换既有文件。</para></param>
    /// <param name="request">Rewrite request describing stripping and appended entries; must not be <see langword="null"/>. <para>描述剔除与追加条目的重写请求；不得为 null。</para></param>
    /// <param name="cancellationToken">Observed between entry copies and appended writes; cancellation leaves the existing output untouched. <para>在条目复制与追加写入之间生效；取消时保持既有输出不动。</para></param>
    /// <exception cref="FileNotFoundException"><paramref name="inputApk"/> does not exist. <para><paramref name="inputApk"/> 不存在。</para></exception>
    /// <exception cref="InvalidDataException">The central directory is corrupt or exceeds 256 MiB, or a local header signature is missing. <para>中央目录损坏或超过 256 MiB，或本地文件头签名缺失。</para></exception>
    /// <exception cref="EndOfStreamException">An entry's declared range runs past the end of the input. <para>条目声明的范围超出输入末尾。</para></exception>
    /// <exception cref="OverflowException">The rewritten archive exceeds the 4 GiB offset addressable by 32-bit ZIP records. <para>重写后的包超出 32 位 ZIP 可寻址的 4 GiB 偏移。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled. <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    /// <exception cref="IOException">The output or its temporary file cannot be written. <para>输出或其临时文件无法写入。</para></exception>
    public static void Rewrite(string inputApk, string outputApk, RewriteRequest request, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(inputApk))
        {
            throw new FileNotFoundException($"APK file not found: {inputApk}. 未找到 APK 文件：{inputApk}", inputApk);
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputApk));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = TempFileHelper.CreateAdjacentPath(outputApk);
        try
        {
            using var input = new FileStream(inputApk, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferBytes, FileOptions.SequentialScan);
            using var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferBytes, FileOptions.SequentialScan);
            RewriteCore(input, output, request, cancellationToken, signing: null);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { }
            throw;
        }

        AtomicPublish.Publish(tempPath, outputApk);
    }

    /// <summary>
    /// Rewrites the input APK and inserts an APK Signing Block before the central directory in the
    /// same pass: the v2/v3 content digest is folded from the bytes actually written, so the output
    /// never has to be read back.
    /// <para>在同一次重写中把 APK 签名分块插入中央目录之前：v2/v3 内容摘要由实际写出的字节累计，
    /// 因此无需回读输出。</para>
    /// </summary>
    /// <param name="inputApk">Existing APK to read. <para>待读取的已存在 APK。</para></param>
    /// <param name="outputApk">Destination path replaced atomically on success. <para>成功后原子替换的目标路径。</para></param>
    /// <param name="request">Rewrite request describing stripping and appended entries.
    /// <para>描述剔除与追加条目的重写请求。</para></param>
    /// <param name="hashName">Hash algorithm name of the content digest ("SHA256"/"SHA512").
    /// <para>内容摘要的哈希算法名称（"SHA256"/"SHA512"）。</para></param>
    /// <param name="signingBlockFactory">Builds the complete signing block from the computed content digest.
    /// <para>由已计算的内容摘要构建完整签名分块。</para></param>
    /// <param name="cancellationToken">Observed between entry copies and appended writes.
    /// <para>在条目复制与追加写入之间生效。</para></param>
    /// <exception cref="InvalidDataException">The central directory is corrupt or exceeds 256 MiB, a local
    /// header signature is missing, or the archive exceeds the 32-bit ZIP limits.
    /// <para>中央目录损坏或超过 256 MiB、本地文件头签名缺失，或归档超出 32 位 ZIP 上限。</para></exception>
    /// <exception cref="OverflowException">A signing block shifts the central directory past 4 GiB.
    /// <para>签名分块把中央目录推移到 4 GiB 之外。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    internal static void RewriteWithSigningBlock(
        string inputApk,
        string outputApk,
        RewriteRequest request,
        string hashName,
        Func<byte[], byte[]> signingBlockFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signingBlockFactory);
        if (!File.Exists(inputApk))
        {
            throw new FileNotFoundException($"APK file not found: {inputApk}. 未找到 APK 文件：{inputApk}", inputApk);
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputApk));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = TempFileHelper.CreateAdjacentPath(outputApk);
        try
        {
            using var input = new FileStream(inputApk, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferBytes, FileOptions.SequentialScan);
            using var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferBytes, FileOptions.SequentialScan);
            using var digest = new ApkContentDigestBuilder(hashName);
            RewriteCore(input, output, request, cancellationToken, new SigningInsertion(digest, signingBlockFactory));
        }
        catch
        {
            try { File.Delete(tempPath); } catch { }
            throw;
        }

        AtomicPublish.Publish(tempPath, outputApk);
    }

    private static void RewriteCore(
        Stream input,
        Stream output,
        RewriteRequest request,
        CancellationToken cancellationToken,
        SigningInsertion? signing)
    {
        var sections = ApkZipSections.Locate(input);
        if (sections.CentralDirectorySize > MaximumCentralDirectoryBytes)
        {
            throw new InvalidDataException("APK central directory exceeds the supported size. APK 中央目录超出支持的大小。");
        }

        input.Seek(sections.CentralDirectoryOffset, SeekOrigin.Begin);
        var centralDirectory = new byte[sections.CentralDirectorySize];
        input.ReadExactly(centralDirectory);

        var records = new List<CentralRecord>();
        var offset = 0;
        while (offset + 46 <= centralDirectory.Length)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(centralDirectory.AsSpan(offset, 4)) != CentralHeaderSignature)
            {
                throw new InvalidDataException("Corrupt APK central directory. APK 中央目录损坏。");
            }

            var record = new CentralRecord
            {
                RecordStart = offset,
                Flags = BinaryPrimitives.ReadUInt16LittleEndian(centralDirectory.AsSpan(offset + 8, 2)),
                Method = BinaryPrimitives.ReadUInt16LittleEndian(centralDirectory.AsSpan(offset + 10, 2)),
                Crc = BinaryPrimitives.ReadUInt32LittleEndian(centralDirectory.AsSpan(offset + 16, 4)),
                CompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(centralDirectory.AsSpan(offset + 20, 4)),
                UncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(centralDirectory.AsSpan(offset + 24, 4)),
                NameLength = BinaryPrimitives.ReadUInt16LittleEndian(centralDirectory.AsSpan(offset + 28, 2)),
                ExtraLength = BinaryPrimitives.ReadUInt16LittleEndian(centralDirectory.AsSpan(offset + 30, 2)),
                CommentLength = BinaryPrimitives.ReadUInt16LittleEndian(centralDirectory.AsSpan(offset + 32, 2)),
                LocalHeaderOffset = BinaryPrimitives.ReadUInt32LittleEndian(centralDirectory.AsSpan(offset + 42, 4)),
            };
            var recordLength = 46 + record.NameLength + record.ExtraLength + record.CommentLength;
            if (offset + recordLength > centralDirectory.Length)
            {
                throw new InvalidDataException("Corrupt APK central directory. APK 中央目录损坏。");
            }

            record.Name = Encoding.UTF8.GetString(centralDirectory.AsSpan(offset + 46, record.NameLength));
            records.Add(record);
            offset += recordLength;
        }

        var kept = new List<CentralRecord>();
        foreach (var record in records)
        {
            if (!request.StripV1SignatureEntries || !IsV1SignatureEntry(record.Name))
            {
                kept.Add(record);
            }
        }

        kept.Sort(static (a, b) => a.LocalHeaderOffset.CompareTo(b.LocalHeaderOffset));

        var scratch = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        var paddingZeros = ArrayPool<byte>.Shared.Rent(MaximumAlignmentPadding);
        paddingZeros.AsSpan().Clear();
        try
        {
            var newOffsets = new Dictionary<CentralRecord, uint>(kept.Count);
            Span<byte> lengths = stackalloc byte[4];
            foreach (var record in kept)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (record.Method == 0)
                {
                    input.Position = record.LocalHeaderOffset + 26;
                    input.ReadExactly(lengths);
                    var headerLength = 30 + BinaryPrimitives.ReadUInt16LittleEndian(lengths)
                        + BinaryPrimitives.ReadUInt16LittleEndian(lengths[2..]);
                    var oldDataOffset = record.LocalHeaderOffset + headerLength;
                    var alignment = record.Name.EndsWith(".so", StringComparison.Ordinal) ? 16384 : 4;
                    while (alignment > 1 && oldDataOffset % alignment != 0) alignment /= 2;
                    var padding = (int)((alignment - (output.Position + headerLength) % alignment) % alignment);
                    if (padding > 0)
                    {
                        WriteAndDigest(output, paddingZeros.AsSpan(0, padding), signing?.Digest);
                    }
                }
                newOffsets[record] = checked((uint)output.Position);
                CopyRawEntry(input, output, record, cancellationToken, scratch, signing?.Digest);
            }

            var appended = new List<byte[]>();
            foreach (var (name, content) in request.EntriesToAppend)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var record = AppendStoredEntry(output, name, content, signing?.Digest);
                appended.Add(BuildCentralRecord(record.name, record.crc, (uint)content.Length, (uint)content.Length, record.offset));
            }

            var centralDirectoryStart = output.Position;
            var centralDirectorySize = SumCentralDirectoryBytes(records, request, appended);
            if (centralDirectoryStart > uint.MaxValue || centralDirectorySize > uint.MaxValue)
            {
                throw new InvalidDataException("Rewritten APK exceeds the 32-bit ZIP size limit. 重写后的 APK 超出 32 位 ZIP 大小上限。");
            }

            var entryCount = kept.Count + appended.Count;
            if (signing is null)
            {
                WriteCentralDirectory(output, records, request, newOffsets, centralDirectory, appended);
                output.Write(BuildEocd(entryCount, centralDirectorySize, centralDirectoryStart));
            }
            else
            {
                using var centralDirectoryBuffer = new MemoryStream(checked((int)centralDirectorySize));
                WriteCentralDirectory(centralDirectoryBuffer, records, request, newOffsets, centralDirectory, appended);
                var centralDirectoryBytes = centralDirectoryBuffer.GetBuffer().AsSpan(0, (int)centralDirectoryBuffer.Length);

                signing.Digest.BeginSection();
                signing.Digest.Append(centralDirectoryBytes);
                signing.Digest.BeginSection();
                signing.Digest.Append(BuildEocd(entryCount, centralDirectorySize, centralDirectoryStart));
                var block = signing.BuildBlock(signing.Digest.Complete());

                var signedCentralDirectoryOffset = checked((uint)(centralDirectoryStart + block.Length));
                output.Write(block);
                output.Write(centralDirectoryBytes);
                output.Write(BuildEocd(entryCount, centralDirectorySize, signedCentralDirectoryOffset));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
            ArrayPool<byte>.Shared.Return(paddingZeros);
        }
    }

    private static long SumCentralDirectoryBytes(List<CentralRecord> records, RewriteRequest request, List<byte[]> appended)
    {
        long size = 0;
        foreach (var record in records)
        {
            if (request.StripV1SignatureEntries && IsV1SignatureEntry(record.Name))
            {
                continue;
            }

            size += 46 + record.NameLength + record.ExtraLength + record.CommentLength;
        }

        foreach (var record in appended)
        {
            size += record.Length;
        }

        return size;
    }

    private static void WriteCentralDirectory(
        Stream output,
        List<CentralRecord> records,
        RewriteRequest request,
        Dictionary<CentralRecord, uint> newOffsets,
        byte[] centralDirectory,
        List<byte[]> appended)
    {
        Span<byte> patchedOffset = stackalloc byte[4];
        foreach (var record in records)
        {
            if (request.StripV1SignatureEntries && IsV1SignatureEntry(record.Name))
            {
                continue;
            }

            var recordLength = 46 + record.NameLength + record.ExtraLength + record.CommentLength;
            var stored = centralDirectory.AsSpan(record.RecordStart, recordLength);
            BinaryPrimitives.WriteUInt32LittleEndian(patchedOffset, newOffsets[record]);
            output.Write(stored[..42]);
            output.Write(patchedOffset);
            output.Write(stored[46..]);
        }

        foreach (var appendedRecord in appended)
        {
            output.Write(appendedRecord);
        }
    }

    private static byte[] BuildEocd(int entryCount, long centralDirectorySize, long centralDirectoryOffset)
    {
        var eocd = new byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(eocd, 0x06054b50);
        BinaryPrimitives.WriteUInt16LittleEndian(eocd.AsSpan(4, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(eocd.AsSpan(6, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(eocd.AsSpan(8, 2), (ushort)entryCount);
        BinaryPrimitives.WriteUInt16LittleEndian(eocd.AsSpan(10, 2), (ushort)entryCount);
        BinaryPrimitives.WriteUInt32LittleEndian(eocd.AsSpan(12, 4), checked((uint)centralDirectorySize));
        BinaryPrimitives.WriteUInt32LittleEndian(eocd.AsSpan(16, 4), checked((uint)centralDirectoryOffset));
        BinaryPrimitives.WriteUInt16LittleEndian(eocd.AsSpan(20, 2), 0);
        return eocd;
    }

    private static void CopyRawEntry(
        Stream input,
        Stream output,
        CentralRecord record,
        CancellationToken cancellationToken,
        byte[] scratch,
        ApkContentDigestBuilder? digest)
    {
        input.Seek(record.LocalHeaderOffset, SeekOrigin.Begin);
        Span<byte> header = stackalloc byte[30];
        input.ReadExactly(header);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != LocalHeaderSignature)
        {
            throw new InvalidDataException($"Missing local header signature for '{record.Name}'. 条目 '{record.Name}' 缺少本地文件头签名。");
        }

        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(26, 2));
        var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(28, 2));
        var rawLength = 30L + nameLength + extraLength + record.CompressedSize;
        CopyRange(input, output, record.LocalHeaderOffset, rawLength, cancellationToken, scratch, digest);

        if ((record.Flags & DataDescriptorFlag) != 0)
        {
            Span<byte> peek = stackalloc byte[4];
            var position = input.Position;
            input.ReadExactly(peek);
            input.Seek(position, SeekOrigin.Begin);
            var descriptorLength = BinaryPrimitives.ReadUInt32LittleEndian(peek) == DataDescriptorSignature ? 16 : 12;
            CopyRange(input, output, position, descriptorLength, cancellationToken, scratch, digest);
        }
    }

    private static void CopyRange(
        Stream input,
        Stream output,
        long offset,
        long length,
        CancellationToken cancellationToken,
        byte[] scratch,
        ApkContentDigestBuilder? digest)
    {
        input.Seek(offset, SeekOrigin.Begin);
        var remaining = length;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = input.Read(scratch.AsSpan(0, (int)Math.Min(scratch.Length, remaining)));
            if (read == 0)
            {
                throw new EndOfStreamException("Unexpected EOF while copying APK entries. 复制 APK 条目时流意外结束。");
            }

            WriteAndDigest(output, scratch.AsSpan(0, read), digest);
            remaining -= read;
        }
    }

    private static void WriteAndDigest(Stream output, ReadOnlySpan<byte> data, ApkContentDigestBuilder? digest)
    {
        output.Write(data);
        digest?.Append(data);
    }

    private static (string name, uint crc, uint offset) AppendStoredEntry(
        Stream output, string entryName, byte[] content, ApkContentDigestBuilder? digest)
    {
        var nameBytes = Encoding.UTF8.GetBytes(entryName);
        var localHeaderOffset = checked((uint)output.Position);
        var crc = Crc32Of(content);

        Span<byte> header = stackalloc byte[30];
        BinaryPrimitives.WriteUInt32LittleEndian(header, LocalHeaderSignature);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(4, 2), 20);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(6, 2), Utf8Flag);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(8, 2), 0);
        WriteDosTimestamp(header.Slice(10, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(14, 4), crc);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(18, 4), (uint)content.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(22, 4), (uint)content.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(26, 2), (ushort)nameBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(28, 2), 0);
        WriteAndDigest(output, header, digest);
        WriteAndDigest(output, nameBytes, digest);
        WriteAndDigest(output, content, digest);

        return (entryName, crc, localHeaderOffset);
    }

    private static byte[] BuildCentralRecord(string entryName, uint crc, uint compressedSize, uint uncompressedSize, uint localHeaderOffset)
    {
        var nameBytes = Encoding.UTF8.GetBytes(entryName);
        var record = new byte[46 + nameBytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(record, CentralHeaderSignature);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4, 2), (3 << 8) | 20);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6, 2), 20);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(8, 2), Utf8Flag);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(10, 2), 0);
        WriteDosTimestamp(record.AsSpan(12, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(16, 4), crc);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(20, 4), compressedSize);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(24, 4), uncompressedSize);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(28, 2), (ushort)nameBytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(42, 4), localHeaderOffset);
        nameBytes.CopyTo(record, 46);
        return record;
    }

    private static uint Crc32Of(byte[] content)
    {
        return Crc32Ieee.Compute(content);
    }

    private static void WriteDosTimestamp(Span<byte> target)
    {
        const ushort dosDate = ((2020 - 1980) << 9) | (1 << 5) | 1;
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(0, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(target.Slice(2, 2), dosDate);
    }

    /// <summary>
    /// Determines whether an entry is a stale v1 signature artifact.
    /// <para>判断条目是否为旧 v1 签名工件。</para>
    /// </summary>
    /// <param name="entryName">Archive entry path as stored; the <c>META-INF/</c> prefix and extensions are compared case-insensitively. <para>按存储原样的条目路径；<c>META-INF/</c> 前缀与扩展名比较不区分大小写。</para></param>
    /// <returns><see langword="true"/> for the manifest file and for signature blocks stored under <c>META-INF/</c>.
    /// <para>对清单文件以及存放在 <c>META-INF/</c> 下的签名块为 <see langword="true"/>。</para></returns>
    public static bool IsV1SignatureEntry(string entryName)
    {
        if (!entryName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = entryName["META-INF/".Length..];
        if (name.Equals("MANIFEST.MF", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return name.EndsWith(".SF", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".RSA", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".DSA", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".EC", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class SigningInsertion(ApkContentDigestBuilder digest, Func<byte[], byte[]> buildBlock)
    {
        public ApkContentDigestBuilder Digest { get; } = digest;

        public Func<byte[], byte[]> BuildBlock { get; } = buildBlock;
    }

    private sealed class CentralRecord
    {
        public int RecordStart { get; init; }
        public ushort Flags { get; init; }
        public ushort Method { get; init; }
        public uint Crc { get; init; }
        public uint CompressedSize { get; init; }
        public uint UncompressedSize { get; init; }
        public ushort NameLength { get; init; }
        public ushort ExtraLength { get; init; }
        public ushort CommentLength { get; init; }
        public uint LocalHeaderOffset { get; init; }
        public string Name { get; set; } = string.Empty;
    }
}