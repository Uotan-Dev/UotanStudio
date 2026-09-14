using Avalonia.Threading;
using UotanStudio.Services;

namespace UotanStudio.ViewModels.Workspace;

internal partial class PropertiesViewModel
{
    // Keep a built-in English provider available for incomplete/custom locale packs.  The
    // application resource lookup intentionally returns the key when a translation is missing;
    // property rows should retain a readable label instead of exposing that implementation detail.
    private static readonly Lazy<UotanStudio.Locale.EnUs.Dock> EnglishDockResources = new();

    // Format handlers intentionally expose stable, language-neutral keys.  Keep their
    // presentation labels in the application locale dictionaries instead of coupling the
    // firmware library to the UI language service.
    private static readonly IReadOnlyDictionary<string, string> PropertyNameResourceKeys =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AlgorithmType"] = "Dock_Properties_Metadata_AlgorithmType",
            ["DescriptorsSize"] = "Dock_Properties_Metadata_DescriptorsSize",
            ["RollbackIndex"] = "Dock_Properties_Metadata_RollbackIndex",
            ["ReleaseString"] = "Dock_Properties_Metadata_ReleaseString",
            ["FooterVersion"] = "Dock_Properties_Metadata_FooterVersion",
            ["OriginalImageSize"] = "Dock_Properties_Metadata_OriginalImageSize",
            ["VbmetaOffset"] = "Dock_Properties_Metadata_VbmetaOffset",
            ["VbmetaSize"] = "Dock_Properties_Metadata_VbmetaSize",
            ["Type"] = "Dock_Properties_Metadata_Type",
            ["HeaderVersion"] = "Dock_Properties_Metadata_HeaderVersion",
            ["PageSize"] = "Dock_Properties_Metadata_PageSize",
            ["KernelSize"] = "Dock_Properties_Metadata_KernelSize",
            ["RamdiskSize"] = "Dock_Properties_Metadata_RamdiskSize",
            ["SecondSize"] = "Dock_Properties_Metadata_SecondSize",
            ["DtbSize"] = "Dock_Properties_Metadata_DtbSize",
            ["OsVersion"] = "Dock_Properties_Metadata_OsVersion",
            ["CommandLine"] = "Dock_Properties_Metadata_CommandLine",
            ["KernelCompression"] = "Dock_Properties_Metadata_KernelCompression",
            ["VendorRamdiskSize"] = "Dock_Properties_Metadata_VendorRamdiskSize",
            ["RamdiskTableEntryCount"] = "Dock_Properties_Metadata_RamdiskTableEntryCount",
            ["VendorCmdline"] = "Dock_Properties_Metadata_VendorCmdline",
            ["Version"] = "Dock_Properties_Metadata_Version",
            ["CompressionType"] = "Dock_Properties_Metadata_CompressionType",
            ["CompressedOffset"] = "Dock_Properties_Metadata_CompressedOffset",
            ["CompressedSize"] = "Dock_Properties_Metadata_CompressedSize",
            ["Compression"] = "Dock_Properties_Metadata_Compression",
            ["EntryCount"] = "Dock_Properties_Metadata_EntryCount",
            ["NodeCount"] = "Dock_Properties_Metadata_NodeCount",
            ["PropertyCount"] = "Dock_Properties_Metadata_PropertyCount",
            ["ReservationCount"] = "Dock_Properties_Metadata_ReservationCount",
            ["BootCpuId"] = "Dock_Properties_Metadata_BootCpuId",
            ["Format"] = "Dock_Properties_Metadata_Format",
            ["BlockSize"] = "Dock_Properties_Metadata_BlockSize",
            ["Blocks"] = "Dock_Properties_Metadata_Blocks",
            ["ImageSize"] = "Dock_Properties_Metadata_ImageSize",
            ["InodeCount"] = "Dock_Properties_Metadata_InodeCount",
            ["FileCount"] = "Dock_Properties_Metadata_FileCount",
            ["RootNid"] = "Dock_Properties_Metadata_RootNid",
            ["VolumeName"] = "Dock_Properties_Metadata_VolumeName",
            ["PartitionSize"] = "Dock_Properties_Metadata_PartitionSize",
            ["BlockCount"] = "Dock_Properties_Metadata_BlockCount",
            ["DirectoryCount"] = "Dock_Properties_Metadata_DirectoryCount",
            ["SegmentSize"] = "Dock_Properties_Metadata_SegmentSize",
            ["TotalBlocks"] = "Dock_Properties_Metadata_TotalBlocks",
            ["TotalChunks"] = "Dock_Properties_Metadata_TotalChunks",
            ["InnerFormat"] = "Dock_Properties_Metadata_InnerFormat",
            ["FilesystemBlockSize"] = "Dock_Properties_Metadata_FilesystemBlockSize",
            ["CommandCount"] = "Dock_Properties_Metadata_CommandCount",
            ["NewBlocks"] = "Dock_Properties_Metadata_NewBlocks",
            ["NewDataBlocks"] = "Dock_Properties_Metadata_NewDataBlocks",
            ["TransferList"] = "Dock_Properties_Metadata_TransferList",
            ["DataFile"] = "Dock_Properties_Metadata_DataFile",
            ["DataFileType"] = "Dock_Properties_Metadata_DataFileType",
            ["DataFileSize"] = "Dock_Properties_Metadata_DataFileSize",
            ["PatchType"] = "Dock_Properties_Metadata_PatchType",
            ["ControlBlockLength"] = "Dock_Properties_Metadata_ControlBlockLength",
            ["DiffBlockLength"] = "Dock_Properties_Metadata_DiffBlockLength",
            ["NewFileSize"] = "Dock_Properties_Metadata_NewFileSize",
            ["FileLength"] = "Dock_Properties_Metadata_FileLength",
            ["PartitionCount"] = "Dock_Properties_Metadata_PartitionCount",
            ["MinorVersion"] = "Dock_Properties_Metadata_MinorVersion",
            ["HeaderType"] = "Dock_Properties_Metadata_HeaderType",
            ["Device"] = "Dock_Properties_Metadata_Device",
            ["ChunkCount"] = "Dock_Properties_Metadata_ChunkCount",
            ["TotalSize"] = "Dock_Properties_Metadata_TotalSize",
            ["InferredFirmwareType"] = "Dock_Properties_Metadata_InferredFirmwareType",
            ["Md5RecordCount"] = "Dock_Properties_Metadata_Md5RecordCount",
            ["MajorVersion"] = "Dock_Properties_Metadata_MajorVersion",
            ["PatchVersion"] = "Dock_Properties_Metadata_PatchVersion",
            ["RegionCount"] = "Dock_Properties_Metadata_RegionCount",
            ["HeaderSize"] = "Dock_Properties_Metadata_HeaderSize",
            ["FileSize"] = "Dock_Properties_Metadata_FileSize",
            ["Flags"] = "Dock_Properties_Metadata_Flags",
            ["DecryptedEntries"] = "Dock_Properties_Metadata_DecryptedEntries",
            ["FormatCode"] = "Dock_Properties_Metadata_FormatCode",
            ["ProjectName"] = "Dock_Properties_Metadata_ProjectName",
            ["CpuModel"] = "Dock_Properties_Metadata_CpuModel",
            ["FlashType"] = "Dock_Properties_Metadata_FlashType",
            ["FirmwareName"] = "Dock_Properties_Metadata_FirmwareName",
            ["ProjectId"] = "Dock_Properties_Metadata_ProjectId",
            ["KeyId"] = "Dock_Properties_Metadata_KeyId",
            ["CompressionFormat"] = "Dock_Properties_Metadata_CompressionFormat",
            ["CanCompress"] = "Dock_Properties_Metadata_CanCompress",
            ["CanDecompress"] = "Dock_Properties_Metadata_CanDecompress",
            ["Entries"] = "Dock_Properties_Metadata_Entries",
            ["Entry"] = "Dock_Properties_Metadata_Entry",
            ["Summary"] = "Dock_Properties_Metadata_Summary",
            ["Error"] = "Dock_Properties_Metadata_Error",
            ["Size"] = "Dock_Properties_Metadata_Size",
            ["SourceFilesystemSize"] = "Dock_Properties_Metadata_SourceFilesystemSize",
            ["RebuiltSize"] = "Dock_Properties_Metadata_RebuiltSize",
            ["FitsSourceFilesystemSize"] = "Dock_Properties_Metadata_FitsSourceFilesystemSize",
            ["SizeExcess"] = "Dock_Properties_Metadata_SizeExcess",
            ["CompressionProfile"] = "Dock_Properties_Metadata_CompressionProfile",
            ["CompressionInference"] = "Dock_Properties_Metadata_CompressionInference",
            ["CompressionAttempts"] = "Dock_Properties_Metadata_CompressionAttempts"
        };

    /// <summary>
    /// Resolves a stable metadata/analysis key to the current UI language. Unknown keys are
    /// deliberately preserved because they may be format-specific data that the application does
    /// not know how to translate.
    /// </summary>
    internal static string LocalizePropertyName(string? name)
    {
        if (string.IsNullOrEmpty(name) || !PropertyNameResourceKeys.TryGetValue(name, out var resourceKey))
        {
            return name ?? string.Empty;
        }

        var localized = LanguageService.Instance.GetString(resourceKey);
        if (!string.Equals(localized, resourceKey, StringComparison.Ordinal))
        {
            return localized;
        }

        // A custom locale may omit a format label. Use the built-in English label before
        // exposing the backend's camel-case key; plugin-specific names still remain untouched.
        var english = GetEnglishPropertyName(resourceKey);
        return string.Equals(english, resourceKey, StringComparison.Ordinal) ? name : english;
    }

    private static bool TryGetPropertyNameResourceKey(string? name, out string resourceKey)
    {
        if (!string.IsNullOrEmpty(name) && PropertyNameResourceKeys.TryGetValue(name, out resourceKey!))
        {
            return true;
        }

        resourceKey = string.Empty;
        return false;
    }

    private PropertyRow CreateLocalizedRow(
        string resourceKey,
        string value,
        bool isEncodingSelector = false,
        bool isLanguageSelector = false,
        string? settingKey = null,
        IReadOnlyList<PropertyChoice>? choices = null,
        PropertyChoice? selectedChoice = null)
    {
        var fallbackName = GetEnglishPropertyName(resourceKey);
        var displayName = LocalizeFixedPropertyName(resourceKey, fallbackName);
        return new PropertyRow(displayName, value)
        {
            NameResourceKey = resourceKey,
            FallbackName = fallbackName,
            IsEncodingSelector = isEncodingSelector,
            IsLanguageSelector = isLanguageSelector,
            SettingKey = settingKey ?? string.Empty,
            Choices = choices ?? [],
            SelectedChoice = selectedChoice
        };
    }

    /// <summary>
    /// Resolves a fixed property label in the active locale, falling back to the built-in English
    /// Dock dictionary when the active locale does not provide the key.
    /// </summary>
    internal static string LocalizeFixedPropertyName(string resourceKey)
    {
        return LocalizeFixedPropertyName(resourceKey, GetEnglishPropertyName(resourceKey));
    }

    private static string LocalizeFixedPropertyName(string resourceKey, string fallbackName)
    {
        var localized = LanguageService.Instance.GetString(resourceKey);
        return string.Equals(localized, resourceKey, StringComparison.Ordinal) ? fallbackName : localized;
    }

    private static string GetEnglishPropertyName(string resourceKey)
    {
        try
        {
            if (EnglishDockResources.Value.TryGetResource(resourceKey, null, out var value)
                && value is string text
                && !string.IsNullOrEmpty(text))
            {
                return text;
            }
        }
        catch
        {
            // A resource provider must not make the properties panel unusable.  Known callers
            // provide valid built-in keys; an unknown key is retained as the final fallback.
        }

        return resourceKey;
    }

    private static PropertyRow CreateDynamicRow(string name, string value)
    {
        // Keep both the stable resource key and the original name. The latter is the
        // fallback for an unavailable locale entry and for format/plugin-specific keys.
        if (TryGetPropertyNameResourceKey(name, out var resourceKey))
        {
            var fallbackName = GetEnglishPropertyName(resourceKey);
            if (string.Equals(fallbackName, resourceKey, StringComparison.Ordinal))
            {
                fallbackName = name;
            }

            return new(LocalizePropertyName(name), value)
            {
                NameResourceKey = resourceKey,
                SourceName = name,
                FallbackName = fallbackName
            };
        }

        return new(name, value) { SourceName = name, FallbackName = name };
    }

    private void RefreshLocalizedPropertyNames()
    {
        foreach (var row in Properties)
        {
            row.RefreshLocalizedName();

            // The type is represented by a concrete row value rather than a binding to
            // ContentTypeDisplay, so refresh that value along with its label.
            if (string.Equals(row.NameResourceKey, "Dock_Properties_Type", StringComparison.Ordinal))
            {
                row.Value = ContentTypeDisplay;
            }
        }

        if (ContentType == PropertiesContentType.TextDocument && _currentTextDocument is { } textDocument)
        {
            // The auto-detect entry is localized text, so rebuild the list while retaining the
            // document's stable language id and suppressing a write-back callback.
            PopulateLanguageSelector(textDocument.Language);
        }

        if (ContentType == PropertiesContentType.ScreenMirror && _mirror is not null)
        {
            // Refresh localized values such as "Not reported" and "Audio off" immediately;
            // waiting for the next statistics tick would leave a stopped session stale.
            UpdateMirrorProperties(false);
        }

        // ContentTypeDisplay is also consumed by the type row's value and by external bindings.
        OnPropertyChanged(nameof(ContentTypeDisplay));
    }

    private void RefreshLocalizedPropertyNamesOnUiThread()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            RefreshLocalizedPropertyNames();
        }
        else
        {
            Dispatcher.UIThread.Post(RefreshLocalizedPropertyNames);
        }
    }

    public sealed partial class PropertyRow
    {
        /// <summary>Resource key used to render this fixed property label.</summary>
        internal string? NameResourceKey { get; init; }

        /// <summary>Original format-handler/analysis key for a dynamic property label.</summary>
        internal string? SourceName { get; init; }

        /// <summary>Readable fallback used when a custom locale omits the resource key.</summary>
        internal string? FallbackName { get; init; }

        internal void RefreshLocalizedName()
        {
            foreach (var choice in Choices)
            {
                choice.RefreshLocalizedLabel();
            }

            if (!string.IsNullOrEmpty(NameResourceKey))
            {
                var localized = LanguageService.Instance.GetString(NameResourceKey);
                Name = string.Equals(localized, NameResourceKey, StringComparison.Ordinal)
                    ? FallbackName ?? SourceName ?? NameResourceKey
                    : localized;
            }
            else if (SourceName is not null)
            {
                Name = PropertiesViewModel.LocalizePropertyName(SourceName);
            }
        }
    }
}