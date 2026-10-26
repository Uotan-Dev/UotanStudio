using System.Collections.ObjectModel;
using UotanStudio.Services;

namespace UotanStudio.ViewModels.Editors;

/// <summary>A localized group of firmware configuration fields.<para>本地化的固件配置字段分组。</para></summary>
/// <param name="Name">Localized group name.<para>本地化的分组名称。</para></param>
/// <param name="Rows">Property rows belonging to the group.<para>属于该分组的属性行。</para></param>
public sealed record FirmwareMetadataGroup(string Name, IReadOnlyList<PropertyRow> Rows);

public partial class FirmwareInfoViewModel
{
    private IReadOnlyDictionary<string, string>? _rawMetadata;
    private string _rawSummary = string.Empty;
    private CancellationTokenSource? _loadCancellation;
    private bool _closed;

    /// <summary>Grouped metadata, preserving descriptor order and repeated partition names.<para>分组后的元数据，保留描述符顺序与重复的分区名称。</para></summary>
    /// <value>Observable collection of localized metadata groups.<para>本地化元数据分组的可观察集合。</para></value>
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
            "Coolpad CPB Package" => LanguageService.Instance.GetString("FirmwareInfo_CpbSummary"),
            _ => _rawSummary
        };
        HasMetadata = Metadata.Count > 0;
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => RefreshMetadata();
}