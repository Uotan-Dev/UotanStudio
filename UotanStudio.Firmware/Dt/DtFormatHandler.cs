using UotanStudio.Firmware.Common;

namespace UotanStudio.Firmware.Dt;

/// <summary>
/// <para>设备树 DTB/DTBO 格式的 IFormatHandler 实现。</para>
/// IFormatHandler implementation for the device tree DTB/DTBO formats.
/// </summary>
public sealed class DtFormatHandler : IFormatHandler
{
    /// <inheritdoc />
    public string FormatName => "Device Tree (DTB/DTBO)";

    /// <inheritdoc />
    public IReadOnlyList<string> FileExtensions => ["dtb", "dtbo", "img"];

    /// <inheritdoc />
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
            // DTB 与 DTBO 魔数均按大端存储（AOSP fdt/dt_table 头均为大端）。
            // Both DTB and DTBO magics are stored big-endian (AOSP fdt/dt_table headers are big-endian).
            var magicValue = (uint)((magic[0] << 24) | (magic[1] << 16) | (magic[2] << 8) | magic[3]);
            return magicValue == FdtConstants.FdtMagic || magicValue == FdtConstants.DtboMagic;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public Task<FirmwareOpResult> ReadMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var bytes = File.ReadAllBytes(filePath);
            var isDtbo = bytes.Length >= 4
                && (uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]) == FdtConstants.DtboMagic;

            if (isDtbo)
            {
                var (header, _, _) = DtboFormat.Read(bytes);
                var summary = $"DTBO image: {header.EntryCount} entries, page size {header.PageSize}";
                return Task.FromResult(FirmwareOpResult.Ok(summary, metadata: new Dictionary<string, string>
                {
                    ["EntryCount"] = header.EntryCount.ToString(),
                    ["PageSize"] = header.PageSize.ToString(),
                    ["Version"] = header.Version.ToString()
                }));
            }

            var tree = DtbFormat.Read(bytes);
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
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail(ex.Message));
        }
    }

    /// <inheritdoc />
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory, UnpackOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var bytes = File.ReadAllBytes(filePath);
            Directory.CreateDirectory(outputDirectory);

            var isDtbo = bytes.Length >= 4
                && (uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]) == FdtConstants.DtboMagic;

            if (isDtbo)
            {
                var (header, _, dtbs) = DtboFormat.Read(bytes);
                var written = new List<string>();
                for (var i = 0; i < dtbs.Count; i++)
                {
                    var outPath = Path.Combine(outputDirectory, $"overlay_{i:D2}.dtb");
                    File.WriteAllBytes(outPath, dtbs[i]);
                    written.Add(outPath);
                }
                return Task.FromResult(FirmwareOpResult.Ok($"Extracted {dtbs.Count} overlays to {outputDirectory}", entries: written));
            }

            // DTB: dump an FDT summary text and export the tree as DTS text.
            var tree = DtbFormat.Read(bytes);
            var dtsPath = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(filePath) + ".dts");
            File.WriteAllText(dtsPath, SerializeDts(tree));
            var summaryPath = Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(filePath) + ".fdt.txt");
            File.WriteAllText(summaryPath, Describe(tree));
            return Task.FromResult(FirmwareOpResult.Ok($"Extracted DTS to {outputDirectory}", entries: [dtsPath, summaryPath]));
        }
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail(ex.Message));
        }
    }

    /// <inheritdoc />
    public Task<FirmwareOpResult> PackAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
    {
        try
        {
            // inputPath may be a DTS text file (parsed minimally) or a raw DTB that gets rewritten.
            if (!File.Exists(inputPath))
            {
                return Task.FromResult(FirmwareOpResult.Fail("Input file does not exist."));
            }

            var bytes = File.ReadAllBytes(inputPath);
            var isDtb = bytes.Length >= 4
                && (uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]) == FdtConstants.FdtMagic;
            if (isDtb)
            {
                var tree = DtbFormat.Read(bytes);
                var outBytes = DtbFormat.Write(tree);
                File.WriteAllBytes(outputPath, outBytes);
                return Task.FromResult(FirmwareOpResult.Ok($"Repacked DTB to {outputPath}"));
            }

            return Task.FromResult(FirmwareOpResult.Fail("Input must be a DTB file for packing."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(FirmwareOpResult.Fail(ex.Message));
        }
    }

    private static int CountNodes(DeviceTreeNode node) => 1 + node.Children.Sum(CountNodes);

    private static int CountProperties(DeviceTreeNode node) => node.Properties.Count + node.Children.Sum(CountProperties);

    private static string Describe(DeviceTree tree)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Device tree blob (FDT v{tree.Version})");
        sb.AppendLine($"Nodes: {CountNodes(tree.Root)}  Properties: {CountProperties(tree.Root)}  Reservations: {tree.MemoryReservations.Count}");
        return sb.ToString();
    }

    private static string SerializeDts(DeviceTree tree)
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
        // Cells are common; treat data as cells unless it looks like a string.
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