using System.Text;
using System.Text.RegularExpressions;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Components;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.BootImage.Models;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Patcher.Kernel;
using UotanStudio.Firmware.SharedKernel;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.Kernel;

/// <summary>Bounded metadata returned by the managed kernel toolchain.
/// <para>托管内核工具链返回的有界元数据。</para></summary>
/// <param name="Compression">Compression detected from the leading bytes of the kernel image.
/// <para>根据内核镜像起始字节检测出的压缩格式。</para></param>
/// <param name="Release">Linux release string extracted from the decompressed kernel, or <see langword="null"/>
/// when no pattern matches. <para>从解压后内核中提取的 Linux 版本号；无匹配时返回 <see langword="null"/>。</para></param>
/// <param name="Kmi">Android KMI identifier derived from the release, or <see langword="null"/> when absent.
/// <para>由版本号推导的 Android KMI 标识；不存在时返回 <see langword="null"/>。</para></param>
/// <param name="InputLength">Size of the input file in bytes. <para>输入文件的字节大小。</para></param>
/// <param name="DecompressedLength">Size of the decompressed kernel in bytes; equals <paramref name="InputLength"/>
/// for uncompressed input. <para>解压后内核的字节大小；未压缩输入时与 <paramref name="InputLength"/> 相等。</para></param>
public sealed record KernelToolchainMetadata(
    CompressionType Compression,
    string? Release,
    string? Kmi,
    long InputLength,
    long DecompressedLength);

/// <summary>Provides bounded kernel inspection and decompression operations.
/// <para>提供有界限制的内核检查与解压操作。</para></summary>
public static partial class KernelToolchainService
{
    private const long MaximumKernelBytes = 512L * 1024 * 1024;

    /// <summary>Reads compression and Linux release metadata from a kernel image.
    /// <para>从内核镜像读取压缩格式与 Linux 版本元数据。</para></summary>
    /// <param name="inputPath">
    /// Path to a raw or compressed kernel image; the file must not exceed 512 MiB.
    /// <para>原始或压缩内核镜像的路径；文件不得超过 512 MiB。</para>
    /// </param>
    /// <param name="cancellationToken">Cancels the file read. <para>取消文件读取。</para></param>
    /// <returns>Metadata describing the compression, release, KMI and the input and decompressed lengths.
    /// <para>描述压缩格式、版本号、KMI 以及输入与解压后长度的元数据。</para></returns>
    /// <exception cref="FileNotFoundException"><paramref name="inputPath"/> does not exist.
    /// <para><paramref name="inputPath"/> 不存在。</para></exception>
    /// <exception cref="InvalidDataException">The image exceeds 512 MiB or decompression of the detected
    /// format fails. <para>镜像超过 512 MiB，或按检测出的格式解压失败。</para></exception>
    /// <exception cref="OperationCanceledException">The token is cancelled while reading.
    /// <para>读取期间令牌被取消。</para></exception>
    /// <example>
    /// <code>
    /// var meta = await KernelToolchainService.AnalyzeAsync("Image.gz");
    /// </code>
    /// </example>
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

    /// <summary>Decompresses a kernel to a temporary file and atomically publishes the result.
    /// <para>将内核解压到临时文件，并以原子方式发布结果。</para></summary>
    /// <param name="inputPath">
    /// Path to a raw or compressed kernel image; the file must not exceed 512 MiB.
    /// <para>原始或压缩内核镜像的路径；文件不得超过 512 MiB。</para>
    /// </param>
    /// <param name="outputPath">
    /// Destination file; an existing file is replaced only after the decompressed kernel passes the size
    /// check, and publishing overwrites.
    /// <para>目标文件；只有解压后的内核通过大小检查后才会替换已有文件，发布时允许覆盖。</para>
    /// </param>
    /// <param name="cancellationToken">Cancels the read; the existing output stays intact.
    /// <para>取消读取；既有输出保持不变。</para></param>
    /// <returns>A task that completes once the decompressed kernel has been published.
    /// <para>解压结果发布完成后结束的异步任务。</para></returns>
    /// <exception cref="FileNotFoundException"><paramref name="inputPath"/> does not exist.
    /// <para><paramref name="inputPath"/> 不存在。</para></exception>
    /// <exception cref="InvalidDataException">The input exceeds 512 MiB, decompression fails, or the
    /// decompressed kernel exceeds 512 MiB. <para>输入超过 512 MiB、解压失败，或解压后的内核超过 512 MiB。</para></exception>
    /// <exception cref="OperationCanceledException">The token is cancelled while reading.
    /// <para>读取期间令牌被取消。</para></exception>
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

    /// <summary>Parses a textual kallsyms dump without interpreting binary kernel token tables.
    /// <para>解析文本格式的 kallsyms 转储，不解读内核二进制符号表。</para></summary>
    /// <param name="path">
    /// Path to a text file whose lines follow the <c>address type name</c> kallsyms layout; lines that do
    /// not match are ignored.
    /// <para>文本文件路径，每行遵循 <c>address type name</c> 的 kallsyms 布局；不匹配的行会被忽略。</para>
    /// </param>
    /// <param name="cancellationToken">Cancels the line-by-line read. <para>取消逐行读取。</para></param>
    /// <returns>The parsed symbols in file order; empty when no line matches.
    /// <para>按文件顺序返回的符号；没有匹配行时为空。</para></returns>
    /// <exception cref="FileNotFoundException"><paramref name="path"/> does not exist.
    /// <para><paramref name="path"/> 不存在。</para></exception>
    /// <exception cref="InvalidDataException">The file contains more than 4,000,000 symbols.
    /// <para>文件包含超过 4,000,000 个符号。</para></exception>
    /// <exception cref="OperationCanceledException">The token is cancelled during the read.
    /// <para>读取期间令牌被取消。</para></exception>
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

    /// <summary>Locates and decodes a compressed binary kallsyms table from a raw or compressed kernel image.
    /// <para>从原始或压缩内核镜像中定位并解码压缩的二进制 kallsyms 表。</para></summary>
    /// <param name="path">
    /// Path to a kernel image file; the file must not exceed 512 MiB.
    /// <para>内核镜像文件路径；文件不得超过 512 MiB。</para>
    /// </param>
    /// <param name="cancellationToken">Cancels the read and the decode hand-off.
    /// <para>取消读取与解码交接。</para></param>
    /// <returns>The decoded symbols and their table metadata.
    /// <para>解码得到的符号及其符号表元数据。</para></returns>
    /// <exception cref="FileNotFoundException"><paramref name="path"/> does not exist.
    /// <para><paramref name="path"/> 不存在。</para></exception>
    /// <exception cref="InvalidDataException">The input exceeds 512 MiB, kernel decompression fails, or the
    /// decompressed kernel exceeds 512 MiB. <para>输入超过 512 MiB、内核解压失败，或解压后的内核超过 512 MiB。</para></exception>
    /// <exception cref="OperationCanceledException">The token is cancelled while reading.
    /// <para>读取期间令牌被取消。</para></exception>
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

    /// <summary>Extracts a binary kallsyms table to a conventional text file using atomic publication.
    /// <para>将二进制 kallsyms 表提取为常规文本文件，并以原子方式发布。</para></summary>
    /// <param name="inputPath">
    /// Path to a raw or compressed kernel image; the file must not exceed 512 MiB.
    /// <para>原始或压缩内核镜像的路径；文件不得超过 512 MiB。</para>
    /// </param>
    /// <param name="outputPath">
    /// Destination text file written as UTF-8 with lines of the form
    /// <c>16-hex-digit address type name</c>; publishing replaces an existing file only after the full
    /// table is written.
    /// <para>目标文本文件，以 UTF-8 写入，每行形如 <c>16 位十六进制地址 类型 名称</c>；整表写入完成后才替换已有文件。</para>
    /// </param>
    /// <param name="cancellationToken">Cancels reads and symbol writing. <para>取消读取与符号写入。</para></param>
    /// <returns>The metadata of the decoded kallsyms table.
    /// <para>解码后的 kallsyms 表元数据。</para></returns>
    /// <exception cref="FileNotFoundException"><paramref name="inputPath"/> does not exist.
    /// <para><paramref name="inputPath"/> 不存在。</para></exception>
    /// <exception cref="InvalidDataException">The input or decompressed kernel exceeds 512 MiB, or kernel
    /// decompression fails. <para>输入或解压后的内核超过 512 MiB，或内核解压失败。</para></exception>
    /// <exception cref="OperationCanceledException">The token is cancelled during the read or the write.
    /// <para>读取或写入期间令牌被取消。</para></exception>
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

    /// <summary>Creates an ELF64 executable containing the raw kernel and a decoded kallsyms symbol table.
    /// <para>生成一个 ELF64 可执行文件，内含原始内核与解码出的 kallsyms 符号表。</para></summary>
    /// <param name="inputPath">
    /// Path to a raw or compressed kernel image; the file must not exceed 512 MiB.
    /// <para>原始或压缩内核镜像的路径；文件不得超过 512 MiB。</para>
    /// </param>
    /// <param name="outputPath">Destination ELF file written by the symbolized-ELF writer.
    /// <para>由符号化 ELF 写入器生成的目标 ELF 文件。</para></param>
    /// <param name="options">
    /// Writer options; <see langword="null"/> uses the defaults of <c>KernelElfOptions</c>.
    /// <para>写入器选项；为 <see langword="null"/> 时使用 <c>KernelElfOptions</c> 的默认值。</para>
    /// </param>
    /// <param name="cancellationToken">Cancels reads and the ELF write. <para>取消读取与 ELF 写入。</para></param>
    /// <returns>The writer result describing the generated ELF.
    /// <para>描述已生成 ELF 的写入器结果。</para></returns>
    /// <exception cref="FileNotFoundException"><paramref name="inputPath"/> does not exist.
    /// <para><paramref name="inputPath"/> 不存在。</para></exception>
    /// <exception cref="InvalidDataException">The input or decompressed kernel exceeds 512 MiB, or kernel
    /// decompression fails. <para>输入或解压后的内核超过 512 MiB，或内核解压失败。</para></exception>
    /// <exception cref="OperationCanceledException">The token is cancelled during a read or write.
    /// <para>读取或写入期间令牌被取消。</para></exception>
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

/// <summary>A parsed line from a textual kallsyms dump.
/// <para>从文本 kallsyms 转储中解析出的一行。</para></summary>
/// <param name="Address">Symbol address parsed as hexadecimal. <para>按十六进制解析的符号地址。</para></param>
/// <param name="Type">Single-letter kallsyms symbol type. <para>单字母的 kallsyms 符号类型。</para></param>
/// <param name="Name">Symbol name as written in the dump. <para>转储中原样记录的符号名。</para></param>
public readonly record struct KernelSymbol(ulong Address, char Type, string Name);