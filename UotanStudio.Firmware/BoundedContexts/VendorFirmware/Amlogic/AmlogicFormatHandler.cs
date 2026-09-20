using UotanStudio.Firmware.SharedKernel;
using System.Buffers.Binary;
using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Amlogic;

/// <summary>Conservative AMLogic ampack detector. Unknown revisions are read-only until independently specified.</summary>
public sealed class AmlogicFormatHandler : IFormatHandler
{
    private const uint Magic = 0x27B51956;
    public string FormatName => "AMLogic ampack";
    public IReadOnlyList<string> FileExtensions => ["img", "aml", "ampack"];

    public bool CanHandle(string filePath)
    {
        if (!File.Exists(filePath)) return false;
        try
        {
            using var stream = File.OpenRead(filePath);
            if (stream.Length < 12) return false;
            stream.Position = 8;
            Span<byte> bytes = stackalloc byte[4];
            return stream.Read(bytes) == 4 && BitConverter.ToUInt32(bytes) == Magic;
        }
        catch { return false; }
    }

    public Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!CanHandle(filePath)) return Task.FromResult(FirmwareOpResult.Fail("Not a content-confirmed AMLogic ampack image."));
        try
        {
            var image = Parse(filePath);
            return Task.FromResult(FirmwareOpResult.Ok($"AMLogic ampack v{image.Version} metadata confirmed ({image.Items.Count} items).",
                entries: [.. image.Items.Select(x => $"{x.SubType}.{x.MainType}")],
                metadata: new Dictionary<string, string> { ["MagicOffset"] = "8", ["Magic"] = $"0x{Magic:X8}", ["Version"] = image.Version.ToString(), ["Alignment"] = image.Alignment.ToString(), ["ItemCount"] = image.Items.Count.ToString(), ["Length"] = image.Length.ToString(), ["Packing"] = "unsupported" }));
        }
        catch (Exception ex) { return Task.FromResult(FirmwareOpResult.Fail(ex.Message)); }
    }

    public async Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var image = Parse(filePath);
            Directory.CreateDirectory(outputDirectory);
            await using var input = File.OpenRead(filePath);
            foreach (var item in image.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.Size > 16L * 1024 * 1024 * 1024 || item.Offset < 0 || item.Offset + item.Size > input.Length)
                    throw new InvalidDataException($"AMLogic item '{item.SubType}' is outside the image.");
                input.Position = item.Offset;
                var safe = string.Concat(item.SubType, ".", item.MainType).Replace("..", "_").Replace('/', '_').Replace('\\', '_');
                await using var output = new FileStream(Path.Combine(outputDirectory, safe), FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);
                await CopyExactlyAsync(input, output, item.Size, cancellationToken).ConfigureAwait(false);
            }
            return FirmwareOpResult.Ok($"Extracted {image.Items.Count} AMLogic items.", entries: [.. image.Items.Select(x => $"{x.SubType}.{x.MainType}")]);
        }
        catch (Exception ex) { return FirmwareOpResult.Fail(ex.Message); }
    }

    public async Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(inputPath)) return FirmwareOpResult.Fail("AMLogic pack input must be a directory.");
        var files = Directory.EnumerateFiles(inputPath, "*", SearchOption.TopDirectoryOnly)
            .Where(x => !Path.GetFileName(x).Equals("image.cfg", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (files.Length == 0 || files.Length > 65_536) return FirmwareOpResult.Fail("AMLogic input item count is outside the limit.");
        const uint alignment = 4;
        var tableOffset = 72L;
        var dataOffset = tableOffset + files.Length * 576L;
        var items = new List<(string Main, string Sub, long Offset, long Size)>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            var separator = name.IndexOf('.');
            if (separator <= 0 || separator == name.Length - 1) return FirmwareOpResult.Fail($"AMLogic item '{name}' must use sub.main naming.");
            var size = new FileInfo(file).Length;
            if (size > 16L * 1024 * 1024 * 1024) return FirmwareOpResult.Fail($"AMLogic item '{name}' is too large.");
            dataOffset = Align(dataOffset, alignment);
            items.Add((name[(separator + 1)..], name[..separator], dataOffset, size));
            dataOffset = checked(dataOffset + size);
        }
        var temp = outputPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            await using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
            var header = new byte[72];
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 2);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), Magic);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(12), checked((ulong)dataOffset));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), alignment);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), checked((uint)items.Count));
            await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            foreach (var item in items)
            {
                var record = new byte[576];
                BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(8), checked((ulong)item.Offset));
                BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(16), checked((ulong)item.Size));
                Encoding.UTF8.GetBytes(item.Main).AsSpan(0, Math.Min(item.Main.Length, 256)).CopyTo(record.AsSpan(24));
                Encoding.UTF8.GetBytes(item.Sub).AsSpan(0, Math.Min(item.Sub.Length, 256)).CopyTo(record.AsSpan(280));
                await output.WriteAsync(record, cancellationToken).ConfigureAwait(false);
            }
            var position = output.Position;
            foreach (var item in items)
            {
                while (position < item.Offset) { var zero = new byte[(int)Math.Min(4096, item.Offset - position)]; await output.WriteAsync(zero, cancellationToken).ConfigureAwait(false); position += zero.Length; }
                await using var input = new FileStream(Path.Combine(inputPath, item.Sub + "." + item.Main), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                await input.CopyToAsync(output, 65536, cancellationToken).ConfigureAwait(false);
                position += item.Size;
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            await output.DisposeAsync().ConfigureAwait(false);
            File.Move(temp, outputPath, true);
            return FirmwareOpResult.Ok($"Packed {items.Count} AMLogic items to {outputPath}", entries: [.. items.Select(x => x.Sub + "." + x.Main)]);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }

    /// <summary>Validates AMLogic header, table ranges and item alignment without extracting data.</summary>
    public Task<FirmwareOpResult> VerifyAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var image = Parse(filePath);
            cancellationToken.ThrowIfCancellationRequested();
            if (image.DeclaredSize != checked((ulong)image.Length))
                return Task.FromResult(FirmwareOpResult.Fail($"AMLogic image size declares {image.DeclaredSize}, actual length is {image.Length}."));
            if (image.Alignment is not (4 or 8))
                return Task.FromResult(FirmwareOpResult.Fail($"Unsupported AMLogic item alignment: {image.Alignment}."));
            foreach (var item in image.Items)
            {
                if (item.Offset % image.Alignment != 0 || item.Offset + item.Size > image.Length)
                    return Task.FromResult(FirmwareOpResult.Fail($"AMLogic item '{item.SubType}.{item.MainType}' is misaligned or outside the image."));
            }
            return Task.FromResult(FirmwareOpResult.Ok($"AMLogic structure verified: {image.Items.Count} items; CRC/VERIFY semantics require external specification."));
        }
        catch (Exception ex) { return Task.FromResult(FirmwareOpResult.Fail(ex.Message)); }
    }

    private static long Align(long value, uint alignment) => checked((value + alignment - 1) / alignment * alignment);

    private static AmlImage Parse(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[72];
        stream.ReadExactly(header);
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]);
        if (magic != Magic) throw new InvalidDataException("AMLogic magic is invalid.");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(header[28..32]);
        if (count == 0 || count > 65536) throw new InvalidDataException("AMLogic item count is outside the limit.");
        var alignment = BinaryPrimitives.ReadUInt32LittleEndian(header[24..28]);
        var items = new List<AmlItem>(checked((int)count));
        for (var i = 0; i < count; i++)
        {
            var bytes = new byte[576]; stream.ReadExactly(bytes);
            var offset = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8));
            var size = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(16));
            var main = ReadName(bytes.AsSpan(24, 256));
            var sub = ReadName(bytes.AsSpan(280, 256));
            items.Add(new AmlItem(checked((long)offset), checked((long)size), main, sub));
        }
        return new AmlImage(BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]), alignment, items, stream.Length,
            BinaryPrimitives.ReadUInt64LittleEndian(header[12..20]));
    }

    private static string ReadName(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes).TrimEnd('\0');
    private static async Task CopyExactlyAsync(Stream input, Stream output, long length, CancellationToken ct)
    {
        var buffer = new byte[128 * 1024];
        while (length > 0) { var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length)), ct).ConfigureAwait(false); if (read == 0) throw new EndOfStreamException(); await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false); length -= read; }
    }

    private sealed record AmlImage(uint Version, uint Alignment, List<AmlItem> Items, long Length, ulong DeclaredSize);
    private sealed record AmlItem(long Offset, long Size, string MainType, string SubType);
}