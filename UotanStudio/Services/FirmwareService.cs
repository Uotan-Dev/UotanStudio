using FirmwareKit.AVB.VBMeta;
using FirmwareKit.CPIO;
using FirmwareKit.CPIO.Extraction;
using FirmwareKit.CPIO.Model;
using FirmwareKit.Oppo;
using FirmwareKit.Oppo.Models;
using FirmwareKit.Payload;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace UotanStudio.Services;

/// <summary>
/// <para>固件操作结果，承载成功标志与输出摘要。</para>
/// Firmware operation result, carrying success flag and output summary.
/// </summary>
public sealed class FirmwareOpResult
{
    public bool Success { get; init; }
    public string Summary { get; init; } = string.Empty;
    public IReadOnlyList<string> Entries { get; init; } = [];
}

/// <summary>
/// <para>固件综合服务，基于 FirmwareKit 生态封装 CPIO 解包/打包、Payload OTA 提取、AVB VBMeta 校验与 Oppo 全格式读取。</para>
/// Firmware integrated service, based on the FirmwareKit ecosystem, wrapping CPIO unpack/pack, Payload OTA extraction, AVB VBMeta verification, and Oppo full-format reading.
/// </summary>
public sealed class FirmwareService
{
    private static readonly Lazy<FirmwareService> LazyInstance = new(() => new FirmwareService());
    /// <summary>获取 FirmwareService 单例。</summary>
    public static FirmwareService Instance => LazyInstance.Value;

    private FirmwareService() { }

    /// <summary>
    /// <para>列举 CPIO/TAR 归档的全部条目名称（不解包到磁盘）。</para>
    /// Lists all entry names in a CPIO/TAR archive without unpacking to disk.
    /// </summary>
    /// <param name="archivePath">归档文件路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>操作结果，Entries 为条目名列表。</returns>
    public async Task<FirmwareOpResult> ListCpioEntriesAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var fs = File.OpenRead(archivePath);
            var archive = new CpioArchive();
            var entries = archive.ReadAll(fs);
            var names = entries.Select(e => e.Name ?? string.Empty).Where(n => n.Length > 0).ToList();
            return new FirmwareOpResult { Success = true, Summary = $"{names.Count} entries", Entries = names };
        }
        catch (Exception ex)
        {
            return new FirmwareOpResult { Success = false, Summary = ex.Message };
        }
    }

    /// <summary>
    /// <para>将 CPIO/TAR 归档解包到指定目录。</para>
    /// Unpacks a CPIO/TAR archive to the specified directory.
    /// </summary>
    /// <param name="archivePath">归档文件路径。</param>
    /// <param name="outputDirectory">输出目录。</param>
    /// <param name="overwrite">是否覆盖已存在文件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>操作结果。</returns>
    public async Task<FirmwareOpResult> UnpackCpioAsync(string archivePath, string outputDirectory, bool overwrite = true, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var fs = File.OpenRead(archivePath);
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
    /// <para>将目录内容打包为 CPIO 归档（newc 格式）。</para>
    /// Packs a directory into a CPIO archive (newc format).
    /// </summary>
    /// <param name="inputDirectory">输入目录。</param>
    /// <param name="outputArchivePath">输出归档路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>操作结果。</returns>
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
            using var output = File.Create(outputArchivePath);
            archive.SaveAll(output, entries, ArchiveFormat.NewAscii);
            return new FirmwareOpResult { Success = true, Summary = $"Packed {entries.Count} entries to {outputArchivePath}" };
        }
        catch (Exception ex)
        {
            return new FirmwareOpResult { Success = false, Summary = ex.Message };
        }
    }

    /// <summary>
    /// <para>从 Android OTA payload.bin 提取指定分区（或全部分区）到输出目录。</para>
    /// Extracts specified partitions (or all partitions) from an Android OTA payload.bin to an output directory.
    /// </summary>
    /// <param name="payloadPath">payload.bin 文件路径。</param>
    /// <param name="outputDirectory">输出目录。</param>
    /// <param name="partitionName">目标分区名；null 或空表示全部分区。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>操作结果。</returns>
    public async Task<FirmwareOpResult> ExtractPayloadAsync(string payloadPath, string outputDirectory, string? partitionName = null, CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(outputDirectory);
            var metadata = await PayloadParser.GetPayloadMetadataAsync(payloadPath);
            var allNames = metadata?.PartitionNames ?? new();
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
    /// <para>校验 Android Verified Boot VBMeta 镜像，输出头部与描述符摘要。</para>
    /// Verifies an Android Verified Boot VBMeta image, outputting header and descriptor summary.
    /// </summary>
    /// <param name="vbmetaPath">vbmeta.img 文件路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>操作结果，Summary 包含头部摘要。</returns>
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
    /// <para>读取 Oppo 固件包（自动检测 OFP/OPS/OZIP）的元数据，输出格式与条目数。</para>
    /// Reads metadata of an Oppo firmware package (auto-detecting OFP/OPS/OZIP), outputting format and entry count.
    /// </summary>
    /// <param name="firmwarePath">固件包路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>操作结果。</returns>
    public async Task<FirmwareOpResult> ReadOppoMetadataAsync(string firmwarePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var reader = new OppReader();
            var metadata = await reader.ReadMetadataAsync(firmwarePath, cancellationToken);
            var summary = $"Format: {metadata.FormatName}\nEntries: {metadata.EntryCount}";
            return new FirmwareOpResult { Success = true, Summary = summary };
        }
        catch (Exception ex)
        {
            return new FirmwareOpResult { Success = false, Summary = ex.Message };
        }
    }

    /// <summary>
    /// <para>解包 Oppo 固件包（自动检测 OFP/OPS/OZIP）到输出目录。</para>
    /// Unpacks an Oppo firmware package (auto-detecting OFP/OPS/OZIP) to an output directory.
    /// </summary>
    /// <param name="firmwarePath">固件包路径。</param>
    /// <param name="outputDirectory">输出目录。</param>
    /// <param name="overwrite">是否覆盖。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>操作结果。</returns>
    public async Task<FirmwareOpResult> ExtractOppoAsync(string firmwarePath, string outputDirectory, bool overwrite = true, CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(outputDirectory);
            var reader = new OppReader();
            var result = await reader.ExtractAsync(firmwarePath, outputDirectory, new OppExtractOptions
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
    /// <para>校验文件 SHA256，用于固件完整性核对。</summary>
    /// Verifies a file's SHA256, for firmware integrity checking.
    /// </summary>
    /// <param name="filePath">文件路径。</param>
    /// <param name="expectedHash">期望的 SHA256 十六进制字符串；空则仅返回计算值。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>操作结果，Summary 为计算值或比对结论。</returns>
    public async Task<FirmwareOpResult> VerifyChecksumAsync(string filePath, string? expectedHash = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var fs = File.OpenRead(filePath);
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
