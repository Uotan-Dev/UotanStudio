using UotanStudio.Comm.Protocols.Mtk;
using UotanStudio.Firmware.VendorFirmware.MediaTek;

namespace UotanStudio.Tools.MtkProbe;

internal sealed record DaMatch(string LoaderPath, MtkDaEntry Entry, int EntryIndex, int StageOneRegion,
    int StageTwoRegion, string Description)
{
}

internal static class DaDiscovery
{
    internal static IReadOnlyList<string> DefaultLoaderCandidates() =>
    [
        @"E:\UotanStudio\reference\flash\mediatek\SP_Flash_Tool_v5.2228\Lib\MTK_AllInOne_DA.bin",
        @"E:\UotanStudio\reference\flash\mediatek\SP_Flash_Tool_v5.2228\Lib\DA_PL.bin",
        @"E:\UotanStudio\reference\flash\mediatek\SP_Flash_Tool_v5.2228\Lib\DA_SWSEC.bin",
        @"E:\UotanStudio\reference\flash\mediatek\mtkclient-main\mtkclient\Loader\MTK_AllInOne_DA_mt6590.bin",
        @"E:\UotanStudio\reference\flash\mediatek\mtkclient-main\mtkclient\Loader\MTK_AllInOne_DA_7687.bin",
        @"E:\UotanStudio\reference\flash\mediatek\mtkclient-main\mtkclient\Loader\MTK_AllInOne_DA_2625.bin"
    ];

    internal static void List(IEnumerable<string>? loaders, Action<string> log)
    {
        foreach (var path in loaders ?? DefaultLoaderCandidates())
        {
            if (!File.Exists(path))
            {
                log($"{Path.GetFileName(path)}: missing");
                continue;
            }

            try
            {
                using var da = MtkDaFile.Open(path);
                log($"{Path.GetFileName(path)}: entries={da.Metadata.Entries.Count} " +
                    $"layout={da.Metadata.Layout} v6header={da.Metadata.HeaderIndicatesV6}");
                foreach (var (entry, index) in da.Metadata.Entries.Select((entry, index) => (entry, index)))
                {
                    log($"   [{index}] hw=0x{entry.HardwareCode:X4} sub=0x{entry.HardwareSubCode:X4} " +
                        $"ver=0x{entry.HardwareVersion:X4} sw={(entry.SoftwareVersion is { } s ? $"0x{s:X4}" : "any")} " +
                        $"page={entry.PageSize} regions={entry.Regions.Count} entryRegion={entry.EntryRegionIndex}");
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                log($"{Path.GetFileName(path)}: {error.GetType().Name}: {error.Message}");
            }
        }
    }

    internal static DaMatch? Find(MtkDeviceInfo device, IEnumerable<string> loaders, Action<string> log)
    {
        DaMatch? best = null;
        var seen = new List<string>();

        foreach (var path in loaders)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                using var da = MtkDaFile.Open(path);
                var entries = da.Metadata.Entries;
                for (var index = 0; index < entries.Count; index++)
                {
                    var entry = entries[index];
                    if (entry.HardwareCode != device.HardwareCode)
                    {
                        continue;
                    }

                    // A 0x65xx hardware code is only served by the mt6590 container, except for 0x6580
                    // which the vendor tool also accepts from any container.
                    // 0x65xx 硬件码只由 mt6590 容器提供，但 0x6580 例外：厂商工具也接受任意容器中的它。
                    if ((entry.HardwareCode & 0xFF00) == 0x6500
                        && !Path.GetFileName(path).Contains("mt6590", StringComparison.OrdinalIgnoreCase)
                        && entry.HardwareCode != 0x6580)
                    {
                        continue;
                    }

                    seen.Add($"{Path.GetFileName(path)}[{index}] sub=0x{entry.HardwareSubCode:X4} " +
                        $"ver=0x{entry.HardwareVersion:X4} sw={(entry.SoftwareVersion is { } s ? $"0x{s:X4}" : "any")}" +
                        $" eligible={entry.HardwareVersion <= device.HardwareVersion}");

                    // The vendor toolchain keys strictly on the hardware code and then requires the
                    // entry's revisions not to exceed the device's: an entry is usable when
                    // loader.hw_version <= hwver and loader.sw_version <= swver. Sub-code is indexed
                    // by the same key and never used as a filter there.
                    // 厂商工具链严格以硬件码为键，然后要求条目版本不超过设备版本：当
                    // loader.hw_version <= hwver 且 loader.sw_version <= swver 时条目可用。
                    // 子码在那边同样按该键索引，从不作为过滤条件。
                    if (entry.HardwareVersion > device.HardwareVersion)
                    {
                        continue;
                    }

                    if (entry.SoftwareVersion is { } entrySoftware && entrySoftware > device.SoftwareVersion)
                    {
                        continue;
                    }

                    var regions = entry.Regions;
                    if (regions.Count < 2)
                    {
                        log($"    {Path.GetFileName(path)}[{index}] has {regions.Count} regions; two are required.");
                        continue;
                    }

                    // The vendor loader reads DA1 from the region after the entry region and DA2 from the
                    // one after that; the entry region itself holds the directory, not executable code.
                    // 厂商加载器把紧接条目区之后的区域作为 DA1，再下一个作为 DA2；
                    // 条目区本身存放目录，而不是可执行代码。
                    var stageOne = entry.EntryRegionIndex + 1;
                    var stageTwo = entry.EntryRegionIndex + 2;
                    if (stageTwo >= regions.Count)
                    {
                        log($"    {Path.GetFileName(path)}[{index}] has {regions.Count} regions; " +
                            $"stages {stageOne}/{stageTwo} are out of range.");
                        continue;
                    }

                    best = new DaMatch(path, entry, index, stageOne, stageTwo,
                        $"{Path.GetFileName(path)} entry {index}");
                    break;
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                log($"  {Path.GetFileName(path)}: {error.GetType().Name}: {error.Message}");
            }

            if (best is not null)
            {
                break;
            }
        }

        if (best is null)
        {
            log($"no eligible entry for hw=0x{device.HardwareCode:X4} sub=0x{device.HardwareSubCode:X4} " +
                $"ver=0x{device.HardwareVersion:X4} sw=0x{device.SoftwareVersion:X4}.");
            if (seen.Count == 0)
            {
                log("no container holds any entry with this hardware code.");
            }
            else
            {
                log("candidates sharing the hardware code:");
                foreach (var candidate in seen)
                {
                    log($"   {candidate}");
                }
            }
        }

        return best;
    }
}