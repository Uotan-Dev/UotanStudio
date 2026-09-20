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

public sealed class OnePlusOpsFormatHandler(IOnePlusLogger? logger = null) : IFormatHandler, IRepackMetadataProvider
{
    void IRepackMetadataProvider.ConfigureRepackMetadata(RepackMetadata metadata, string sourcePath, string outputDirectory)
    {
        metadata.CanPackDirectory = true;
    }

    private readonly IOnePlusLogger _logger = logger ?? NullLogger.Instance;

    public string FormatName => "OnePlus OPS Firmware";

    public IReadOnlyList<string> FileExtensions => ["ops"];

    /// <summary>
    /// Determines whether the file matches this format.
    /// <para>判断文件是否匹配此格式。</para>
    /// </summary>
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
    /// Wrong keys are rejected on a small probe window instead of a full-image
    /// decryption; only the winning key triggers a full pass.
    /// <para>尝试使用 OnePlusKeyTable 的全部密钥解密分区数据。错误密钥在小试探窗口上
    /// 即被拒绝，不再整镜像解密；只有命中的密钥才做全量解密。</para>
    /// </summary>
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
    /// Legacy path for small payloads: the probe would cover the entire buffer
    /// anyway, so the original full-decrypt flow is kept verbatim.
    /// <para>小数据的原有路径：试探窗口已覆盖整个缓冲，保留原全量解密流程。</para>
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
            // 流式来源（dataFiles/source）下 Data 为空，长度取 Size；内存路径两者一致。
            // For streamed sources (dataFiles/source) Data is empty, so Size carries
            // the length; the in-memory path keeps both in sync.
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

        // 试探起点按 256 字节密钥周期对齐，保证与全量解密在同一周期相位上。
        // Align the probe start to the 256-byte key period so the probe sees the
        // same phase as a full decryption.
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