using UotanStudio.Firmware.BoundedContexts.DeviceTree.Dt.Dtb;
using UotanStudio.Firmware.BoundedContexts.DeviceTree.Dt.Dtbo;
using UotanStudio.Firmware.BoundedContexts.DeviceTree.Dt.Models;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Repacking;
using DtTree = UotanStudio.Firmware.BoundedContexts.DeviceTree.Dt.Models.DeviceTree;

namespace UotanStudio.Firmware.BoundedContexts.DeviceTree.Dt;

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

    public string FormatName => "Device Tree (DTB/DTBO)";

    public IReadOnlyList<string> FileExtensions => ["dtb", "dtbo", "img"];

    /// <summary>
    /// Determines whether the file matches this format.
    /// <para>判断文件是否匹配此格式。</para>
    /// </summary>
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
        try
        {
            using var fs = File.OpenRead(filePath);
            Span<byte> magic = stackalloc byte[4];
            if (fs.Read(magic) != 4)
            {
                return false;
            }


            var magicValue = (uint)((magic[0] << 24) | (magic[1] << 16) | (magic[2] << 8) | magic[3]);
            return magicValue == FdtConstants.FdtMagic || magicValue == FdtConstants.DtboMagic;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the firmware metadata asynchronously.
    /// <para>异步读取固件元数据。</para>
    /// </summary>
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
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
        => UotanStudio.Firmware.SharedKernel.Repacking.RepackMetadata.UnpackAsync(this, filePath, outputDirectory, options,
            cancellationToken, () => UnpackCoreAsync(filePath, outputDirectory, options, cancellationToken));

    private Task<FirmwareOpResult> UnpackCoreAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var input = File.OpenRead(filePath);
            var isDtbo = IsDtbo(input);

            if (isDtbo)
            {
                var written = DtboFormat.Unpack(filePath, outputDirectory, options, cancellationToken);
                return Task.FromResult(FirmwareOpResult.Ok($"Extracted DTBO overlays and repack configuration to {outputDirectory}", entries: written));
            }


            Directory.CreateDirectory(outputDirectory);
            var tree = DtbFormat.Read(ReadDtb(input));
            var dtsPath = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(filePath) + ".dts");
            File.WriteAllText(dtsPath, SerializeDts(tree));
            var summaryPath = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(filePath) + ".fdt.txt");
            File.WriteAllText(summaryPath, Describe(tree));
            return Task.FromResult(FirmwareOpResult.Ok($"Extracted DTS to {outputDirectory}", entries: [dtsPath, summaryPath]));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Task.FromResult(FirmwareOpResult.Fail(ex.Message));
        }
    }

    /// <summary>
    /// Packs the input into firmware asynchronously.
    /// <para>异步将输入打包为固件。</para>
    /// </summary>
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