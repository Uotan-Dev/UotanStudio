using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UotanStudio.Firmware.BootSecurity.Avb.Signing;
using UotanStudio.Services;

namespace UotanStudio.ViewModels.Editors;

/// <summary>A localized group of firmware configuration fields.<para>本地化的固件配置字段分组。</para></summary>
/// <param name="Name">Localized group name.<para>本地化的分组名称。</para></param>
/// <param name="Rows">Property rows belonging to the group.<para>属于该分组的属性行。</para></param>
public sealed record FirmwareMetadataGroup(string Name, IReadOnlyList<PropertyRow> Rows);

public partial class FirmwareInfoViewModel
{
    private const string AvbFormatName = "Android Verified Boot (AVB)";

    private IReadOnlyDictionary<string, string>? _rawMetadata;
    private string _rawSummary = string.Empty;
    private CancellationTokenSource? _loadCancellation;
    private bool _closed;

    /// <summary>Grouped metadata, preserving descriptor order and repeated partition names.<para>分组后的元数据，保留描述符顺序与重复的分区名称。</para></summary>
    /// <value>Observable collection of localized metadata groups.<para>本地化元数据分组的可观察集合。</para></value>
    public ObservableCollection<FirmwareMetadataGroup> MetadataGroups { get; } = [];

    /// <summary>
    /// Whether a VBMeta flag write is currently in progress.
    /// <para>当前是否正在写入 VBMeta 标志位。</para>
    /// </summary>
    [ObservableProperty] private bool _flagToggleInProgress;

    /// <summary>
    /// Whether the inline VBMeta flag buttons accept input.
    /// <para>行内 VBMeta 标志位按钮是否可交互。</para>
    /// </summary>
    public bool CanToggleVbmetaFlags => !IsBusy && !FlagToggleInProgress;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanToggleVbmetaFlags));

    partial void OnFlagToggleInProgressChanged(bool value) => OnPropertyChanged(nameof(CanToggleVbmetaFlags));

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
            IAsyncRelayCommand? action = FormatName == AvbFormatName
                ? fieldKey switch
                {
                    "HashtreeDisabled" => ToggleHashtreeDisabledCommand,
                    "VerificationDisabled" => ToggleVerificationDisabledCommand,
                    _ => null
                }
                : null;
            var row = new PropertyRow(FieldLabel(fieldKey), value)
            {
                ActionCommand = action,
                ActionLabel = action is null ? string.Empty : FieldLabel("ToggleFlag"),
            };
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
            AvbFormatName => string.Empty,
            "EROFS" or "EXT4" or "F2FS" => LanguageService.Instance.GetString("FirmwareInfo_FilesystemSummary", FormatName),
            "FAT" or "EXFAT" => LanguageService.Instance.GetString("FirmwareInfo_FatSummary", FormatName),
            "Android Boot / Vendor Boot Image" => LanguageService.Instance.GetString("FirmwareInfo_BootSummary"),
            "Android OTA Payload (payload.bin)" => LanguageService.Instance.GetString("FirmwareInfo_PayloadSummary"),
            "Android Sparse Image (simg)" => LanguageService.Instance.GetString("FirmwareInfo_SparseSummary"),
            "Coolpad CPB Package" => LanguageService.Instance.GetString("FirmwareInfo_CpbSummary"),
            _ => _rawSummary
        };
        HasSummary = Summary.Length > 0;
        HasMetadata = Metadata.Count > 0;
    }

    /// <summary>
    /// Toggles the hashtree-disabled VBMeta flag (fastboot <c>--disable-verity</c>) in the open image and reloads the metadata.
    /// <para>切换打开镜像内禁用哈希树校验的 VBMeta 标志位（fastboot <c>--disable-verity</c>）并重新读取元数据。</para>
    /// </summary>
    [RelayCommand]
    private Task ToggleHashtreeDisabledAsync() => ToggleVbmetaFlagAsync(AvbVbmetaFlag.HashtreeDisabled);

    /// <summary>
    /// Toggles the verification-disabled VBMeta flag (fastboot <c>--disable-verification</c>) in the open image and reloads the metadata.
    /// <para>切换打开镜像内禁用 AVB 校验的 VBMeta 标志位（fastboot <c>--disable-verification</c>）并重新读取元数据。</para>
    /// </summary>
    [RelayCommand]
    private Task ToggleVerificationDisabledAsync() => ToggleVbmetaFlagAsync(AvbVbmetaFlag.VerificationDisabled);

    private async Task ToggleVbmetaFlagAsync(AvbVbmetaFlag flag)
    {
        var path = FilePath;
        if (_closed || IsBusy || FlagToggleInProgress || _rawMetadata is null || string.IsNullOrEmpty(path))
        {
            return;
        }

        var key = flag == AvbVbmetaFlag.HashtreeDisabled ? "HashtreeDisabled" : "VerificationDisabled";
        if (!_rawMetadata.TryGetValue(key, out var raw) || !bool.TryParse(raw, out var disabled))
        {
            return;
        }

        FlagToggleInProgress = true;
        try
        {
            var result = await AvbSigner.SetVbmetaFlagAsync(path, flag, !disabled).ConfigureAwait(true);
            if (!result.Success)
            {
                ErrorMessage = UserError.WithDetails(result.ErrorMessage, "UserError_SaveFailed");
                HasError = true;
                LogService.Instance.Write(LogLevel.Error, "FirmwareInfo",
                    $"Failed to toggle {key} for '{path}': {result.ErrorMessage}");
                return;
            }

            await LoadAsync(path).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = UserError.Describe(ex, "UserError_SaveFailed");
            HasError = true;
            LogService.Instance.Write(LogLevel.Error, "FirmwareInfo",
                $"Failed to toggle {key} for '{path}': {ex.Message}");
        }
        finally
        {
            FlagToggleInProgress = false;
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => RefreshMetadata();
}