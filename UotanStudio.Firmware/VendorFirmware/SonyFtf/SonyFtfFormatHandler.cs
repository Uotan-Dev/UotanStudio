using UotanStudio.Compress.Zip;
using UotanStudio.Firmware.Core;

namespace UotanStudio.Firmware.VendorFirmware.SonyFtf;

/// <summary>Reads legacy Sony Xperia FTF bundles, which are bounded ZIP containers of SIN images.</summary>
/// <para>读取旧版 Sony Xperia FTF 固件包；该格式是由 SIN 镜像组成的有界 ZIP 容器。</para>
internal sealed class SonyFtfFormatHandler : IReadonlyFormatHandler
{
    private const int MaximumEntries = 4096;
    private const long MaximumEntryBytes = 32L * 1024 * 1024 * 1024;
    private const long MaximumTotalBytes = 64L * 1024 * 1024 * 1024;

    public string FormatName => "Sony FTF";
    public IReadOnlyList<string> FileExtensions { get; } = ["ftf"];

    public bool CanHandle(string filePath)
    {
        if (!HeaderProbe.ProbeMagic(filePath, 4, 0, "PK\\x03\\x04"u8)) return false;
        try
        {
            using var archive = ZipContainer.OpenRead(filePath, new(MaximumEntries, MaximumEntryBytes, MaximumTotalBytes));
            return archive.Entries.Any(e => !string.IsNullOrEmpty(e.Name) && e.FullName.EndsWith(".sin", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    public Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var archive = ZipContainer.OpenRead(filePath, new(MaximumEntries, MaximumEntryBytes, MaximumTotalBytes));
            var entries = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).Select(e => e.FullName).ToArray();
            var sins = entries.Count(e => e.EndsWith(".sin", StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(FirmwareOpResult.Ok($"Sony FTF bundle with {sins} SIN images and {entries.Length} files.", entries,
                new Dictionary<string, string> { ["EntryCount"] = entries.Length.ToString(), ["SinCount"] = sins.ToString(), ["Container"] = "ZIP" }));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return Task.FromResult(FirmwareOpResult.Fail($"Sony FTF metadata read failed: {ex.Message}")); }
    }

    public async Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(outputDirectory);
            var overwrite = options?.Overwrite ?? true;
            var maximum = options?.MaximumOutputBytes ?? new UnpackOptions().MaximumOutputBytes;
            long total = 0;
            var written = new List<string>();
            using var archive = ZipContainer.OpenRead(filePath, new(MaximumEntries, MaximumEntryBytes, MaximumTotalBytes));
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(entry.Name)) { FileExtraction.ResolvePath(outputDirectory, entry.FullName); continue; }
                if (entry.Length < 0 || entry.Length > maximum - total) throw new InvalidDataException($"FTF entry '{entry.FullName}' exceeds output budget.");
                var target = FileExtraction.ResolvePath(outputDirectory, entry.FullName);
                await FileExtraction.WriteAtomicAsync(target, async output =>
                {
                    await using var input = entry.Open();
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                }, overwrite, cancellationToken).ConfigureAwait(false);
                total += entry.Length;
                written.Add(target);
            }
            return FirmwareOpResult.Ok($"Sony FTF unpacked to {outputDirectory} ({written.Count} files).", written,
                new Dictionary<string, string> { ["ExtractedBytes"] = total.ToString(), ["SinCount"] = written.Count(p => p.EndsWith(".sin", StringComparison.OrdinalIgnoreCase)).ToString() });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return FirmwareOpResult.Fail($"Sony FTF unpack failed: {ex.Message}"); }
    }
}
