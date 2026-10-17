using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.Core;
using UotanStudio.Hash.Digests;
using UotanStudio.Hash.RockchipCrc32;

namespace UotanStudio.Firmware.VendorFirmware.Rockchip;

/// <summary>
/// Writes bounded, vendor-compatible RKAF and RKFW containers.
/// <para>写出受大小上限约束、兼容厂商的 RKAF 与 RKFW 容器。</para>
/// </summary>
public static class RockchipPacker
{
    private const int RkafHeaderSize = 2048;
    private const int RkafDescriptorOffset = 140;
    private const int RkafDescriptorSize = 112;
    private const int MaximumRkafEntries = 16;
    private const int RkafAlignment = 2048;
    private const int MaximumPackageFileBytes = 4 * 1024 * 1024;

    private static readonly byte[] ZeroChunk = new byte[64 * 1024];

    /// <summary>
    /// Packs the source directory into an RKAF or RKFW container using default options.
    /// <para>使用默认选项将源目录打包为 RKAF 或 RKFW 容器。</para>
    /// </summary>
    /// <param name="sourceDirectory">
    /// Directory holding the files to pack.
    /// <para>存放待打包文件的目录。</para>
    /// </param>
    /// <param name="outputPath">
    /// Destination container path; the result is staged in a temporary file and moved into place only after a
    /// complete write, so a failed run keeps any pre-existing output.
    /// <para>目标容器路径；结果先写入临时文件，完整写出后才移动到目标位置，失败的打包会保留已有输出。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the packing; cancellation propagates as <see cref="OperationCanceledException"/>.
    /// <para>取消打包；取消时抛出 <see cref="OperationCanceledException"/>。</para>
    /// </param>
    /// <returns>
    /// A task that completes once the container is fully written.
    /// <para>容器完整写出后完成的任务。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="sourceDirectory"/> or <paramref name="outputPath"/> is <see langword="null"/>.
    /// <para><paramref name="sourceDirectory"/> 或 <paramref name="outputPath"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="sourceDirectory"/> or <paramref name="outputPath"/> is blank.
    /// <para><paramref name="sourceDirectory"/> 或 <paramref name="outputPath"/> 为空白。</para></exception>
    /// <exception cref="DirectoryNotFoundException"><paramref name="sourceDirectory"/> does not exist.
    /// <para><paramref name="sourceDirectory"/> 不存在。</para></exception>
    public static Task PackAsync(
        string sourceDirectory,
        string outputPath,
        CancellationToken cancellationToken = default)
        => PackAsync(sourceDirectory, outputPath, new RockchipPackOptions(), cancellationToken);

    /// <summary>
    /// Packs the source directory into a container, selecting RKFW when it contains
    /// <c>embedded-update.img</c> and RKAF otherwise.
    /// <para>将源目录打包为容器：包含 <c>embedded-update.img</c> 时选择 RKFW，否则选择 RKAF。</para>
    /// </summary>
    /// <param name="sourceDirectory">
    /// Directory holding the files to pack; for RKFW it must contain <c>BOOT</c> or <c>LDR</c> plus
    /// <c>embedded-update.img</c>, for RKAF between 1 and 16 entries listed directly or via
    /// <c>package-file</c>.
    /// <para>待打包文件所在目录：RKFW 需包含 <c>BOOT</c> 或 <c>LDR</c> 以及 <c>embedded-update.img</c>；
    /// RKAF 需要 1 到 16 个条目，直接枚举或由 <c>package-file</c> 列出。</para>
    /// </param>
    /// <param name="outputPath">
    /// Destination container path; the result is staged in a temporary file and moved into place only after a
    /// complete write, so a failed run keeps any pre-existing output.
    /// <para>目标容器路径；结果先写入临时文件，完整写出后才移动到目标位置，失败的打包会保留已有输出。</para>
    /// </param>
    /// <param name="options">
    /// Header identity, version, timestamp and template-reuse settings; must not be <see langword="null"/>.
    /// <para>头部标识、版本、时间戳与模板复用设置；不得为 <see langword="null"/>。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the packing; cancellation propagates as <see cref="OperationCanceledException"/>.
    /// <para>取消打包；取消时抛出 <see cref="OperationCanceledException"/>。</para>
    /// </param>
    /// <returns>
    /// A task that completes once the container is fully written and its trailing checksum or MD5 digest is stored.
    /// <para>容器完整写出并保存末尾校验和或 MD5 摘要后完成的任务。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="sourceDirectory"/>, <paramref name="outputPath"/> or <paramref name="options"/> is <see langword="null"/>.
    /// <para><paramref name="sourceDirectory"/>、<paramref name="outputPath"/> 或 <paramref name="options"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="sourceDirectory"/> or <paramref name="outputPath"/> is blank.
    /// <para><paramref name="sourceDirectory"/> 或 <paramref name="outputPath"/> 为空白。</para></exception>
    /// <exception cref="DirectoryNotFoundException"><paramref name="sourceDirectory"/> does not exist.
    /// <para><paramref name="sourceDirectory"/> 不存在。</para></exception>
    /// <exception cref="InvalidDataException">The inputs violate the container limits: missing or wrong loader/image
    /// magic, an unreadable or mismatched header template, more than 16 or zero RKAF entries, a 32-bit field
    /// overflow, an unsupported chip family, or a malformed version, model, manufacturer or entry name.
    /// <para>输入违反容器限制：引导加载器/镜像魔数缺失或错误、头部模板不可读或魔数不符、RKAF 条目数为 0 或超过
    /// 16、32 位字段溢出、不支持的芯片系列，或版本、机型、厂商、条目名称格式错误。</para></exception>
    /// <exception cref="FileNotFoundException">A path referenced by <c>package-file</c> does not exist on disk.
    /// <para><c>package-file</c> 引用的路径在磁盘上不存在。</para></exception>
    /// <exception cref="EndOfStreamException">A source file shrank below its planned length while being copied.
    /// <para>源文件在复制过程中缩小到低于计划长度。</para></exception>
    public static Task PackAsync(
        string sourceDirectory,
        string outputPath,
        RockchipPackOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(options);
        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException(sourceDirectory);
        }

        return File.Exists(Path.Combine(sourceDirectory, "embedded-update.img"))
            ? PackRkfwAsync(sourceDirectory, outputPath, options, cancellationToken)
            : PackRkafAsync(sourceDirectory, outputPath, options, cancellationToken);
    }

    private static Task PackRkfwAsync(
        string sourceDirectory,
        string outputPath,
        RockchipPackOptions options,
        CancellationToken cancellationToken)
        => FileExtraction.WriteAtomicAsync(outputPath, async output =>
        {
            var loaderPath = File.Exists(Path.Combine(sourceDirectory, "BOOT"))
                ? Path.Combine(sourceDirectory, "BOOT")
                : Path.Combine(sourceDirectory, "LDR");
            var imagePath = Path.Combine(sourceDirectory, "embedded-update.img");
            if (!File.Exists(loaderPath) || !File.Exists(imagePath))
            {
                throw new InvalidDataException("RKFW requires BOOT or LDR and embedded-update.img.");
            }

            await ValidateMagicAsync(loaderPath, ["BOOT"u8.ToArray(), "LDR "u8.ToArray()], "RKFW loader", cancellationToken)
                .ConfigureAwait(false);
            await ValidateMagicAsync(imagePath, ["RKAF"u8.ToArray()], "embedded RKAF", cancellationToken)
                .ConfigureAwait(false);

            var header = options.ReuseHeaderTemplate
                ? await ReadTemplateAsync(Path.Combine(sourceDirectory, "rkfw-header.bin"), RkFwHeader.StructSize,
                    RkFwHeader.MagicRkfw, cancellationToken).ConfigureAwait(false)
                : null;
            header ??= new byte[RkFwHeader.StructSize];
            "RKFW"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), RkFwHeader.StructSize);
            ApplyRkfwMetadata(header, options, hasTemplate: File.Exists(Path.Combine(sourceDirectory, "rkfw-header.bin")) &&
                options.ReuseHeaderTemplate);

            var loaderLength = new FileInfo(loaderPath).Length;
            var imageLength = new FileInfo(imagePath).Length;
            if (loaderLength > uint.MaxValue)
            {
                throw new InvalidDataException("RKFW loader is too large.");
            }
            var imageOffset = checked(RkFwHeader.StructSize + loaderLength);
            if (imageOffset > uint.MaxValue)
            {
                throw new InvalidDataException("RKFW embedded image offset exceeds the 32-bit field.");
            }

            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(25), RkFwHeader.StructSize);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(29), checked((uint)loaderLength));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(33), checked((uint)imageOffset));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(37), unchecked((uint)imageLength));

            using var md5 = Md5.CreateAccumulator();
            await WriteAndHashAsync(output, header, md5, cancellationToken).ConfigureAwait(false);
            await CopyFileAsync(loaderPath, output, loaderLength, md5, crcState: null, cancellationToken).ConfigureAwait(false);
            await CopyFileAsync(imagePath, output, imageLength, md5, crcState: null, cancellationToken).ConfigureAwait(false);
            var digest = Encoding.ASCII.GetBytes(Convert.ToHexStringLower(md5.GetHashAndReset()));
            await output.WriteAsync(digest, cancellationToken).ConfigureAwait(false);
        }, cancellationToken: cancellationToken);

    private static Task PackRkafAsync(
        string sourceDirectory,
        string outputPath,
        RockchipPackOptions options,
        CancellationToken cancellationToken)
        => FileExtraction.WriteAtomicAsync(outputPath, async output =>
        {
            var template = options.ReuseHeaderTemplate
                ? await ReadTemplateAsync(Path.Combine(sourceDirectory, "rkaf-header.bin"), RkafHeaderSize,
                    RkFwHeader.MagicRkaf, cancellationToken).ConfigureAwait(false)
                : null;
            var header = template ?? new byte[RkafHeaderSize];
            "RKAF"u8.CopyTo(header);
            if (template is null)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(132), 0x01000000);
            }
            WriteIdentity(header.AsSpan(8, 34), options.Model ?? ReadCString(header.AsSpan(8, 34)) ?? "UotanStudio", "model");
            WriteIdentity(header.AsSpan(72, 56), options.Manufacturer ?? ReadCString(header.AsSpan(72, 56)) ?? "UotanStudio",
                "manufacturer");

            var sources = ReadRkafSources(sourceDirectory, template);
            if (sources.Count == 0 || sources.Count > MaximumRkafEntries)
            {
                throw new InvalidDataException($"RKAF requires between 1 and {MaximumRkafEntries} entries.");
            }

            var position = (long)RkafHeaderSize;
            var planned = new List<RkafPlannedEntry>(sources.Count);
            for (var index = 0; index < sources.Count; index++)
            {
                var source = sources[index];
                var rawLength = new FileInfo(source.FullPath).Length;
                var length = source.Name.Equals("parameter", StringComparison.Ordinal) ? checked(rawLength + 12) : rawLength;
                if (length > uint.MaxValue)
                {
                    throw new InvalidDataException($"RKAF entry '{source.RelativePath}' exceeds the supported size.");
                }
                position = Align(position, RkafAlignment);
                planned.Add(new RkafPlannedEntry(source, position, length, rawLength));
                position = checked(position + Align(length, RkafAlignment));
            }

            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), unchecked((uint)position));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(136), checked((uint)planned.Count));
            for (var index = 0; index < MaximumRkafEntries; index++)
            {
                var descriptor = header.AsSpan(RkafDescriptorOffset + index * RkafDescriptorSize, RkafDescriptorSize);
                if (index >= planned.Count)
                {
                    if (template is null)
                    {
                        descriptor.Clear();
                    }
                    continue;
                }

                var entry = planned[index];
                var templateDescriptor = FindTemplateDescriptor(template, entry.Source.RelativePath);
                if (templateDescriptor is not null)
                {
                    templateDescriptor.CopyTo(descriptor);
                }
                else
                {
                    descriptor.Clear();
                }
                WriteIdentity(descriptor[..32], entry.Source.Name, "entry name");
                WriteIdentity(descriptor.Slice(32, 60), entry.Source.RelativePath, "entry path");
                BinaryPrimitives.WriteUInt32LittleEndian(descriptor[96..], checked((uint)entry.Offset));
                BinaryPrimitives.WriteUInt32LittleEndian(descriptor[104..], checked((uint)(Align(entry.Length, RkafAlignment) / RkafAlignment)));
                BinaryPrimitives.WriteUInt32LittleEndian(descriptor[108..], checked((uint)entry.Length));
            }

            var crc = new RkCrc32State();
            await WriteAndCrcAsync(output, header, crc, cancellationToken).ConfigureAwait(false);
            foreach (var entry in planned)
            {
                if (output.Position < entry.Offset)
                {
                    await WriteZerosAsync(output, entry.Offset - output.Position, crc, cancellationToken).ConfigureAwait(false);
                }

                if (entry.Source.Name.Equals("parameter", StringComparison.Ordinal))
                {
                    var prefix = new byte[8];
                    "PARM"u8.CopyTo(prefix);
                    BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(4), checked((uint)entry.RawLength));
                    await WriteAndCrcAsync(output, prefix, crc, cancellationToken).ConfigureAwait(false);
                    var parameterCrc = new RkCrc32State();
                    await CopyFileAsync(entry.Source.FullPath, output, entry.RawLength, hash: null, crc,
                        cancellationToken, parameterCrc).ConfigureAwait(false);
                    var suffix = new byte[4];
                    BinaryPrimitives.WriteUInt32LittleEndian(suffix, parameterCrc.Value);
                    await WriteAndCrcAsync(output, suffix, crc, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await CopyFileAsync(entry.Source.FullPath, output, entry.RawLength, hash: null, crc, cancellationToken)
                        .ConfigureAwait(false);
                }

                var end = checked(entry.Offset + Align(entry.Length, RkafAlignment));
                await WriteZerosAsync(output, end - output.Position, crc, cancellationToken).ConfigureAwait(false);
            }

            var checksum = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(checksum, crc.Value);
            await output.WriteAsync(checksum, cancellationToken).ConfigureAwait(false);
        }, cancellationToken: cancellationToken);

    private static List<RkafSourceEntry> ReadRkafSources(string root, byte[]? template)
    {
        var packagePath = Path.Combine(root, "package-file");
        if (File.Exists(packagePath))
        {
            if (new FileInfo(packagePath).Length > MaximumPackageFileBytes)
            {
                throw new InvalidDataException("Rockchip package-file exceeds the size limit.");
            }

            var entries = new List<RkafSourceEntry>();
            foreach (var line in File.ReadLines(packagePath).Take(MaximumRkafEntries + 1))
            {
                var fields = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 2 || fields[0].StartsWith('#') || fields[1].Equals("RESERVED", StringComparison.Ordinal))
                {
                    continue;
                }
                if (entries.Count == MaximumRkafEntries)
                {
                    throw new InvalidDataException("Rockchip package-file contains too many entries.");
                }
                var relative = ResolveTemplatePath(template, fields[0], fields[1]);
                var fullPath = FileExtraction.ResolvePath(root, relative);
                if (!File.Exists(fullPath))
                {
                    fullPath = FileExtraction.ResolvePath(root, fields[1]);
                }
                if (!File.Exists(fullPath))
                {
                    throw new FileNotFoundException($"RKAF source entry was not found: {relative}", fullPath);
                }
                entries.Add(new RkafSourceEntry(fields[0], relative.Replace('\\', '/'), fullPath));
            }
            return entries;
        }

        return [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => new { Path = path, Relative = NormalizeRelativePath(root, path) })
            .Where(file => !IsHelperFile(file.Relative))
            .OrderBy(file => file.Relative, StringComparer.Ordinal)
            .Select(file => new RkafSourceEntry(
                file.Relative.Equals("parameter.txt", StringComparison.OrdinalIgnoreCase)
                    ? "parameter"
                    : Path.GetFileNameWithoutExtension(file.Relative),
                file.Relative,
                file.Path))];
    }

    private static string ResolveTemplatePath(byte[]? template, string name, string fallback)
    {
        if (template is null)
        {
            return fallback;
        }
        var count = Math.Min(MaximumRkafEntries, checked((int)BinaryPrimitives.ReadUInt32LittleEndian(template.AsSpan(136))));
        for (var index = 0; index < count; index++)
        {
            var descriptor = template.AsSpan(RkafDescriptorOffset + index * RkafDescriptorSize, RkafDescriptorSize);
            if (ReadCString(descriptor[..32])?.Equals(name, StringComparison.Ordinal) == true)
            {
                return ReadCString(descriptor.Slice(32, 60)) ?? fallback;
            }
        }
        return fallback;
    }

    private static byte[]? FindTemplateDescriptor(byte[]? template, string relativePath)
    {
        if (template is null)
        {
            return null;
        }
        var count = Math.Min(MaximumRkafEntries, checked((int)BinaryPrimitives.ReadUInt32LittleEndian(template.AsSpan(136))));
        for (var index = 0; index < count; index++)
        {
            var descriptor = template.AsSpan(RkafDescriptorOffset + index * RkafDescriptorSize, RkafDescriptorSize);
            if (ReadCString(descriptor.Slice(32, 60))?.Equals(relativePath, StringComparison.Ordinal) == true)
            {
                return descriptor.ToArray();
            }
        }
        return null;
    }

    private static void ApplyRkfwMetadata(byte[] header, RockchipPackOptions options, bool hasTemplate)
    {
        var version = options.Version ?? (hasTemplate ? null : "1.0.0");
        if (version is not null)
        {
            var parts = version.Split('.');
            if (parts.Length != 3 || !byte.TryParse(parts[0], out var major) || !byte.TryParse(parts[1], out var minor) ||
                !ushort.TryParse(parts[2], out var build))
            {
                throw new InvalidDataException("RKFW version must use major.minor.build with byte, byte and ushort values.");
            }
            header[6] = (byte)build;
            header[7] = (byte)(build >> 8);
            header[8] = minor;
            header[9] = major;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(10), options.Code ??
            (hasTemplate ? BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(10)) : 0x02000000));
        var timestamp = options.Timestamp ?? (hasTemplate ? null : DateTimeOffset.UnixEpoch);
        if (timestamp is not null)
        {
            var utc = timestamp.Value.UtcDateTime;
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), checked((ushort)utc.Year));
            header[16] = checked((byte)utc.Month);
            header[17] = checked((byte)utc.Day);
            header[18] = checked((byte)utc.Hour);
            header[19] = checked((byte)utc.Minute);
            header[20] = checked((byte)utc.Second);
        }

        if (options.Chip is not null || !hasTemplate)
        {
            EncodeChip(options.Chip ?? "RK3566").CopyTo(header, 21);
        }
        if (!hasTemplate)
        {
            header[0x2D] = 1;
        }
    }

    private static byte[] EncodeChip(string chip)
    {
        var normalized = chip.ToUpperInvariant();
        var family = normalized switch
        {
            "RV1109" or "RV1126" => 0x19,
            "PX30" => 0x30,
            "RK3562" => 0x32,
            "RK3399" or "RK3399PRO" => 0x33,
            "RK3588" or "RK3588S" => 0x35,
            "RK3326" => 0x36,
            "RK3566" or "RK3568" => 0x38,
            "RK3528" => 0x39,
            "RK3368" => 0x41,
            "RK3308" => 0x48,
            "RK29XX" => 0x50,
            "RV1108" => 0x51,
            "RK3066" or "RK30XX" => 0x60,
            "RK3188" or "RK31XX" => 0x70,
            "RK3288" or "RK32XX" => 0x80,
            _ => throw new InvalidDataException($"Unsupported Rockchip chip family: {chip}.")
        };
        var digits = normalized.Where(char.IsAsciiDigit).Select(character => checked((byte)character)).ToArray();
        if (family is not (0x50 or 0x51 or 0x60 or 0x70 or 0x80) && digits.Length == 4)
        {
            return [digits[3], digits[2], digits[1], digits[0]];
        }
        return digits.Length >= 3 ? [checked((byte)family), digits[^1], digits[^2], digits[^3]] : [checked((byte)family), 0, 0, 0];
    }

    private static async Task<byte[]?> ReadTemplateAsync(
        string path,
        int size,
        uint magic,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length < size)
        {
            throw new InvalidDataException($"Rockchip header template is shorter than {size} bytes.");
        }
        var data = new byte[size];
        await input.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt32LittleEndian(data) != magic)
        {
            throw new InvalidDataException("Rockchip header template has the wrong magic.");
        }
        return data;
    }

    private static async Task ValidateMagicAsync(
        string path,
        IReadOnlyList<byte[]> accepted,
        string description,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var magic = new byte[4];
        await input.ReadExactlyAsync(magic, cancellationToken).ConfigureAwait(false);
        if (!accepted.Any(value => value.AsSpan().SequenceEqual(magic)))
        {
            throw new InvalidDataException($"Invalid {description} magic.");
        }
    }

    private static readonly SearchValues<char> ForbiddenIdentityChars = SearchValues.Create(['\0', '\r', '\n']);

    private static void WriteIdentity(Span<byte> field, string value, string description)
    {
        var length = Encoding.UTF8.GetByteCount(value);
        if (string.IsNullOrEmpty(value) || value.AsSpan().ContainsAny(ForbiddenIdentityChars) || length >= field.Length)
        {
            throw new InvalidDataException($"Rockchip {description} is invalid or too long: '{value}'.");
        }
        Encoding.UTF8.GetBytes(value, field);
        field[length] = 0;
    }

    private static string? ReadCString(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        var value = Encoding.UTF8.GetString(end < 0 ? field : field[..end]);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static bool IsHelperFile(string relativePath)
        => relativePath.Equals("rkaf-header.bin", StringComparison.OrdinalIgnoreCase) ||
           relativePath.Equals("rkfw-header.bin", StringComparison.OrdinalIgnoreCase) ||
           relativePath.Equals("partition-metadata.txt", StringComparison.OrdinalIgnoreCase) ||
           relativePath.StartsWith(".uotan/", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeRelativePath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
        _ = FileExtraction.ResolvePath(root, relative);
        return relative;
    }

    private static long Align(long value, int alignment) => checked((value + alignment - 1) / alignment * alignment);

    private static async Task CopyFileAsync(
        string path,
        FileStream output,
        long expectedLength,
        DigestAccumulator? hash,
        RkCrc32State? crcState,
        CancellationToken cancellationToken,
        RkCrc32State? secondaryCrc = null)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[128 * 1024];
        var remaining = expectedLength;
        while (remaining > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException($"Rockchip source file changed size while packing: {path}");
            }
            var memory = buffer.AsMemory(0, read);
            hash?.Append(memory.Span);
            crcState?.Append(memory.Span);
            secondaryCrc?.Append(memory.Span);
            await output.WriteAsync(memory, cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }
        if (await input.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0)
        {
            throw new IOException($"Rockchip source file changed size while packing: {path}");
        }
    }

    private static async Task WriteAndHashAsync(
        FileStream output,
        byte[] data,
        DigestAccumulator hash,
        CancellationToken cancellationToken)
    {
        hash.Append(data);
        await output.WriteAsync(data, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteAndCrcAsync(
        FileStream output,
        byte[] data,
        RkCrc32State crc,
        CancellationToken cancellationToken)
    {
        crc.Append(data);
        await output.WriteAsync(data, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteZerosAsync(
        FileStream output,
        long count,
        RkCrc32State crc,
        CancellationToken cancellationToken)
    {
        if (count < 0)
        {
            throw new InvalidDataException("Negative Rockchip padding.");
        }
        while (count > 0)
        {
            var length = (int)Math.Min(ZeroChunk.Length, count);
            var memory = ZeroChunk.AsMemory(0, length);
            crc.Append(memory.Span);
            await output.WriteAsync(memory, cancellationToken).ConfigureAwait(false);
            count -= length;
        }
    }

    private sealed class RkCrc32State
    {
        public uint Value { get; private set; }

        public void Append(ReadOnlySpan<byte> data) => Value = RockchipCrc32.Compute(data, Value);
    }

    private sealed record RkafSourceEntry(string Name, string RelativePath, string FullPath);
    private sealed record RkafPlannedEntry(RkafSourceEntry Source, long Offset, long Length, long RawLength);
}