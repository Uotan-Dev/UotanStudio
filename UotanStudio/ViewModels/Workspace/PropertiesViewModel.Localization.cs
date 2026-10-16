using Avalonia.Threading;
using UotanStudio.Services;

namespace UotanStudio.ViewModels.Workspace;

internal partial class PropertiesViewModel
{

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
    /// Resolves a stable metadata or analysis key to the active locale; unknown keys are
    /// deliberately preserved as-is.
    /// <para>将稳定的元数据或分析键解析到当前语言；未知键按原样保留。</para>
    /// </summary>
    /// <param name="name">The metadata or analysis key to resolve.<para>要解析的元数据或分析键。</para></param>
    /// <returns>The localized label, or the original key when it is unknown.<para>本地化后的标签；未知键返回原值。</para></returns>
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
    /// Resolves a fixed property label in the active locale, falling back to the built-in
    /// English dictionary when the active locale does not provide the key.
    /// <para>在当前语言解析固定属性标签；当前语言缺少该键时回退到内置英文词典。</para>
    /// </summary>
    /// <param name="resourceKey">Resource key of the fixed label.<para>固定标签的资源键。</para></param>
    /// <returns>The localized label or its English fallback.<para>本地化标签或其英文回退值。</para></returns>
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
        => LanguageService.Instance.GetEnglish(resourceKey);

    private static PropertyRow CreateDynamicRow(string name, string value)
    {

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

            if (string.Equals(row.NameResourceKey, "Dock_Properties_Type", StringComparison.Ordinal))
            {
                row.Value = ContentTypeDisplay;
            }
        }

        if (ContentType == PropertiesContentType.TextDocument && _currentTextDocument is { } textDocument)
        {

            PopulateLanguageSelector(textDocument.Language);
        }

        if (ContentType == PropertiesContentType.ScreenMirror && _mirror is not null)
        {

            UpdateMirrorProperties(false);
        }

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
        /// <summary>Resource key used to render this fixed property label.<para>渲染此固定属性标签所用的资源键。</para></summary>
        /// <value>The label's resource key, or null for dynamic labels.<para>标签的资源键；动态标签为 null。</para></value>
        internal string? NameResourceKey { get; init; }

        /// <summary>Original format-handler or analysis key for a dynamic property label.<para>动态属性标签的原始格式处理或分析键。</para></summary>
        /// <value>The source key, or null for fixed labels.<para>源键；固定标签为 null。</para></value>
        internal string? SourceName { get; init; }

        /// <summary>Readable fallback used when a custom locale omits the resource key.<para>自定义语言缺少资源键时使用的可读回退值。</para></summary>
        /// <value>The fallback display name.<para>回退显示名称。</para></value>
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
                Name = LocalizePropertyName(SourceName);
            }
        }
    }
}