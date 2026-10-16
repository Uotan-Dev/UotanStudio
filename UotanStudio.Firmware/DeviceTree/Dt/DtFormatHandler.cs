using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;
using DtTree = UotanStudio.Firmware.DeviceTree.Dt.DeviceTree;

namespace UotanStudio.Firmware.DeviceTree.Dt;

/// <summary>
/// Format handler that recognizes DTB and DTBO images by extension or magic, reads their metadata, extracts
/// overlays or DTS text, and repacks them atomically.
/// <para>格式处理器：按扩展名或魔数识别 DTB 与 DTBO 镜像，读取元数据，提取 overlay 或 DTS 文本，并原子重打包。</para>
/// </summary>
public sealed class DtFormatHandler : IFormatHandler, IRepackMetadataProvider
{
    void IRepackMetadataProvider.ConfigureRepackMetadata(RepackMetadata metadata, string sourcePath, string outputDirectory)
    {
        using var input = File.OpenRead(sourcePath);
        Span<byte> magic = stackalloc byte[4];
        input.ReadExactly(magic);
        if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(magic) == FdtConstants.DtboMagic)
        {
            metadata.Configuration = DtboFormat.CreateConfiguration(input);
        }
        metadata.CanPackDirectory = metadata.Configuration.Count > 0;
    }

    /// <summary>Display name of the handled format family.
    /// <para>所处理格式家族的显示名称。</para></summary>
    /// <value>Always <c>Device Tree (DTB/DTBO)</c>. <para>恒为 <c>Device Tree (DTB/DTBO)</c>。</para></value>
    public string FormatName => "Device Tree (DTB/DTBO)";

    /// <summary>Lowercase file extensions this handler claims.
    /// <para>本处理器声称的小写文件扩展名。</para></summary>
    /// <value><c>dtb</c>, <c>dtbo</c> and <c>img</c>; <c>img</c> still requires a magic match in
    /// <see cref="CanHandle"/> for content dispatch. <para><c>dtb</c>、<c>dtbo</c> 与 <c>img</c>；
    /// <c>img</c> 仍需由 <see cref="CanHandle"/> 魔数匹配来决定内容分派。</para></value>
    public IReadOnlyList<string> FileExtensions => ["dtb", "dtbo", "img"];

    /// <summary>
    /// Determines whether the file matches this format.
    /// <para>判断文件是否匹配此格式。</para>
    /// </summary>
    /// <param name="filePath">Path to test; a missing or unreadable path is treated as no match.
    /// <para>待测试的路径；路径不存在或不可读时视为不匹配。</para></param>
    /// <returns><see langword="true"/> for <c>.dtb</c>/<c>.dtbo</c> extensions or files whose first four
    /// big-endian bytes carry the FDT or DTBO magic. <para>扩展名为 <c>.dtb</c>/<c>.dtbo</c>，或前四大端字节为
    /// FDT/DTBO 魔数时返回 <see langword="true"/>。</para></returns>
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }
        var ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
        if (ext is "dtb" or "dtbo")
        {
            return true;
        }
        Span<byte> fdtMagic = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(fdtMagic, FdtConstants.FdtMagic);
        Span<byte> dtboMagic = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(dtboMagic, FdtConstants.DtboMagic);
        return HeaderProbe.ProbeMagic(filePath, sizeof(uint), 0, fdtMagic)
               || HeaderProbe.ProbeMagic(filePath, sizeof(uint), 0, dtboMagic);
    }

    /// <summary>
    /// Reads the firmware metadata asynchronously.
    /// <para>异步读取固件元数据。</para>
    /// </summary>
    /// <param name="filePath">DTB or DTBO file to inspect. <para>要检查的 DTB 或 DTBO 文件。</para></param>
    /// <param name="cancellationToken">Cancels before any read; cancellation surfaces as
    /// <see cref="OperationCanceledException"/>. <para>在读取前取消；取消以 <see cref="OperationCanceledException"/> 呈现。</para></param>
    /// <returns>A completed task whose result summarizes entries or node counts with a metadata dictionary;
    /// malformed or unreadable input yields a failed result instead of throwing.
    /// <para>已完成的任务，其结果汇总条目数或节点数并附带元数据字典；输入无效或不可读时返回失败结果而不抛异常。</para></returns>
    /// <exception cref="OperationCanceledException">The token was canceled. <para>令牌被取消。</para></exception>
    public Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var input = File.OpenRead(filePath);
            var isDtbo = IsDtbo(input);

            if (isDtbo)
            {
                var (header, _) = DtboFormat.ReadTable(input);
                var summary = $"DTBO image: {header.EntryCount} entries, page size {header.PageSize}";
                return Task.FromResult(FirmwareOpResult.Ok(summary, metadata: new Dictionary<string, string>
                {
                    ["EntryCount"] = header.EntryCount.ToString(),
                    ["PageSize"] = header.PageSize.ToString(),
                    ["Version"] = header.Version.ToString()
                }));
            }

            var tree = DtbFormat.Read(ReadDtb(input));
            var nodeCount = CountNodes(tree.Root);
            var propCount = CountProperties(tree.Root);
            var dtbSummary = $"Device tree blob (FDT v{tree.Version}): {nodeCount} nodes, {propCount} properties, " +
                          $"{tree.MemoryReservations.Count} memory reservations";
            return Task.FromResult(FirmwareOpResult.Ok(dtbSummary, metadata: new Dictionary<string, string>
            {
                ["Version"] = tree.Version.ToString(),
                ["NodeCount"] = nodeCount.ToString(),
                ["PropertyCount"] = propCount.ToString(),
                ["ReservationCount"] = tree.MemoryReservations.Count.ToString(),
                ["BootCpuId"] = tree.BootCpuId.ToString()
            }));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Task.FromResult(FirmwareOpResult.Fail(ex.Message));
        }
    }

    /// <summary>
    /// Extracts the firmware contents asynchronously.
    /// <para>异步提取固件内容。</para>
    /// </summary>
    /// <param name="filePath">DTB or DTBO image to extract. <para>要提取的 DTB 或 DTBO 镜像。</para></param>
    /// <param name="outputDirectory">Destination directory, created when missing; DTBO writes overlays plus
    /// repack metadata, DTB writes <c>.dts</c> and a summary text file.
    /// <para>目标目录，缺失时创建；DTBO 写出 overlay 与重打包元数据，DTB 写出 <c>.dts</c> 与摘要文本文件。</para></param>
    /// <param name="options">Optional output-size limit, overwrite policy and progress sink; <see langword="null"/>
    /// uses defaults. <para>可选的输出大小上限、覆盖策略与进度回调；为 <see langword="null"/> 时使用默认值。</para></param>
    /// <param name="cancellationToken">Cancels staged extraction; a canceled run leaves the destination untouched.
    /// <para>取消暂存式提取；被取消的运行不会改动目标目录。</para></param>
    /// <returns>A task whose result lists the written files on success or carries the failure message;
    /// format errors do not throw. <para>任务结果在成功时列出已写文件，失败时携带错误消息；格式错误不抛异常。</para></returns>
    /// <exception cref="OperationCanceledException">The token was canceled. <para>令牌被取消。</para></exception>
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
        => RepackMetadata.UnpackAsync(this, filePath, outputDirectory, options,
            cancellationToken, () => UnpackCoreAsync(filePath, outputDirectory, options, cancellationToken));

    private async Task<FirmwareOpResult> UnpackCoreAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var input = File.OpenRead(filePath);
            var isDtbo = IsDtbo(input);

            if (isDtbo)
            {
                var written = await DtboFormat.UnpackAsync(filePath, outputDirectory, options, cancellationToken).ConfigureAwait(false);
                return FirmwareOpResult.Ok($"Extracted DTBO overlays and repack configuration to {outputDirectory}", entries: written);
            }


            Directory.CreateDirectory(outputDirectory);
            var tree = DtbFormat.Read(ReadDtb(input));
            var dtsPath = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(filePath) + ".dts");
            File.WriteAllText(dtsPath, SerializeDts(tree));
            var summaryPath = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(filePath) + ".fdt.txt");
            File.WriteAllText(summaryPath, Describe(tree));
            return FirmwareOpResult.Ok($"Extracted DTS to {outputDirectory}", entries: [dtsPath, summaryPath]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return FirmwareOpResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Packs the input into firmware asynchronously.
    /// <para>异步将输入打包为固件。</para>
    /// </summary>
    /// <param name="inputPath">A directory containing DTBO repack metadata, or a DTB/DTBO file; a DTBO file
    /// is revalidated and copied whole, a DTB is parsed and rewritten.
    /// <para>包含 DTBO 重打包元数据的目录，或 DTB/DTBO 文件；DTBO 文件重新校验后整体复制，DTB 解析后重写。</para></param>
    /// <param name="outputPath">Destination file, replaced atomically via a temporary file.
    /// <para>目标文件，通过临时文件原子替换。</para></param>
    /// <param name="cancellationToken">Cancels the write; the temporary output is deleted and the previous
    /// destination content is kept. <para>取消写入；临时输出被删除并保留目标的原有内容。</para></param>
    /// <returns>A task whose result reports success with the output path or a failure message for a missing
    /// or unrecognized input; format errors do not throw.
    /// <para>任务结果在成功时报告输出路径，在输入缺失或无法识别时给出失败消息；格式错误不抛异常。</para></returns>
    /// <exception cref="OperationCanceledException">The token was canceled. <para>令牌被取消。</para></exception>
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(inputPath))
            {
                DtboFormat.PackDirectory(inputPath, outputPath, cancellationToken);
                return Task.FromResult(FirmwareOpResult.Ok($"Repacked DTBO to {outputPath}"));
            }
            if (!File.Exists(inputPath))
            {
                return Task.FromResult(FirmwareOpResult.Fail("Input file does not exist."));
            }

            byte[] bytes = [];
            bool isDtbo;
            using (var input = File.OpenRead(inputPath))
            {
                isDtbo = IsDtbo(input);
                if (!isDtbo) bytes = ReadDtb(input);
            }
            if (isDtbo)
            {
                DtboFormat.Repack(inputPath, outputPath, cancellationToken);
                return Task.FromResult(FirmwareOpResult.Ok($"Repacked DTBO to {outputPath}"));
            }
            var isDtb = bytes.Length >= 4
                && (uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]) == FdtConstants.FdtMagic;
            if (isDtb)
            {
                var tree = DtbFormat.Read(bytes);
                var outBytes = DtbFormat.Write(tree);
                DtboFormat.AtomicWrite(outputPath, output => output.Write(outBytes), cancellationToken);
                return Task.FromResult(FirmwareOpResult.Ok($"Repacked DTB to {outputPath}"));
            }

            return Task.FromResult(FirmwareOpResult.Fail("Input must be a DTB, DTBO, or a directory containing DTBO repack metadata."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Task.FromResult(FirmwareOpResult.Fail(ex.Message));
        }
    }

    private static bool IsDtbo(Stream input)
    {
        Span<byte> magic = stackalloc byte[4];
        input.Position = 0;
        return input.Read(magic) == 4
            && System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(magic) == FdtConstants.DtboMagic;
    }

    private static byte[] ReadDtb(Stream input)
    {
        if (input.Length > DtboFormat.DefaultMaximumDecompressedBytes)
            throw new InvalidDataException("DTB exceeds the 256 MiB metadata limit.");
        input.Position = 0;
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        return bytes;
    }

    private static int CountNodes(DeviceTreeNode node) => 1 + node.Children.Sum(CountNodes);

    private static int CountProperties(DeviceTreeNode node) => node.Properties.Count + node.Children.Sum(CountProperties);

    private static string Describe(DtTree tree)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Device tree blob (FDT v{tree.Version})");
        sb.AppendLine($"Nodes: {CountNodes(tree.Root)}  Properties: {CountProperties(tree.Root)}  Reservations: {tree.MemoryReservations.Count}");
        return sb.ToString();
    }

    private static string SerializeDts(DtTree tree)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("/dts-v1/;");
        foreach (var rsv in tree.MemoryReservations)
        {
            sb.AppendLine($"/memreserve/ 0x{rsv.Address:x16} 0x{rsv.Size:x16};");
        }
        WriteNodeDts(sb, tree.Root, 0);
        return sb.ToString();
    }

    private static void WriteNodeDts(System.Text.StringBuilder sb, DeviceTreeNode node, int depth)
    {
        var indent = new string('\t', depth);
        var name = string.IsNullOrEmpty(node.Name) ? "/" : node.Name;
        sb.Append(indent).Append(name).AppendLine(" {");

        foreach (var property in node.Properties)
        {
            sb.Append(indent).Append('\t').Append(property.Name);
            if (property.IsEmpty)
            {
                sb.AppendLine(";");
            }
            else
            {
                sb.Append(" = ").Append(FormatPropertyValue(property)).AppendLine(";");
            }
        }

        foreach (var child in node.Children)
        {
            WriteNodeDts(sb, child, depth + 1);
        }

        sb.Append(indent).AppendLine("};");
    }

    private static string FormatPropertyValue(DeviceTreeProperty property)
    {
        var bytes = property.ValueBytes;
        if (bytes.Length % 4 == 0 && IsLikelyCells(bytes))
        {
            var cells = property.GetCells();
            return "[" + string.Join(" ", cells.Select(c => $"{c:x8}")) + "]";
        }
        var text = property.GetString();
        return text.Length > 0 && !text.Any(c => char.IsControl(c) && c != 0)
            ? $"\"{text}\""
            : "[" + string.Join(" ", bytes.Select(b => $"{b:x2}")) + "]";
    }

    private static bool IsLikelyCells(byte[] bytes)
    {

        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == 0)
            {
                continue;
            }
            if (bytes[i] is >= 0x20 and <= 0x7E)
            {
                continue;
            }
            return true;
        }
        return false;
    }
}