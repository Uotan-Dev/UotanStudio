using System.IO.Compression;
using UotanStudio.Firmware.Core.Streams;
using UotanStudio.Firmware.VendorFirmware.Oppo.Crypto;
using UotanStudio.Firmware.VendorFirmware.Oppo.Models;

namespace UotanStudio.Firmware.VendorFirmware.Oppo;

/// <summary>
/// Reads, parses and extracts Oppo firmware packages (OFP, OPS, Ozip, including ZIP-wrapped OFP) from files or streams. Package bytes are untrusted input: structure, sizes and output paths are validated first and outputs are written atomically through temporary files.
/// <para>从文件或流读取、解析并解包 OPPO 固件包（OFP、OPS、Ozip，含 ZIP 封装的 OFP）。包字节视为不可信输入：先校验结构、大小与输出路径，输出经临时文件原子写入。</para>
/// </summary>
public sealed partial class OppReader
{
    private OppExtractResult ExtractOzipCore(
        Stream stream,
        OppArchive archive,
        string outputDir,
        bool overwrite,
        string? keyId,
        HashSet<string>? selected,
        IProgress<OppExtractProgress>? progress, CancellationToken cancellationToken)
    {
        if (archive.Entries.Count == 1 && archive.Entries[0].EncryptedSize > 0)
        {
            var fallbackPath = Path.Combine(outputDir, "decrypted.zip");
            var extracted = false;
            if (!File.Exists(fallbackPath) || overwrite)
            {
                WriteAtomically(fallbackPath, overwrite, output =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var limited = new CountingWriteStream(output, archive.Entries[0].Size,
                        leaveOpen: true, cancellationToken: cancellationToken);
                    stream.Position = 0;
                    OzipDecryptor.DecryptFileWithAutoDetection(stream, limited);
                    cancellationToken.ThrowIfCancellationRequested();
                });
                extracted = true;
            }

            return new OppExtractResult
            {
                ExtractedFileCount = extracted ? 1 : 0,
                OutputDirectory = outputDir,
                KeyId = keyId ?? archive.Metadata.KeyId
            };
        }

        stream.Position = 0;
        using var decrypted = OzipDecryptor.OpenDecryptedStream(stream, out var detectedKey, leaveOpen: true);
        using var zip = new ZipArchive(decrypted, ZipArchiveMode.Read, leaveOpen: true);

        var entries = zip.Entries
            .Where(entry => !(entry.Length == 0 && entry.FullName.EndsWith('/')))
            .ToArray();
        var extractedCount = 0;
        var completed = 0;

        foreach (var entry in entries)
        {
            if (selected is not null && !selected.Contains(entry.FullName)) continue;
            progress?.Report(new OppExtractProgress(entries.Length, completed, entry.FullName));
            cancellationToken.ThrowIfCancellationRequested();

            var sanitized = SanitizeFileName(entry.FullName);
            if (string.IsNullOrWhiteSpace(sanitized) || sanitized is "." or "..")
            {
                sanitized = $"entry_{completed}";
            }

            var outputPath = Path.Combine(outputDir, sanitized);
            OppOutputPathGuard.Validate(outputDir, outputPath);

            if (File.Exists(outputPath) && !overwrite)
            {
                completed++;
                continue;
            }

            WriteAtomically(outputPath, overwrite, output =>
            {
                using var boundedOutput = new CountingWriteStream(output, entry.Length, leaveOpen: true,
                    cancellationToken: cancellationToken);
                using var entryStream = entry.Open();
                entryStream.CopyTo(boundedOutput);
                if (boundedOutput.BytesWritten != entry.Length)
                {
                    throw new InvalidDataException(
                        $"ZIP entry '{entry.FullName}' produced {boundedOutput.BytesWritten} bytes; expected {entry.Length}.");
                }
                cancellationToken.ThrowIfCancellationRequested();
            });

            extractedCount++;
            completed++;
            progress?.Report(new OppExtractProgress(entries.Length, completed, entry.FullName));
        }

        return new OppExtractResult
        {
            ExtractedFileCount = extractedCount,
            OutputDirectory = outputDir,
            KeyId = keyId ?? detectedKey.Description ?? detectedKey.KeyHex
        };
    }

    private static void WriteAtomically(string outputPath, bool overwrite, Action<Stream> write)
    {
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        var tempPath = Path.Combine(
            outputDirectory ?? Path.GetTempPath(),
            $".uotan-ozip-{Guid.NewGuid():N}.tmp");

        try
        {
            using (var output = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                DefaultBufferSize,
                FileOptions.SequentialScan))
            {
                write(output);
            }

            File.Move(tempPath, outputPath, overwrite);
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (IOException)
            {

            }
            catch (UnauthorizedAccessException)
            {

            }
        }
    }
}