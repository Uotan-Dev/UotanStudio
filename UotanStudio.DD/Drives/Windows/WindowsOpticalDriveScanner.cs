using System.Runtime.InteropServices;

namespace UotanStudio.DD.Drives.Windows;

/// <summary>
/// Enumerates optical recorders on Windows through the in-process IMAPI2 interop.
/// <para>在 Windows 上通过进程内 IMAPI2 互操作枚举光驱。</para>
/// </summary>
/// <remarks>
/// Recorder identifiers come from <c>IDiscMaster2</c> and media type plus state from
/// <c>IDiscFormat2Data</c>, matching the values the previous PowerShell automation exposed. IMAPI2
/// media states combine informational bits with error bits; writable requires one of
/// blank/appendable/overwrite-only and no error bit set.
/// <para>
/// 刻录机标识来自 <c>IDiscMaster2</c>，介质类型与状态来自 <c>IDiscFormat2Data</c>，与原 PowerShell
/// 自动化暴露的值一致。IMAPI2 介质状态把信息位与错误位组合在一起；可写要求 blank/appendable/
/// overwrite-only 之一且未置任何错误位。
/// </para>
/// </remarks>
internal sealed class WindowsOpticalDriveScanner : IDriveScanner
{
    /// <inheritdoc />
    public Task<IReadOnlyList<FlashDrive>> ScanAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var drives = new List<FlashDrive>();
        string[] recorders;
        try
        {
            recorders = Imapi2Interop.ListRecorders();
        }
        catch (Exception exception) when (exception is COMException or DllNotFoundException or EntryPointNotFoundException)
        {
            // A host without the IMAPI2 COM server simply reports no optical recorders.
            // 缺少 IMAPI2 COM 服务器的主机直接上报没有光驱。
            return Task.FromResult<IReadOnlyList<FlashDrive>>([]);
        }

        foreach (var id in recorders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Imapi2Interop.RecorderInfo info;
            try
            {
                info = Imapi2Interop.GetRecorderInfo(id);
            }
            catch (COMException)
            {
                continue;
            }

            var description = (info.Vendor.Trim() + ' ' + info.Product.Trim()).Trim();
            if (description.Length == 0)
            {
                description = "Optical Recorder " + id;
            }

            drives.Add(new FlashDrive
            {
                DevicePath = id,
                DisplayName = description,
                Size = 0,
                IsRemovable = true,
                IsReadOnly = false,
                IsSystem = false,
                Description = description,
                MountPoints = [],
                MediaKind = FlashMediaKind.OpticalDisc,
                DiscFormat = MediaTypeToFormat(info.MediaType),
                IsDiscWritable = IsWritableMedia(info.MediaType) && IsWritableState(info.MediaState),
            });
        }

        return Task.FromResult<IReadOnlyList<FlashDrive>>(drives);
    }

    /// <summary>
    /// Maps an <c>IMAPI_MEDIA_PHYSICAL_TYPE</c> value to the user-facing disc family.
    /// <para>把 <c>IMAPI_MEDIA_PHYSICAL_TYPE</c> 值映射为面向用户的光盘类别。</para>
    /// </summary>
    /// <param name="mediaType">IMAPI media type value; -1 means unknown.<para>IMAPI 介质类型值；-1 表示未知。</para></param>
    internal static OpticalDiscFormat MediaTypeToFormat(int mediaType) => mediaType switch
    {
        >= Imapi2Interop.MediaTypeCdRom and <= Imapi2Interop.MediaTypeCdMax => OpticalDiscFormat.Cd,
        >= Imapi2Interop.MediaTypeDvdRom and <= Imapi2Interop.MediaTypeDvdMax => OpticalDiscFormat.Dvd,
        >= Imapi2Interop.MediaTypeBdRom and <= Imapi2Interop.MediaTypeBdMax => OpticalDiscFormat.BluRay,
        _ => OpticalDiscFormat.Unknown,
    };

    /// <summary>
    /// Determines whether the loaded media family accepts recording.
    /// <para>判断已装入的介质类别是否接受刻录。</para>
    /// </summary>
    /// <param name="mediaType">IMAPI media type value; -1 means unknown.<para>IMAPI 介质类型值；-1 表示未知。</para></param>
    internal static bool IsWritableMedia(int mediaType)
    {
        if (mediaType == Imapi2Interop.MediaTypeUnknown)
        {
            return false;
        }

        // Unknown (0) is read-only press media; every recordable family starts at CD-R (0x2).
        // Unknown (0) 是只读压盘介质；可刻录类别从 CD-R (0x2) 开始。
        return mediaType is >= 2 and <= Imapi2Interop.MediaTypeCdMax
            or >= 5 and <= Imapi2Interop.MediaTypeDvdMax
            or >= 0x12 and <= Imapi2Interop.MediaTypeBdMax;
    }

    /// <summary>
    /// Determines whether the loaded media state allows recording.
    /// <para>判断已装入的介质状态是否允许刻录。</para>
    /// </summary>
    /// <param name="mediaState">IMAPI media state value; -1 means unknown.<para>IMAPI 介质状态值；-1 表示未知。</para></param>
    internal static bool IsWritableState(int mediaState)
    {
        if (mediaState == Imapi2Interop.MediaTypeUnknown)
        {
            return false;
        }

        if ((mediaState & Imapi2Interop.MediaStateUnsupportedMask) != 0)
        {
            return false;
        }

        const int writableBits = Imapi2Interop.MediaStateOverwriteOnly
            | Imapi2Interop.MediaStateBlank
            | Imapi2Interop.MediaStateAppendable;
        return (mediaState & writableBits) != 0;
    }
}