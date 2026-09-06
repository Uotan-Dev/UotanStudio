using System.IO.Compression;
using UotanStudio.Firmware.Common.Streams;
using UotanStudio.Firmware.Oppo.Crypto;
using UotanStudio.Firmware.Oppo.Models;

namespace UotanStudio.Firmware.Oppo;

public sealed partial class OppReader
{
    private OppExtractResult ExtractOzipCore(
        Stream stream,
        OppArchive archive,
        string outputDir,
        bool overwrite,
        string? keyId,
        IProgress<OppExtractProgress>? progress)
    {
        if (archive.Entries.Count == 1 && archive.Entries[0].EncryptedSize > 0)
        {
            var fallbackPath = Path.Combine(outputDir, "decrypted.zip");
            var extracted = false;
            if (!File.Exists(fallbackPath) || overwrite)
            {
                WriteAtomically(fallbackPath, overwrite, output =>
                {
                    stream.Position = 0;
                    OzipDecryptor.DecryptFileWithAutoDetection(stream, output);
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
            progress?.Report(new OppExtractProgress(entries.Length, completed, entry.FullName));

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
                using var boundedOutput = new CountingWriteStream(output, entry.Length, leaveOpen: true);
                using var entryStream = entry.Open();
                entryStream.CopyTo(boundedOutput);
                if (boundedOutput.BytesWritten != entry.Length)
                {
                    throw new InvalidDataException(
                        $"ZIP entry '{entry.FullName}' produced {boundedOutput.BytesWritten} bytes; expected {entry.Length}.");
                }
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
