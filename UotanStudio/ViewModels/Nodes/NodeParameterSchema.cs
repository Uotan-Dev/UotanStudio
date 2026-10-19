using System.Diagnostics;
using UotanStudio.Services;
using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.ViewModels.Nodes;

/// <summary>Presentation rules for existing operation contracts; hidden values remain part of execution.<para>面向既有操作契约的呈现规则；隐藏值仍参与执行。</para></summary>
internal static class NodeParameterSchema
{
    internal static void Apply(FlowNodeViewModel node)
    {
        if (node is PatchNodeViewModel)
        {
            PatchNodePorts.ApplyPanelFlags(node);
            return;
        }
        string[] required = node.TypeKey switch
        {
            "Firmware.Container.Unpack" => ["Firmware Path"],
            "Firmware.Container.Pack" or "Firmware.Cpio.Pack" => ["Input Dir"],
            "Validation.Signature.Verify" => ["Firmware"],
            "Boot.CommandLine.Edit" => ["Image", "Parameter", "Output Path"],
            "Firmware.Cpio.Unpack" => ["Archive Path"],
            "Image.Super.Pack" => ["Output Path", "Device Size", "Partitions"],
            "Device.Property.Read" => ["Device", "Name"],
            "Device.Partition.Flash" => ["Device", "Partition", "Image"],
            "Device.Power.Reboot" => ["Device"],
            "File.Binary.Read" or "File.Artifact.Open" => ["Path"],
            "Data.Value.Convert" => ["Input"],
            "Validation.Data.Hash" or "Data.Bytes.Slice" or "Data.Bytes.Swap"
                or "Data.Compression.Compress" or "Data.Compression.Decompress" => ["Data"],
            "Data.Value.Compare" or "Data.Bytes.Concat" => ["Data A", "Data B"],
            "Data.Cipher.Encrypt" or "Data.Cipher.Decrypt" => ["Data", "Key"],
            "Network.File.Download" => ["URL", "Output Path"],
            "File.Artifact.Publish" => ["Path"],
            _ => []
        };
        foreach (var title in required)
        {
            Configure(node, title, port => port.IsRequired = true);
        }

        switch (node.TypeKey)
        {
            case "Firmware.Container.Pack":
                Advanced(node, "Config");
                break;
            case "File.Artifact.Publish":
                Configure(node, "Data", port => port.IsRequired = false);
                Configure(node, "Artifact", port => port.IsRequired = false);
                break;
            case "Image.Super.Pack":
                Advanced(node, "Metadata Size", "Slots", "Block Size", "Sparse", "Virtual AB");
                break;
            case "Network.File.Download":
                Advanced(node, "Request Headers", "Expected SHA256", "Timeout Seconds");
                break;
        }
    }

    private static void Advanced(FlowNodeViewModel node, params ReadOnlySpan<string> titles)
    {
        foreach (var title in titles)
        {
            Configure(node, title, port => port.IsAdvanced = true);
        }
    }

    private static void Configure(FlowNodeViewModel node, string title, Action<NodeConnectorViewModel> configure)
    {
        if (node.Input.FirstOrDefault(port => port.Title == title) is { } port)
        {
            configure(port);
        }
        else
        {
            Debug.Fail($"Parameter schema port '{title}' is missing on '{node.TypeKey}'.");
            LogService.Instance.Write(LogLevel.Warn, "NodeSchema",
                $"Parameter schema port '{title}' is missing on '{node.TypeKey}'.");
        }
    }
}