using System.Collections.ObjectModel;
using UotanStudio.Services;

namespace UotanStudio.ViewModels.Editors;

/// <summary>A localized group of firmware configuration fields.</summary>
public sealed record FirmwareMetadataGroup(string Name, IReadOnlyList<PropertyRow> Rows);

public partial class FirmwareInfoViewModel
{
    private IReadOnlyDictionary<string, string>? _rawMetadata;
    private string _rawSummary = string.Empty;
    private CancellationTokenSource? _loadCancellation;
    private bool _closed;

    /// <summary>Grouped metadata, preserving descriptor order and repeated partition names.</summary>
    public ObservableCollection<FirmwareMetadataGroup> MetadataGroups { get; } = [];

    private static string FieldLabel(string key)
    {
        var resource = "FirmwareField_" + key;
        var value = LanguageService.Instance.GetString(resource);
        return value == resource ? key : value;
    }

    private void RefreshMetadata()
    {
        Metadata.Clear();
        MetadataGroups.Clear();
        if (_rawMetadata is null)
        {
            return;
        }

        Dictionary<string, List<PropertyRow>> groups = [];
        foreach (var (key, rawValue) in _rawMetadata)
        {
            var groupKey = "General";
            var fieldKey = key;
            var separator = key.IndexOf("].", StringComparison.Ordinal);
            if (key.StartsWith("Descriptor[", StringComparison.Ordinal) && separator > 0)
            {
                groupKey = key[..(separator + 1)];
                fieldKey = key[(separator + 2)..];
            }
            else if (key is "FeatureCompat" or "FeatureIncompat" or "FeatureRoCompat" or "Feature" or "AvailableCompressionAlgorithms" or "State" or "CheckpointFlags")
            {
                groupKey = "Features";
            }

            var value = fieldKey switch
            {
                "HashtreeDisabled" or "VerificationDisabled" or "PartialUpdate" => FieldLabel(rawValue),
                "Type" when rawValue == "Hash" => FieldLabel("PartitionHash"),
                "VerificationStatus" or "Type" or "ChecksumStatus" => FieldLabel(rawValue),
                _ => rawValue.Length == 0 ? FieldLabel("Empty") : rawValue
            };
            var row = new PropertyRow(FieldLabel(fieldKey), value);
            Metadata.Add(row);
            if (!groups.TryGetValue(groupKey, out var rows))
            {
                rows = [];
                groups.Add(groupKey, rows);
            }
            rows.Add(row);
        }
        foreach (var (key, rows) in groups)
        {
            var name = key.StartsWith("Descriptor[", StringComparison.Ordinal)
                ? FieldLabel("Descriptor") + " " + key[11..^1]
                : FieldLabel(key);
            MetadataGroups.Add(new FirmwareMetadataGroup(name, rows));
        }

        Summary = FormatName switch
        {
            "Android Verified Boot (AVB)" => LanguageService.Instance.GetString("FirmwareInfo_AvbSummary"),
            "EROFS" or "EXT4" or "F2FS" => LanguageService.Instance.GetString("FirmwareInfo_FilesystemSummary", FormatName),
            "FAT" or "EXFAT" => LanguageService.Instance.GetString("FirmwareInfo_FatSummary", FormatName),
            "Android Boot / Vendor Boot Image" => LanguageService.Instance.GetString("FirmwareInfo_BootSummary"),
            "Android OTA Payload (payload.bin)" => LanguageService.Instance.GetString("FirmwareInfo_PayloadSummary"),
            "Android Sparse Image (simg)" => LanguageService.Instance.GetString("FirmwareInfo_SparseSummary"),
            _ => _rawSummary
        };
        HasMetadata = Metadata.Count > 0;
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => RefreshMetadata();
}