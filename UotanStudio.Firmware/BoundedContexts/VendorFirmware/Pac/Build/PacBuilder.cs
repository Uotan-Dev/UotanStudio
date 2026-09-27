using System.Runtime.InteropServices;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Pac.Models;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Repacking;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Pac.Build;

/// <summary>
/// Options for building a canonical, unsigned SPRD/UNISOC PAC package.
/// <para>构建标准无签名 SPRD/展锐 PAC 包的选项。</para>
/// </summary>
public sealed class PacPackOptions
{
    /// <summary>Text stored in the PAC format-version field. <para>写入 PAC 格式版本字段的文本。</para></summary>
    public string PacVersion { get; set; } = "UOTAN PAC";

    /// <summary>Text stored in the firmware-name field. <para>写入固件名称字段的文本。</para></summary>
    public string FirmwareName { get; set; } = "UotanStudio";

    /// <summary>Text stored in the firmware-version field. <para>写入固件版本字段的文本。</para></summary>
    public string FirmwareVersion { get; set; } = "1.0";

    /// <summary>Text stored in the firmware-alias field. <para>写入固件别名字段的文本。</para></summary>
    public string FirmwareAlias { get; set; } = string.Empty;

    /// <summary>Raw category written to every directory entry. <para>写入每个目录条目的原始类别。</para></summary>
    public uint EntryType { get; set; } = (uint)PacFileType.Image;

    /// <summary>Raw flash-use flag written to every directory entry. <para>写入每个目录条目的原始刷写用途标志。</para></summary>
    public uint FlashUse { get; set; }

    /// <summary>Raw omission flag written to every directory entry. <para>写入每个目录条目的原始省略标志。</para></summary>
    public uint OmitFlag { get; set; }
}

/// <summary>
/// Builds a bounded canonical PAC package from a directory tree.
/// <para>从目录树构建有界的标准 PAC 包。</para>
/// </summary>
public static class PacBuilder
{
    private const int MaximumEntries = 1024;
    private const int CopyBufferSize = 128 * 1024;
    private const uint ExpectedMagic = 0xFFFAFFFA;

    /// <summary>
    /// Builds a PAC package from all non-empty regular files below <paramref name="inputDirectory"/>.
    /// <para>将 <paramref name="inputDirectory"/> 下所有非空普通文件构建为 PAC 包。</para>
    /// </summary>
    /// <param name="inputDirectory">Source directory; relative paths become PAC entry names using <c>/</c>. <para>源目录；相对路径会使用 <c>/</c> 作为 PAC 条目名称。</para></param>
    /// <param name="outputPath">Destination package path, published atomically. <para>以原子方式发布的目标包路径。</para></param>
    /// <param name="options">Header and entry fields; <see langword="null"/> uses canonical defaults. <para>头部和条目字段；为 <see langword="null"/> 时使用标准默认值。</para></param>
    /// <param name="cancellationToken">Cancellation checked during enumeration and copying. <para>在枚举和复制期间检查的取消令牌。</para></param>
    /// <returns>The number of files written. <para>写入的文件数量。</para></returns>
    /// <exception cref="DirectoryNotFoundException">The source directory does not exist. <para>源目录不存在。</para></exception>
    /// <exception cref="InvalidDataException">The directory contains too many files, an empty file, an unsafe or oversized name, or the package exceeds the PAC 32-bit size limit. <para>文件过多、包含空文件、名称不安全或过长，或包超过 PAC 32 位大小限制。</para></exception>
    public static int Build(
        string inputDirectory,
        string outputPath,
        PacPackOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(inputDirectory))
        {
            throw new DirectoryNotFoundException(inputDirectory);
        }

        options ??= new PacPackOptions();
        var destination = Path.GetFullPath(outputPath);
        var files = Directory.EnumerateFiles(inputDirectory, "*", SearchOption.AllDirectories)
            .Where(path => !string.Equals(Path.GetFileName(path), RepackMetadata.FileName, StringComparison.Ordinal))
            .Where(path => !string.Equals(Path.GetFullPath(path), destination,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            .Select(path => new SourceFile(path, GetEntryName(inputDirectory, path)))
            .OrderBy(file => file.Name, StringComparer.Ordinal)
            .ToArray();
        if (files.Length > MaximumEntries)
        {
            throw new InvalidDataException($"PAC contains more than {MaximumEntries} files.");
        }

        long dataOffset = checked((long)SprdHead.Size + checked((long)files.Length * SprdFile.StructFixedSize));
        var entries = new SprdFile[files.Length];
        for (var index = 0; index < files.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(files[index].Path);
            if (info.Length <= 0 || info.Length > uint.MaxValue)
            {
                throw new InvalidDataException($"PAC entry must be non-empty and fit in 32 bits: {files[index].Name}.");
            }
            if (dataOffset > uint.MaxValue || info.Length > uint.MaxValue - dataOffset)
            {
                throw new InvalidDataException("PAC package exceeds the 32-bit size limit.");
            }

            var entry = new SprdFile
            {
                StructSize = SprdFile.StructFixedSize,
                Size = checked((uint)info.Length),
                Type = options.EntryType,
                FlashUse = options.FlashUse,
                PacOffset = checked((uint)dataOffset),
                OmitFlag = options.OmitFlag
            };
            WriteUtf16(entry.Id, files[index].Name);
            WriteUtf16(entry.Name, files[index].Name);
            entries[index] = entry;
            dataOffset = checked(dataOffset + info.Length);
        }

        var head = new SprdHead
        {
            PacSize = checked((uint)dataOffset),
            FileCount = checked((uint)entries.Length),
            DirOffset = SprdHead.Size,
            PacMagic = ExpectedMagic
        };
        WriteUtf16(head.PacVersion, options.PacVersion);
        WriteUtf16(head.FwName, options.FirmwareName);
        WriteUtf16(head.FwVersion, options.FirmwareVersion);
        WriteUtf16(head.FwAlias, options.FirmwareAlias);

        FileExtraction.WriteAtomic(outputPath, output =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Write(new byte[SprdHead.Size]);
            ushort dataCrc = 0;
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytes = entry.ToBytes();
                output.Write(bytes);
                dataCrc = Crc16.Calculate(dataCrc, bytes);
            }

            var buffer = new byte[CopyBufferSize];
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var input = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    CopyBufferSize, FileOptions.SequentialScan);
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, read);
                    dataCrc = Crc16.Calculate(dataCrc, buffer[..read]);
                }
            }

            head.DataCrc = dataCrc;
            var header = head.ToBytes();
            head.HeadCrc = Crc16.Calculate(0, header[..(SprdHead.Size - 4)]);
            header = head.ToBytes();
            output.Position = 0;
            output.Write(header);
        }, cancellationToken: cancellationToken);

        return files.Length;
    }

    private static string GetEntryName(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        FileExtraction.ResolvePath(root, relative);
        if (Encoding.Unicode.GetByteCount(relative) > 256 * 2)
        {
            throw new InvalidDataException($"PAC entry name is too long: {relative}.");
        }
        return relative;
    }

    private static void WriteUtf16(ushort[] target, string value)
    {
        var bytes = Encoding.Unicode.GetBytes(value);
        if (bytes.Length > target.Length * 2)
        {
            throw new InvalidDataException($"PAC text field is too long: {value}.");
        }
        bytes.CopyTo(MemoryMarshal.AsBytes<ushort>(target.AsSpan()));
    }

    private readonly record struct SourceFile(string Path, string Name);
}
