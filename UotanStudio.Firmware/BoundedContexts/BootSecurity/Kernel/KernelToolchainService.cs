using System.Text;
using System.Text.RegularExpressions;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Components;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Models;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Patcher.Kernel;
using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.Kernel;

/// <summary>Bounded metadata returned by the managed kernel toolchain.</summary>
public sealed record KernelToolchainMetadata(
    CompressionType Compression,
    string? Release,
    string? Kmi,
    long InputLength,
    long DecompressedLength);

/// <summary>Provides bounded kernel inspection and decompression operations.</summary>
public static partial class KernelToolchainService
{
    private const long MaximumKernelBytes = 512L * 1024 * 1024;

    /// <summary>Reads compression and Linux release metadata from a kernel image.</summary>
    public static async Task<KernelToolchainMetadata> AnalyzeAsync(string inputPath,
        CancellationToken cancellationToken = default)
    {
        var data = await ReadBoundedAsync(inputPath, cancellationToken).ConfigureAwait(false);
        var compression = BootCompressor.Detect(data.AsSpan(0, Math.Min(data.Length, 16)).ToArray());
        var decompressed = compression == CompressionType.None ? data : BootCompressor.TryDecompress(data, compression)
            ?? throw new InvalidDataException($"Unable to decompress kernel as {compression}.");
        return new(compression, KernelVersionExtractor.ExtractRelease(decompressed),
            KernelVersionExtractor.ExtractKmi(decompressed), data.LongLength, decompressed.LongLength);
    }

    /// <summary>Decompresses a kernel to a temporary file and atomically publishes the result.</summary>
    public static async Task DecompressAsync(string inputPath, string outputPath,
        CancellationToken cancellationToken = default)
    {
        var data = await ReadBoundedAsync(inputPath, cancellationToken).ConfigureAwait(false);
        var type = BootCompressor.Detect(data.AsSpan(0, Math.Min(data.Length, 16)).ToArray());
        var decoded = type == CompressionType.None ? data : BootCompressor.TryDecompress(data, type)
            ?? throw new InvalidDataException($"Unable to decompress kernel as {type}.");
        if (decoded.LongLength > MaximumKernelBytes)
            throw new InvalidDataException("Decompressed kernel exceeds the configured limit.");
        FileExtraction.WriteAtomic(outputPath, output => output.Write(decoded), overwrite: true);
    }

    /// <summary>Parses a textual kallsyms dump without interpreting binary kernel token tables.</summary>
    public static async Task<IReadOnlyList<KernelSymbol>> ReadKallsymsTextAsync(string path,
        CancellationToken cancellationToken = default)
    {
        var result = new List<KernelSymbol>();
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var match = KallsymsLineRegex().Match(line);
            if (match.Success && ulong.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var address))
                result.Add(new(address, match.Groups[2].Value[0], match.Groups[3].Value));
            if (result.Count > 4_000_000)
                throw new InvalidDataException("kallsyms text contains too many symbols.");
        }
        return result;
    }

    /// <summary>Locates and decodes a compressed binary kallsyms table from a raw or compressed kernel image.</summary>
    public static async Task<KallsymsBinaryResult> ReadKallsymsBinaryAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var data = await ReadBoundedAsync(path, cancellationToken).ConfigureAwait(false);
        var type = BootCompressor.Detect(data.AsSpan(0, Math.Min(data.Length, 16)).ToArray());
        var decoded = type == CompressionType.None ? data : BootCompressor.TryDecompress(data, type)
            ?? throw new InvalidDataException($"Unable to decompress kernel as {type}.");
        if (decoded.LongLength > MaximumKernelBytes)
        {
            throw new InvalidDataException("Decompressed kernel exceeds the configured limit.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        return KallsymsBinaryReader.Read(decoded);
    }

    /// <summary>Extracts a binary kallsyms table to a conventional text file using atomic publication.</summary>
    public static async Task<KallsymsBinaryMetadata> ExtractKallsymsAsync(
        string inputPath,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        var result = await ReadKallsymsBinaryAsync(inputPath, cancellationToken).ConfigureAwait(false);
        await FileExtraction.WriteAtomicAsync(outputPath, async output =>
        {
            await using var writer = new StreamWriter(output, new UTF8Encoding(false), 64 * 1024, leaveOpen: true);
            foreach (var symbol in result.Symbols)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await writer.WriteLineAsync($"{symbol.Address:x16} {symbol.Type} {symbol.Name}".AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
            }
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }, overwrite: true, cancellationToken).ConfigureAwait(false);
        return result.Metadata;
    }

    /// <summary>Creates an ELF64 executable containing the raw kernel and a decoded kallsyms symbol table.</summary>
    public static async Task<KernelElfResult> BuildSymbolizedElfAsync(
        string inputPath,
        string outputPath,
        KernelElfOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var data = await ReadBoundedAsync(inputPath, cancellationToken).ConfigureAwait(false);
        var type = BootCompressor.Detect(data.AsSpan(0, Math.Min(data.Length, 16)).ToArray());
        var decoded = type == CompressionType.None ? data : BootCompressor.TryDecompress(data, type)
            ?? throw new InvalidDataException($"Unable to decompress kernel as {type}.");
        if (decoded.LongLength > MaximumKernelBytes)
        {
            throw new InvalidDataException("Decompressed kernel exceeds the configured limit.");
        }
        var kallsyms = KallsymsBinaryReader.Read(decoded);
        cancellationToken.ThrowIfCancellationRequested();
        return await KernelElfWriter.WriteAsync(
            decoded,
            kallsyms.Symbols,
            outputPath,
            options ?? new KernelElfOptions(),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Kernel image was not found.", path);
        if (info.Length > MaximumKernelBytes) throw new InvalidDataException("Kernel image exceeds the configured limit.");
        var data = new byte[checked((int)info.Length)];
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.ReadExactlyAsync(data, cancellationToken).ConfigureAwait(false);
        return data;
    }

    [GeneratedRegex("^([0-9a-fA-F]+)\\s+([A-Za-z])\\s+(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex KallsymsLineRegex();
}

/// <summary>A parsed line from a textual kallsyms dump.</summary>
public readonly record struct KernelSymbol(ulong Address, char Type, string Name);