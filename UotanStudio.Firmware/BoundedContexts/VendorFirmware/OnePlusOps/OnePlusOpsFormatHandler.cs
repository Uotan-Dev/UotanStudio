using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.OnePlusOps.Logging;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.OnePlusOps;

public sealed class OnePlusOpsFormatHandler(IOnePlusLogger? logger = null) : IFormatHandler
{
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


                var entries = new List<OpsEntry>();
                using (var fs = File.OpenRead(inputPath))
                {
                    var decryptor = new OpsDecryptor(fs, _logger);
                    var header = decryptor.ParseHeader();
                    entries = ReadEntries(decryptor, header);
                    foreach (var e in entries)
                    {
                        e.Data = decryptor.ReadEntryData(e.Offset, e.Size);
                    }
                }

                WriteOps(outputPath, entries);
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
    /// <para>尝试使用 OnePlusKeyTable 的全部密钥解密分区数据。</para>
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
            .Where(f => !f.EndsWith(".decrypted", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var entries = new List<OpsEntry>();
        for (var i = 0; i < files.Count; i++)
        {
            var name = Path.GetFileName(files[i]);
            entries.Add(new OpsEntry
            {
                Id = (uint)i,
                Name = name.Length > OpsConstants.ENTRY_NAME_SIZE ? name[..OpsConstants.ENTRY_NAME_SIZE] : name,
                Data = File.ReadAllBytes(files[i])
            });
        }

        WriteOps(outputPath, entries);
    }

    private static void WriteOps(string outputPath, List<OpsEntry> entries)
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
            writer.Write(entry.Data?.Length ?? 0);
            var nameBytes = new byte[OpsConstants.ENTRY_NAME_SIZE];
            var nameSrc = Encoding.ASCII.GetBytes(entry.Name ?? string.Empty);
            Array.Copy(nameSrc, nameBytes, Math.Min(nameSrc.Length, nameBytes.Length));
            writer.Write(nameBytes);
        }


        for (var i = 0; i < entries.Count; i++)
        {
            Align(writer, OpsConstants.SECTOR_SIZE);
            dataOffsets[i] = writer.BaseStream.Position;
            writer.Write(entries[i].Data ?? []);
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
