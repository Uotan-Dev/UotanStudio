using Avalonia;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Services;
using UotanStudio.Services.Firmware;
using UotanStudio.Workflow.Execution;

namespace UotanStudio.Workflow.Nodes;

public partial class FirmwareNodeViewModel
{
    /// <summary>
    /// Creates a default firmware pack node.
    /// <para>创建默认的固件打包节点。</para>
    /// </summary>
    public static FirmwareNodeViewModel CreatePackNode()
    {
        var node = new FirmwareNodeViewModel("Pack Firmware")
        {
            Location = new Point(100, 100),
            TypeKey = "Firmware.Container.Pack"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Input Dir", PortType = NodePortType.Path });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Format",
            PortType = NodePortType.Text,
            IsAdvanced = true,
            Choices =
            [
                .. new[] { "auto", "ext4", "erofs", "f2fs", "squashfs", "cpio", "tar", "payload", "super" }
                    .Select(value => new NodePortChoice(value))
            ],
            InitialValue = "auto"
        });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "File List",
            PortType = NodePortType.List,
            IsAdvanced = true
        });
        node.Input.Add(new NodeConnectorViewModel
        {
            Title = "Config",
            PortType = NodePortType.Text,
            DisplayResourceKey = "Node_Conn_LegacyOutputPath"
        });
        node.Input.Add(new NodeConnectorViewModel { Title = "Output Path", PortType = NodePortType.Path });
        node.Output.Add(new NodeConnectorViewModel { Title = "Firmware", PortType = NodePortType.Path });
        return node;
    }

    private async Task ExecutePackAsync(INodeExecutionContext context)
    {
        var inputDir = PathText(ReadInput("Input Dir"));
        var config = PathText(ReadInput("Config"));
        var configuredOutput = PathText(ReadInput("Output Path"));
        if (string.IsNullOrWhiteSpace(inputDir) || !Directory.Exists(inputDir))
        {
            throw new DirectoryNotFoundException($"Input directory was not found: {inputDir}");
        }

        var outputPath = !string.IsNullOrWhiteSpace(configuredOutput)
            ? configuredOutput
            : string.IsNullOrWhiteSpace(config) ? DefaultOutputFor(inputDir) : config;

        using var metadataScope = RepackMetadata.UseContext(
            Services.Projects.ProjectContextService.Instance.CaptureMetadataContext(inputDir));
        var repack = RepackMetadata.Read(inputDir);
        var declaredFormat = PathText(ReadInput("Format"));
        var fileList = NodeDataUtil.ToList(ReadInput("File List"));

        if (fileList.Count > 0)
        {
            var missing = fileList
                .Select(item => Convert.ToString(item, System.Globalization.CultureInfo.InvariantCulture))
                .Where(relative => !string.IsNullOrWhiteSpace(relative))
                .Where(relative => !File.Exists(Path.Combine(inputDir, relative!))
                    && !Directory.Exists(Path.Combine(inputDir, relative!)))
                .ToList();
            if (missing.Count > 0)
            {
                throw new InvalidDataException(
                    $"The File List names {missing.Count} path(s) that are absent from '{inputDir}': {string.Join(", ", missing.Take(5))}.  文件清单包含输入目录中不存在的路径。");
            }
        }

        var formatName = string.IsNullOrWhiteSpace(declaredFormat) || declaredFormat == "auto"
            ? repack?.FormatName
            : declaredFormat;

        if (repack is null && string.IsNullOrWhiteSpace(formatName))
        {
            throw new InvalidDataException(LanguageService.Instance.GetString("Node_Pack_RequiresMetadata"));
        }
        if (repack is { } metadata && !metadata.CanPackDirectory && formatName == metadata.FormatName)
        {
            throw new InvalidDataException(metadata.PackingNotes);
        }

        var packed = await RegisteredFirmwareFormats.PackDirectoryAsync(formatName!, inputDir,
            outputPath, context.CancellationToken, throwIfUnsupported: true).ConfigureAwait(false);
        if (!packed.Success)
        {
            throw new InvalidDataException(packed.Summary);
        }
        WriteOutput("Firmware", outputPath);
        context.Log.Write(LogLevel.Info, Title,
            fileList.Count > 0 ? $"{packed.Summary} ({fileList.Count} listed path(s) verified)  已校验文件清单。" : packed.Summary);
    }

    private static string DefaultOutputFor(string inputDir)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(inputDir)) ?? ".";
        return Path.Combine(parent, Path.GetFileName(inputDir) + ".img");
    }
}