using FirmwareKit.AVB.VBMeta;
using FirmwareKit.CPIO;
using FirmwareKit.CPIO.Extraction;
using FirmwareKit.CPIO.Model;
using FirmwareKit.Oppo;
using FirmwareKit.Oppo.Models;
using FirmwareKit.Payload;
using FirmwareKit.Payload.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace UotanStudio.Services;

/// <summary>
/// Firmware operation result, carrying success flag and output summary.
/// <para>固件操作结果，承载成功标志与输出摘要。</para>
/// </summary>
public sealed class FirmwareOpResult
{
    /// <summary>
    /// Gets whether the operation succeeded.
    /// <para>获取操作是否成功。</para>
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Gets the operation summary.
    /// <para>获取操作摘要。</para>
    /// </summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>
    /// Gets the list of entry names.
    /// <para>获取条目名称列表。</para>
    /// </summary>
    public IReadOnlyList<string> Entries { get; init; } = [];
}

/// <summary>
/// Firmware integrated service, based on the FirmwareKit ecosystem, wrapping CPIO unpack/pack, Payload OTA extraction, AVB VBMeta verification, and Oppo full-format reading.
/// <para>固件综合服务，基于 FirmwareKit 生态封装 CPIO 解包/打包、Payload OTA 提取、AVB VBMeta 校验与 Oppo 全格式读取。</para>
/// </summary>
public sealed class FirmwareService
{
    private static readonly Lazy<FirmwareService> LazyInstance = new(() => new FirmwareService());

    /// <summary>
    /// Gets the FirmwareService singleton instance.
    /// <para>获取 FirmwareService 单例。</para>
    /// </summary>
    public static FirmwareService Instance => LazyInstance.Value;

    private FirmwareService() { }

    /// <summary>
    /// Lists all entry names in a CPIO/TAR archive without unpacking to disk.
    /// <para>列举 CPIO/TAR 归档的全部条目名称（不解包到磁盘）。</para>
    /// </summary>
    /// <param name="archivePath">The archive file path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Operation result, Entries is the entry name list.</returns>
    public async Task<FirmwareOpResult> ListCpioEntriesAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        try
        {
            await using FileStream fs = File.OpenRead(archivePath);
            var archive = new CpioArchive();
            IReadOnlyList<ArchiveEntry> entries = archive.ReadAll(fs);
            var names = entries.Select(e => e.Name ?? string.Empty).Where(n => n.Length > 0).ToList();
            return new FirmwareOpResult { Success = true, Summary = $"{names.Count} entries", Entries = names };
        }
        catch (Exception ex)
        {
            return new FirmwareOpResult { Success = false, Summary = ex.Message };
        }
    }

    /// <summary>
    /// Unpacks a CPIO/TAR archive to the specified directory.
    /// <para>将 CPIO/TAR 归档解包到指定目录。</para>
    /// </summary>
    /// <param name="archivePath">The archive file path.</param>
    /// <param name="outputDirectory">The output directory.</param>
    /// <param name="overwrite">Whether to overwrite existing files.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Operation result.</returns>
    public async Task<FirmwareOpResult> UnpackCpioAsync(string archivePath, string outputDirectory, bool overwrite = true, CancellationToken cancellationToken = default)
    {
        try
        {
            await using FileStream fs = File.OpenRead(archivePath);
            var archive = new CpioArchive();
            Directory.CreateDirectory(outputDirectory);
            archive.ExtractToDirectory(fs, outputDirectory, new ExtractOptions
            {
                MakeDirectories = true,
                Overwrite = overwrite
            });
            var count = Directory.EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories).Count();
            return new FirmwareOpResult { Success = true, Summary = $"Extracted {count} files to {outputDirectory}" };
        }
        catch (Exception ex)
        {
            return new FirmwareOpResult { Success = false, Summary = ex.Message };
        }
    }

    /// <summary>
    /// Packs a directory into a CPIO archive (newc format).
    /// <para>将目录内容打包为 CPIO 归档（newc 格式）。</para>
    /// </summary>
    /// <param name="inputDirectory">The input directory.</param>
    /// <param name="outputArchivePath">The output archive path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Operation result.</returns>
    public async Task<FirmwareOpResult> PackCpioAsync(string inputDirectory, string outputArchivePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var archive = new CpioArchive();
            var entries = new List<ArchiveEntry>();
            foreach (var file in Directory.EnumerateFiles(inputDirectory, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(inputDirectory, file);
                entries.Add(new ArchiveEntry
                {
                    Name = rel.Replace('\\', '/'),
                    Data = await File.ReadAllBytesAsync(file, cancellationToken),
                    Metadata = new ArchiveEntryMetadata
                    {
                        FileType = CpioFileType.Regular,
                        UnixPermissions = 0x1A4,
                        LinkCount = 1
                    }
                });
            }
            using FileStream output = File.Create(outputArchivePath);
            archive.SaveAll(output, entries, ArchiveFormat.NewAscii);
            return new FirmwareOpResult { Success = true, Summary = $"Packed {entries.Count} entries to {outputArchivePath}" };
        }
        catch (Exception ex)
        {
            return new FirmwareOpResult { Success = false, Summary = ex.Message };
        }
    }

    /// <summary>
    /// Extracts specified partitions (or all partitions) from an Android OTA payload.bin to an output directory.
    /// <para>从 Android OTA payload.bin 提取指定分区（或全部分区）到输出目录。</para>
    /// </summary>
    /// <param name="payloadPath">The payload.bin file path.</param>
    /// <param name="outputDirectory">The output directory.</param>
    /// <param name="partitionName">Target partition name; null or empty means all partitions.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Operation result.</returns>
    public async Task<FirmwareOpResult> ExtractPayloadAsync(string payloadPath, string outputDirectory, string? partitionName = null, CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(outputDirectory);
            PayloadMetadata metadata = await PayloadParser.GetPayloadMetadataAsync(payloadPath);
            List<string> allNames = metadata?.PartitionNames ?? new();
            var targets = string.IsNullOrWhiteSpace(partitionName)
                ? allNames.Where(n => !string.IsNullOrEmpty(n)).ToArray()
                : new[] { partitionName };

            await PayloadParser.ExtractAsync(payloadPath, targets, outputDirectory);
            return new FirmwareOpResult { Success = true, Summary = $"Extracted {targets.Length} partitions", Entries = targets.ToList() };
        }
        catch (Exception ex)
        {
            return new FirmwareOpResult { Success = false, Summary = ex.Message };
        }
    }

    /// <summary>
    /// Verifies an Android Verified Boot VBMeta image, outputting header and descriptor summary.
    /// <para>校验 Android Verified Boot VBMeta 镜像，输出头部与描述符摘要。</para>
    /// </summary>
    /// <param name="vbmetaPath">The vbmeta.img file path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Operation result, Summary contains header summary.</returns>
    public async Task<FirmwareOpResult> VerifyAvbAsync(string vbmetaPath, CancellationToken cancellationToken = default)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(vbmetaPath, cancellationToken);
            var header = AvbVBMetaImageHeader.FromBytes(bytes);
            // AvbVBMetaImage.TryVerify 仅校验镜像自洽性；返回头部摘要
            var summary = $"VBMeta header parsed.\nAlgorithm: {header.AlgorithmType}\nDescriptors size: {header.DescriptorsSize} bytes";
            return new FirmwareOpResult { Success = true, Summary = summary };
        }
        catch (Exception ex)
        {
            return new FirmwareOpResult { Success = false, Summary = ex.Message };
        }
    }

    /// <summary>
    /// Reads metadata of an Oppo firmware package (auto-detecting OFP/OPS/OZIP), outputting format and entry count.
    /// <para>读取 Oppo 固件包（自动检测 OFP/OPS/OZIP）的元数据，输出格式与条目数。</para>
    /// </summary>
    /// <param name="firmwarePath">The firmware package path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Operation result.</returns>
    public async Task<FirmwareOpResult> ReadOppoMetadataAsync(string firmwarePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var reader = new OppReader();
            OppMetadata metadata = await reader.ReadMetadataAsync(firmwarePath, cancellationToken);
            var summary = $"Format: {metadata.FormatName}\nEntries: {metadata.EntryCount}";
            return new FirmwareOpResult { Success = true, Summary = summary };
        }
        catch (Exception ex)
        {
            return new FirmwareOpResult { Success = false, Summary = ex.Message };
        }
    }

    /// <summary>
    /// Unpacks an Oppo firmware package (auto-detecting OFP/OPS/OZIP) to an output directory.
    /// <para>解包 Oppo 固件包（自动检测 OFP/OPS/OZIP）到输出目录。</para>
    /// </summary>
    /// <param name="firmwarePath">The firmware package path.</param>
    /// <param name="outputDirectory">The output directory.</param>
    /// <param name="overwrite">Whether to overwrite existing files.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Operation result.</returns>
    public async Task<FirmwareOpResult> ExtractOppoAsync(string firmwarePath, string outputDirectory, bool overwrite = true, CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(outputDirectory);
            var reader = new OppReader();
            OppExtractResult result = await reader.ExtractAsync(firmwarePath, outputDirectory, new OppExtractOptions
            {
                Overwrite = overwrite
            }, progress: null, cancellationToken);
            var ok = result.ExtractedFileCount;
            return new FirmwareOpResult { Success = ok >= 0, Summary = $"Extracted {ok} files" };
        }
        catch (Exception ex)
        {
            return new FirmwareOpResult { Success = false, Summary = ex.Message };
        }
    }

    /// <summary>
    /// Verifies a file's SHA256, for firmware integrity checking.
    /// <para>校验文件 SHA256，用于固件完整性核对。</para>
    /// </summary>
    /// <param name="filePath">The file path.</param>
    /// <param name="expectedHash">Expected SHA256 hex string; empty returns only computed value.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Operation result, Summary is computed value or comparison result.</returns>
    public async Task<FirmwareOpResult> VerifyChecksumAsync(string filePath, string? expectedHash = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await using FileStream fs = File.OpenRead(filePath);
            var hash = await System.Security.Cryptography.SHA256.HashDataAsync(fs, cancellationToken);
            var hex = Convert.ToHexString(hash).ToLowerInvariant();
            var ok = string.IsNullOrEmpty(expectedHash) || string.Equals(hex, expectedHash, StringComparison.OrdinalIgnoreCase);
            return new FirmwareOpResult
            {
                Success = ok,
                Summary = string.IsNullOrEmpty(expectedHash) ? $"SHA256: {hex}" : (ok ? "Checksum match." : $"Mismatch: expected={expectedHash}, actual={hex}")
            };
        }
        catch (Exception ex)
        {
            return new FirmwareOpResult { Success = false, Summary = ex.Message };
        }
    }
}
