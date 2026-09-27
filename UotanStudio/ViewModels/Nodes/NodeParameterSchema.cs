using UotanStudio.ViewModels.NodeViewModels;

namespace UotanStudio.ViewModels.Nodes;

/// <summary>Presentation rules for existing operation contracts; hidden values remain part of execution.</summary>
internal static class NodeParameterSchema
{
    internal static void Apply(FlowNodeViewModel node)
    {
        string[] required = node.TypeKey switch
        {
            "Firmware.Container.Unpack" => ["Firmware Path"],
            "Firmware.Container.Pack" or "Firmware.Cpio.Pack" => ["Input Dir"],
            "Firmware.Container.Extract" or "Validation.Signature.Verify" => ["Firmware"],
            "Firmware.Cpio.Unpack" => ["Archive Path"],
            "Build.Apk.Sign" => ["APK Path", "Output Path", "Private Key", "Certificate"],
            "Image.Super.Unpack" => ["Super Path"],
            "Image.Super.Pack" => ["Output Path", "Device Size", "Partitions"],
            "Image.Sparse.Convert" => ["Input"],
            "Build.Dat.Convert" => ["Transfer List"],
            "Build.Ota.CreateDelta" => ["Source", "Target"],
            "Device.Property.Read" => ["Device", "Name"],
            "Device.Partition.Flash" => ["Device", "Partition", "Image"],
            "Device.Partition.Erase" => ["Device", "Partition"],
            "Device.Power.Reboot" or "Device.Storage.EraseAll" => ["Device"],
            "Device.Ota.Sideload" => ["Device", "OTA Package"],
            "File.Binary.Read" or "File.Artifact.Open" => ["Path"],
            "Data.Value.Convert" => ["Input"],
            "Validation.Data.Hash" or "Data.Bytes.Slice" or "Data.Bytes.Swap"
                or "Data.Compression.Compress" or "Data.Compression.Decompress" => ["Data"],
            "Data.Value.Compare" or "Data.Bytes.Concat" => ["Data A", "Data B"],
            "Data.Cipher.Encrypt" or "Data.Cipher.Decrypt" => ["Data", "Key"],
            "Network.File.Download" or "Network.GitHub.Download" => ["URL", "Output Path"],
            "File.Artifact.Publish" => ["Path"],
            _ => []
        };
        foreach (var title in required)
        {
            Configure(node, title, port => port.IsRequired = true);
        }
        if (node is PatchNodeViewModel)
        {
            Advanced(node, "Image Capacity", "Extraction Budget", "Encoding", "Expected Hash", "Expected Matches", "Expected Sections",
                "Allow Create", "Check Old Value", "File System Filter", "External Services", "Namespaces");
            foreach (var title in new[] { "Expected Old Value", "Expected Old JSON", "Expected UID", "Expected GID", "Expected Mode", "Expected Exists", "Expected Hex Value" })
            {
                Configure(node, title, port =>
                {
                    port.IsAdvanced = true;
                    port.IsVisibleWhen = current => Boolean(current, "Check Old Value");
                });
            }
            foreach (var title in new[] { "Path", "Root", "Output Path", "Workspace", "Asset", "Relative Path", "Partition Path" })
            {
                Configure(node, title, port => port.IsRequired = true);
            }
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
            case "Config.AndroidProperty.Edit":
            case "Config.Ini.Edit":
            case "Config.Json.Edit":
                Visible(node, "Value", current => !EqualsValue(current, "Operation", "Delete"));
                Visible(node, "JSON Value", current => !EqualsValue(current, "Operation", "Delete"));
                Visible(node, "Allow Create", current => !EqualsValue(current, "Operation", "Delete"));
                break;
            case "Config.InitRc.EditCommand":
                Visible(node, "Replacement", current => !EqualsValue(current, "Operation", "Delete"));
                break;
            case "Config.InitRc.AddCommand":
                Visible(node, "Anchor Statement", current => !EqualsValue(current, "Placement", "End"));
                Advanced(node, "Identity Arguments");
                break;
            case "Config.Fstab.Edit":
                Visible(node, "Value", current => MayEqual(current, "Operation", "SetValue") || MayEqual(current, "Operation", "SetFlag"));
                Visible(node, "Flag Name", current => MayEqual(current, "Operation", "SetFlag") || MayEqual(current, "Operation", "DeleteFlag"));
                break;
            case "Validation.Condition.Evaluate":
                Configure(node, "Path", port => port.IsRequired = false);
                Visible(node, "Path", current => MayEqual(current, "Operation", "FileExists") || MayEqual(current, "Operation", "DirectoryExists"));
                Visible(node, "Value", current => !EqualsValue(current, "Operation", "FileExists")
                    && !EqualsValue(current, "Operation", "DirectoryExists")
                    || !string.IsNullOrWhiteSpace(Convert.ToString(current.Input.First(port => port.Title == "Value").InitialValue)));
                Visible(node, "Upper Version", current => MayEqual(current, "Operation", "VersionRange"));
                Visible(node, "Expected Value", current => !EqualsValue(current, "Operation", "FileExists") && !EqualsValue(current, "Operation", "DirectoryExists"));
                break;
            case "Binary.Pattern.Find":
            case "Binary.Pattern.Replace":
                Advanced(node, "Find Mask", "Replace Mask", "Start Offset", "Search Length", "Alignment", "Skip Matches");
                break;
            case "Image.Xattr.Set":
            case "Image.Xattr.Delete":
                Visible(node, "Expected Hex Value", current => Boolean(current, "Check Old Value") && Boolean(current, "Expected Exists"));
                break;
            case "Image.Super.Pack":
                Advanced(node, "Metadata Size", "Slots", "Block Size", "Sparse", "Virtual AB");
                break;
            case "Network.File.Download":
            case "Network.GitHub.Download":
                Advanced(node, "Request Headers", "Expected SHA256", "Timeout Seconds");
                break;
        }

        if (node.TypeKey.StartsWith("UI.Field.", StringComparison.Ordinal))
        {
            Configure(node, "Field ID", port => { port.IsConfiguration = true; port.IsAdvanced = true; });
            Advanced(node, "Description", "Minimum", "Maximum", "Sensitive", "Visible When", "Enabled When", "Required When");
        }
        if (node.TypeKey == "UI.Result.Read")
        {
            Configure(node, "Value Type", port => port.IsConfiguration = true);
        }
        if (node.TypeKey.StartsWith("UI.Dialog.", StringComparison.Ordinal))
        {
            Advanced(node, "Timeout Seconds");
        }
    }

    private static void Advanced(FlowNodeViewModel node, params string[] titles)
    {
        foreach (var title in titles)
        {
            Configure(node, title, port => port.IsAdvanced = true);
        }
    }

    private static void Visible(FlowNodeViewModel node, string title, Func<FlowNodeViewModel, bool> predicate)
        => Configure(node, title, port => port.IsVisibleWhen = predicate);

    private static void Configure(FlowNodeViewModel node, string title, Action<NodeConnectorViewModel> configure)
    {
        if (node.Input.FirstOrDefault(port => port.Title == title) is { } port)
        {
            configure(port);
        }
    }

    private static bool Boolean(FlowNodeViewModel node, string title)
    {
        var port = node.Input.FirstOrDefault(input => input.Title == title);
        return port?.HasConnectedSource == true || NodeDataUtil.ToBool(port?.InitialValue);
    }

    private static bool EqualsValue(FlowNodeViewModel node, string title, string expected)
    {
        var port = node.Input.FirstOrDefault(input => input.Title == title);
        return port?.HasConnectedSource != true && string.Equals(Convert.ToString(port?.InitialValue), expected, StringComparison.Ordinal);
    }

    private static bool MayEqual(FlowNodeViewModel node, string title, string expected)
        => node.Input.FirstOrDefault(input => input.Title == title)?.HasConnectedSource == true || EqualsValue(node, title, expected);
}