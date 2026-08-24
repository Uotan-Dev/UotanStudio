using System;
using System.IO;
using System.Threading.Tasks;
using UotanStudio.Firmware.BootImage;
using UotanStudio.Firmware.Oppo;
using UotanStudio.Firmware.Payload;
using UotanStudio.Services;

namespace UotanStudio.Core.Inspector;

/// <summary>
/// <para>固件部件检查器：按格式解析部件文件并生成结构树，供工程树懒加载子节点。</para>
/// Firmware component inspector: parses a component file by format and produces a structure
/// tree, used by the project tree for lazy-loaded child nodes.
/// </summary>
public static class FirmwareInspector
{
    /// <summary>
    /// <para>在后台解析指定部件文件，返回结构树根节点；无法识别格式时返回 null。</para>
    /// Parses the given component file on a background thread and returns the structure root;
    /// returns null when the format cannot be recognized.
    /// </summary>
    /// <param name="path"><para>部件文件路径。</para>The component file path.</param>
    /// <returns><para>结构树根节点，无法识别时为 null。</para>The structure root, or null when unrecognized.</returns>
    public static async Task<InspectorNode?> InspectAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        return await Task.Run(async () =>
        {
            try
            {
                return await InspectCoreAsync(path);
            }
            catch (Exception ex)
            {
                return new InspectorNode(Path.GetFileName(path)) { Detail = ex.Message };
            }
        });
    }

    /// <summary>
    /// <para>快速判断文件是否可能为可解析的固件部件（按扩展名与魔数）。</para>
    /// Quickly checks whether a file is likely a parseable firmware component
    /// (by extension and magic bytes).
    /// </summary>
    /// <param name="path"><para>文件路径。</para>The file path.</param>
    /// <returns>是否可能可解析。<para>Whether the file may be parseable.</para></returns>
    public static bool CanInspect(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        return DetectKind(path) is not null;
    }

    private static async Task<InspectorNode?> InspectCoreAsync(string path)
    {
        var kind = DetectKind(path);
        if (kind is null)
        {
            return null;
        }

        var name = Path.GetFileName(path);
        return kind switch
        {
            FirmwareKind.Payload => await InspectPayloadAsync(name, path),
            FirmwareKind.CpioTar => await InspectCpioAsync(name, path),
            FirmwareKind.Oppo => await InspectOppoAsync(name, path),
            FirmwareKind.BootImage => InspectBootImage(name, path),
            _ => null
        };
    }

    private static async Task<InspectorNode> InspectPayloadAsync(string name, string path)
    {
        var metadata = await PayloadParser.GetPayloadMetadataAsync(path);
        var node = new InspectorNode(name)
        {
            Detail = $"Payload OTA: {metadata.PartitionNames.Count} partition(s), block size {metadata.BlockSize}"
        };
        foreach (var partition in metadata.PartitionNames)
        {
            node.Children.Add(new InspectorNode(partition));
        }

        return node;
    }

    private static async Task<InspectorNode> InspectCpioAsync(string name, string path)
    {
        var result = await FirmwareService.Instance.ListCpioEntriesAsync(path);
        var node = new InspectorNode(name) { Detail = result.Summary };
        if (result.Success)
        {
            foreach (var entry in result.Entries)
            {
                node.Children.Add(new InspectorNode(entry));
            }
        }

        return node;
    }

    private static async Task<InspectorNode> InspectOppoAsync(string name, string path)
    {
        var archive = await new OppReader().ParseAsync(path);
        var metadata = archive.Metadata;
        var node = new InspectorNode(name)
        {
            Detail = $"Format: {metadata.FormatName}, {metadata.EntryCount} entry(ies)"
        };
        foreach (var entry in archive.Entries)
        {
            var child = new InspectorNode(entry.Name)
            {
                Detail = entry.IsEncrypted ? "Encrypted" : $"{entry.Size} bytes"
            };
            node.Children.Add(child);
        }

        return node;
    }

    private static InspectorNode InspectBootImage(string name, string path)
    {
        using var parser = new BootImageParser();
        parser.ParseFile(path);
        var header = parser.Header;
        var node = new InspectorNode(name)
        {
            Detail = $"Boot image v{(int)header.HeaderVersion}: kernel {header.KernelSize} bytes, ramdisk {header.RamdiskSize} bytes, page size {header.PageSize}"
        };
        node.Children.Add(new InspectorNode("Kernel") { Detail = $"{header.KernelSize} bytes" });
        node.Children.Add(new InspectorNode("Ramdisk") { Detail = $"{header.RamdiskSize} bytes" });
        if (header.DtbSize > 0)
        {
            node.Children.Add(new InspectorNode("DTB") { Detail = $"{header.DtbSize} bytes" });
        }

        return node;
    }

    private static FirmwareKind? DetectKind(string path)
    {
        var name = Path.GetFileName(path);
        var ext = Path.GetExtension(path).ToLowerInvariant();

        if (string.Equals(name, "payload.bin", StringComparison.OrdinalIgnoreCase) || ext == ".payload")
        {
            return FirmwareKind.Payload;
        }

        if (ext is ".cpio" or ".tar")
        {
            return FirmwareKind.CpioTar;
        }

        if (ext is ".ofp" or ".ops" or ".ozip")
        {
            return FirmwareKind.Oppo;
        }

        var header = ReadHeader(path, 8);
        if (header.Length >= 4 && IsAscii(header, "CrAU"))
        {
            return FirmwareKind.Payload;
        }

        if (header.Length >= 8 && (IsAscii(header, "ANDROID!") || IsAscii(header, "VNDRBOOT")))
        {
            return FirmwareKind.BootImage;
        }

        if (header.Length >= 6 && (IsAscii(header, "070701") || IsAscii(header, "070702") || IsAscii(header, "070707")))
        {
            return FirmwareKind.CpioTar;
        }

        return null;
    }

    private static byte[] ReadHeader(string path, int count)
    {
        var buffer = new byte[count];
        try
        {
            using var fs = File.OpenRead(path);
            var read = 0;
            while (read < count)
            {
                var n = fs.Read(buffer, read, count - read);
                if (n <= 0)
                {
                    break;
                }

                read += n;
            }

            if (read != count)
            {
                Array.Resize(ref buffer, read);
            }
        }
        catch
        {
            return [];
        }

        return buffer;
    }

    private static bool IsAscii(byte[] header, string text)
    {
        if (header.Length < text.Length)
        {
            return false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            if (header[i] != (byte)text[i])
            {
                return false;
            }
        }

        return true;
    }

    private enum FirmwareKind
    {
        Payload,
        CpioTar,
        Oppo,
        BootImage
    }
}