using UotanStudio.Compress.Zip;
using UotanStudio.Firmware.Core;

namespace UotanStudio.Firmware.VendorFirmware.Htc.Ruu;

/// <summary>
/// Detection, metadata reading and unpacking for HTC RUU ROM ZIP containers.
/// <para>HTC RUU ROM ZIP 容器的检测、元数据读取与解包入口。</para>
/// </summary>
internal sealed class RuuFormatHandler : IReadonlyFormatHandler
{
    /// <summary>Display name and registration key.</summary>
    /// <para>显示名称与注册键。</para>
    public string FormatName => "HTC RUU ROM Package";

    /// <summary>Extensions claimed before content inspection.</summary>
    /// <para>内容检查前声明认领的扩展名。</para>
    public IReadOnlyList<string> FileExtensions => ["ruu"];

    /// <summary>
    /// Accepts files carrying the Htc@egi$ header, the LaR@eZip header, or a ZIP magic at offset
    /// zero or after the 0x100 InstallShield signature prefix.
    /// <para>接受带 Htc@egi$ 头、LaR@eZip 头，或在偏移 0/0x100 签名前缀后带 ZIP 魔数的文件。</para>
    /// </summary>
    /// <param name="filePath">Candidate path.<para>候选路径。</para></param>
    /// <returns>True when this handler accepts the file.<para>接受该文件时为真。</para></returns>
    public bool CanHandle(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return RuuFormat.ReadHeader(stream) is not null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Reads container metadata without extracting entries.</summary>
    /// <para>读取容器元数据，不提取条目。</para>
    /// <param name="filePath">Container path.<para>容器路径。</para></param>
    /// <param name="cancellationToken">Cancellation token.<para>取消令牌。</para></param>
    /// <returns>Result carrying kind, offset, key fields and entry names.</returns>
    public async Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            var header = RuuFormat.ReadHeader(stream);
            if (header is null)
            {
                return FirmwareOpResult.Fail("Not an HTC RUU ROM container.");
            }

            var metadata = new Dictionary<string, string>
            {
                ["ContainerKind"] = header.Kind.ToString(),
                ["HeaderOffset"] = $"0x{header.HeaderOffset:X}",
                ["RangeCount"] = header.Ranges.Count.ToString()
            };
            if (header.Kind == RuuContainerKind.EncryptedZip)
            {
                metadata["KeymapIndex"] = $"0x{header.KeymapIndex:X}";
                metadata["Chunks"] = $"0x{header.Chunks:X}";
                metadata["ChunkSize"] = RuuFormat.ChunkSize((byte)header.Chunks).ToString();
                metadata["Mainver"] = header.Mainver;
            }

            return FirmwareOpResult.Ok(
                $"HTC RUU {header.Kind} (header at 0x{header.HeaderOffset:X})",
                metadata: metadata);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or OverflowException or UnauthorizedAccessException)
        {
            return FirmwareOpResult.Fail(exception.Message);
        }
    }

    /// <summary>Decrypts when needed and extracts every ZIP entry into the output directory.</summary>
    /// <para>按需解密并将全部 ZIP 条目提取到输出目录。</para>
    /// <param name="filePath">Container path; never modified.<para>容器路径；不会被修改。</para></param>
    /// <param name="outputDirectory">Destination directory.<para>目标目录。</para></param>
    /// <param name="options">Extraction limits, selection and overwrite policy.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result listing written files and total bytes.</returns>
    public async Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory,
        UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new UnpackOptions();
        var selected = options.CreateSelectedEntrySet();
        var maximumOutputBytes = options.MaximumOutputBytes;
        var written = new List<string>();
        var components = new List<FirmwareComponent>();
        long totalBytes = 0;
        var stage = Path.Combine(Path.GetTempPath(), $"uotan-ruu-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stage);

        try
        {
            await using var input = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            var header = RuuFormat.ReadHeader(input);
            if (header is null)
            {
                return FirmwareOpResult.Fail("Not an HTC RUU ROM container.");
            }

            if (header.Kind == RuuContainerKind.LargeZip)
            {
                return FirmwareOpResult.Fail("LaR@eZip multi-volume containers are not supported for unpacking; extract the member ROM ZIP first.");
            }

            var payloadStart = header.Kind == RuuContainerKind.SignedZip
                ? header.HeaderOffset + RuuFormat.SignaturePrefixLength
                : header.HeaderOffset;
            var payloadLength = input.Length - payloadStart;
            RuuFormat.ValidateRange(payloadStart, payloadLength, input.Length, "RUU payload");
            var payloadPath = Path.Combine(stage, "rom.zip");

            if (header.Kind == RuuContainerKind.EncryptedZip)
            {
                var dataOffset = payloadStart + RuuFormat.HeaderLength;
                var encryptedLength = payloadLength - RuuFormat.HeaderLength;
                RuuFormat.ValidateRange(dataOffset, encryptedLength, input.Length, "RUU encrypted payload");
                var keys = RuuFormat.FindKeys(input, dataOffset, (byte)header.Chunks, RuuKeydata.Length / 0x60);
                if (keys is null)
                {
                    return FirmwareOpResult.Fail("No published RUU key matched the encrypted payload; the device may be unsupported.");
                }

                input.Position = dataOffset;
                await using var decrypted = new FileStream(payloadPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous);
                RuuFormat.Decrypt(input, decrypted, encryptedLength, (byte)header.Chunks, keys,
                    null, cancellationToken);
                await decrypted.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                input.Position = payloadStart;
                await using var plain = new FileStream(payloadPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous);
                await input.CopyToAsync(plain, cancellationToken).ConfigureAwait(false);
            }

            await using (var archive = ZipContainer.OpenRead(payloadPath))
            {
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/'))
                    {
                        FileExtraction.ResolvePath(outputDirectory, entry.FullName);
                        continue;
                    }

                    if (selected is not null && !selected.Contains(entry.FullName))
                    {
                        continue;
                    }

                    var target = FileExtraction.ResolvePath(outputDirectory, entry.FullName);
                    var length = entry.Length;
                    if (length < 0 || length > maximumOutputBytes - totalBytes)
                    {
                        throw new InvalidDataException(
                            $"RUU entry '{entry.FullName}' exceeds the remaining output budget ({maximumOutputBytes - totalBytes} bytes left).");
                    }

                    await FileExtraction.WriteAtomicAsync(target, output =>
                    {
                        ZipContainer.CopyEntryTo(entry, output, length, cancellationToken);
                        return Task.CompletedTask;
                    }, options.Overwrite, cancellationToken).ConfigureAwait(false);

                    totalBytes += length;
                    written.Add(target);
                    components.Add(FirmwareComponent.Of(entry.FullName, length));
                    options.Progress?.Report(archive.Entries.Count == 0 ? 100 : written.Count * 100.0 / archive.Entries.Count, entry.FullName);
                }
            }

            return FirmwareOpResult.Ok(
                $"RUU unpacked to {outputDirectory} ({written.Count} files, {totalBytes} bytes)",
                entries: written, components: components);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or OverflowException or UnauthorizedAccessException)
        {
            return FirmwareOpResult.Fail(exception.Message);
        }
        finally
        {
            try
            {
                Directory.Delete(stage, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}