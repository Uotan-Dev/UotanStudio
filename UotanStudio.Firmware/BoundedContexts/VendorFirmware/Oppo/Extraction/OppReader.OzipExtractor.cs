using System.IO.Compression;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Crypto;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Models;
using UotanStudio.Firmware.SharedKernel.Streams;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Oppo.Extraction;

/// <summary>
/// Reads, parses and extracts Oppo firmware packages (OFP, OPS, Ozip, including ZIP-wrapped OFP) from files or streams. Package bytes are treated as untrusted input: structure, sizes and output paths are validated before anything is written, and outputs are written atomically through temporary files. The reader is stateless apart from its injected crypto provider and logger and can be used from multiple threads.
/// <para>从文件或流中读取、解析并提取 Oppo 固件包（OFP、OPS、Ozip，含 ZIP 包裹的 OFP）。包字节视为不可信输入：写入前会校验结构、大小和输出路径，并通过临时文件原子写出。除注入的加密提供程序和日志器外，读取器无状态，可多线程使用。</para>
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
            ValidateOutputPath(outputDir, outputPath);

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