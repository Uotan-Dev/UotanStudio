using System.Collections.ObjectModel;
using UotanStudio.Comm.Protocols.Bootrom;
using UotanStudio.Services;
using UotanStudio.Services.Devices;
using UotanStudio.UiAbstractions;

namespace UotanStudio.ViewModels.Flash;

/// <summary>
/// A read-only name and value displayed in the flash page device status table.
/// <para>烧录页面设备状态表格中的只读名称和值。</para>
/// </summary>
public sealed class FlashDeviceStatusEntry
{
    /// <summary>
    /// Group the entry belongs to, one of <see cref="FlashDeviceStatusRowKeys"/>; the view draws the first
    /// entry of a group with the group's label and aligns the rest under it, which turns one medium's
    /// several values into several lines instead of one line carrying all of them.
    /// <para>条目所属分组，取 <see cref="FlashDeviceStatusRowKeys"/> 之一；视图为每组首条绘制分组标签并把
    /// 其余条目对齐到其下，从而让一个介质的多个取值分行显示而不是挤在同一行。</para>
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    /// Display name of the status item; empty for a value line that continues the group's label.
    /// <para>状态项的显示名称；延续分组标签的取值行为空。</para>
    /// </summary>
    public required string Name { get; init; }

    /// <summary>Display value of the status item.<para>状态项的显示值。</para></summary>
    public required string Value { get; init; }
}

/// <summary>
/// Group keys the device status table uses to fold one subject into consecutive lines.
/// <para>设备状态表用来把一个主题折成连续多行的分组键。</para>
/// </summary>
public static class FlashDeviceStatusRowKeys
{
    /// <summary>The loader stage line.<para>loader 阶段行。</para></summary>
    public const string Stage = "stage";

    /// <summary>The chip the loader reports.<para>loader 上报的芯片。</para></summary>
    public const string Chip = "chip";

    /// <summary>The flash id the loader reports.<para>loader 上报的 flash id。</para></summary>
    public const string FlashId = "flashid";

    /// <summary>The capability word and its decoded bits.<para>能力字及其解码出的位。</para></summary>
    public const string Capability = "capability";

    /// <summary>The medium the loader answers from right now, with its geometry.<para>loader 当前应答的介质及其几何。</para></summary>
    public const string Storage = "storage";

    /// <summary>Another present medium and its geometry.<para>另一个存在的介质及其几何。</para></summary>
    public const string StorageProbe = "storage-probe";
}

public sealed partial class FlashViewModel
{
    /// <summary>
    /// Status rows displayed by the view; empty until the read-device-info action fills them.
    /// <para>视图显示的状态行；读取设备信息动作填充前为空。</para>
    /// </summary>
    public ObservableCollection<FlashDeviceStatusEntry> DeviceStatus { get; } = [];

    /// <summary>
    /// Serializes device status publications: rows are published only when no newer fill has started,
    /// so a stale click cannot overwrite newer rows.
    /// <para>串行化设备状态发布：仅在没有更新的填充启动时才发布行，防止过期的点击覆盖新行。</para>
    /// </summary>
    private int _rkStatusRefreshEpoch;

    /// <summary>
    /// Cooperative bound for the read-only RKDL status query, so the table is never held up by a device.
    /// <para>只读 RKDL 状态查询的协作式时限；到点即放弃，不让该表格被设备拖住。</para>
    /// </summary>
    private const int RkdlStatusTimeoutMs = 5000;

    /// <summary>
    /// Fills the device status rows from the read-device-info action: a Rockchip device running the
    /// loader (usbplug stage) gets the answers of the read-only information commands and its storage selector
    /// is narrowed to the media the device actually serves, a maskrom or bootrom device gets the
    /// waiting-for-loader state without any device query, and every other target clears the rows.
    /// <para>由读取设备信息动作填充设备状态行：运行 loader（usbplug 阶段）的瑞芯微设备填入只读信息命令的
    /// 应答，并把存储选择框收窄到设备实际提供的介质；maskrom/bootrom 设备只填等待加载 loader 的状态而
    /// 不发起任何设备查询；其他目标清空状态行。</para>
    /// </summary>
    /// <remarks>
    /// The table is never filled automatically: only the read-device-info action calls this. The USB boot
    /// ROM stages answer only the vendor control requests the discovery layer already used, and their bulk
    /// CBW behavior on information commands is unreliable (a bootrom port can answer a CRC error), so no
    /// session is opened for them and their rows carry the stage condition only. The loader query runs on
    /// the shared RKDL lease path with the page's cooperative timeout and reports failures by publishing
    /// the failure row at debug level, like the other read-only identity queries.
    /// <para>该表从不自动填充：只有读取设备信息动作调用此方法。USB boot ROM 阶段只应答发现层已用过的
    /// vendor 控制请求，其对信息命令的 CBW 批量行为不可靠（bootrom 端口可能以 CRC 错误应答），因此不为
    /// 它们打开会话，行中只写阶段状态。loader 查询走共享 RKDL 租约路径并受页面协作时限约束，失败时按
    /// debug 级别发布失败行，与其他只读身份查询一致。</para>
    /// </remarks>
    internal async void RefreshRkdlDeviceStatus()
    {
        if (_surface is null)
        {
            return;
        }

        var language = _localizer;
        var epoch = ++_rkStatusRefreshEpoch;
        List<FlashDeviceStatusEntry> rows;
        List<int>? storages = null;
        var currentStorage = -1;
        if (SelectedDevice is not { Protocol: DeviceProtocolKind.Rkdl } device)
        {
            rows = [];
        }
        else if (!IsRockchipLoaderStageDevice)
        {
            // maskrom/bootrom 阶段不打开会话：只写阶段状态，等待加载 loader 后再查询设备信息。
            rows =
            [
                new()
                {
                    Key = FlashDeviceStatusRowKeys.Stage,
                    Name = language.GetString("Flash_RkStatus"),
                    Value = language.GetString("Flash_RkStatusWaitingLoader"),
                },
            ];
        }
        else
        {
            try
            {
                using var timeout = new System.Threading.CancellationTokenSource(RkdlStatusTimeoutMs);
                (rows, storages, currentStorage) = await Task.Run(() => App.DeviceMonitor.RunRkdlAsync(device,
                    session => Task.FromResult(BuildRkdlStatusRows(session, language)), timeout.Token)
                    .GetAwaiter().GetResult(), timeout.Token).ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                // 只读状态探测不得让 async void 漏出任何异常：USB 错误、会话失效与取消在此全部降级为失败行。
                _log.Write(LogLevel.Debug, "Flash",
                    $"RKDL device status for {device.Key} is unavailable: {exception.Message}");
                rows =
                [
                    new()
                    {
                        Key = FlashDeviceStatusRowKeys.Stage,
                        Name = language.GetString("Flash_RkStatus"),
                        Value = language.GetString("Flash_RkStatusQueryFailed"),
                    },
                ];
            }
        }

        if (epoch != _rkStatusRefreshEpoch)
        {
            return;
        }

        DeviceStatus.Clear();
        foreach (var row in rows)
        {
            DeviceStatus.Add(row);
        }

        if (storages is not null)
        {
            SetRkStorageOptions(storages);
            if (currentStorage >= 0)
            {
                SelectRkStorage(currentStorage);
            }
        }
    }

    /// <summary>
    /// Builds the device status rows from one open RKDL session: the loader-ready line first, then the
    /// information commands, and the storage groups the running loader answers; the media those groups list
    /// and the current medium's ordinal are returned so the selector can offer the same set.
    /// <para>从一个已打开的 RKDL 会话构建设备状态行：先写 loader 已就绪行，再写信息类命令的应答，最后写
    /// 运行中的 loader 才能应答的存储分组；同时返回各组列出的介质与当前介质序号，供选择框提供同一集合。</para>
    /// </summary>
    /// <param name="session">Open RKDL session on the selected device.
    /// <para>选中设备上已打开的 RKDL 会话。</para></param>
    /// <param name="language">Localized string lookup for the row labels.
    /// <para>行标签使用的本地化字符串查询。</para></param>
    /// <returns>The rows in display order, the ordinals of the media that produced a storage group, and the
    /// current medium's ordinal (or <c>-1</c> when it produced no group); the ordinals are
    /// <see langword="null"/> when the current medium could not be named.
    /// <para>按显示顺序排列的状态行、产生了存储分组的介质序号，以及当前介质序号（未产生分组时为 <c>-1</c>）；
    /// 当前介质无法命名时序号为 <see langword="null"/>。</para></returns>
    private static (List<FlashDeviceStatusEntry> Rows, List<int>? Storages, int CurrentStorage)
        BuildRkdlStatusRows(RkdlSession session, ILocalizer language)
    {
        var rows = new List<FlashDeviceStatusEntry>
        {
            new()
            {
                Key = FlashDeviceStatusRowKeys.Stage,
                Name = language.GetString("Flash_RkStatus"),
                Value = language.GetString("Flash_RkStatusLoaderReady"),
            },
        };

        var capability = session.ReadCapability();
        var chipInfo = session.ReadChipInfo();
        var flashId = session.ReadFlashId();
        rows.Add(new FlashDeviceStatusEntry
        {
            Key = FlashDeviceStatusRowKeys.Chip,
            Name = language.GetString("Flash_RkChipName"),
            Value = chipInfo.ChipName,
        });
        rows.Add(new FlashDeviceStatusEntry
        {
            Key = FlashDeviceStatusRowKeys.FlashId,
            Name = language.GetString("Flash_RkFlashId"),
            Value = $"0x{Convert.ToHexString(flashId)}",
        });
        rows.Add(new FlashDeviceStatusEntry
        {
            Key = FlashDeviceStatusRowKeys.Capability,
            Name = language.GetString("Flash_RkCapability"),
            Value = $"0x{Convert.ToHexString(capability.Raw)}",
        });
        foreach (var line in capability.Describe())
        {
            rows.Add(new FlashDeviceStatusEntry
            {
                Key = FlashDeviceStatusRowKeys.Capability,
                Name = string.Empty,
                Value = line,
            });
        }

        var (storages, currentStorage) = AppendRkFlashRows(rows, session, language);
        return (rows, storages, currentStorage);
    }

    /// <summary>
    /// Probes the storage media the vendor table offers and appends one group per medium that reported flash
    /// geometry: the current medium first under <see cref="FlashDeviceStatusRowKeys.Storage"/>, then every other
    /// medium the read-back mask proves present under <see cref="FlashDeviceStatusRowKeys.StorageProbe"/>. A
    /// medium whose read-back mask does not name it, or that answers without reporting any geometry, produces
    /// no row, so the table and the selector only offer media the device actually serves; the device is
    /// switched back to the medium it started on.
    /// <para>探测厂商表提供的存储介质，并为每个报告了闪存几何的介质追加一组：当前介质在前，写在
    /// <see cref="FlashDeviceStatusRowKeys.Storage"/> 下；其余经读回掩码证明存在的介质写在
    /// <see cref="FlashDeviceStatusRowKeys.StorageProbe"/> 下。读回掩码不指向、或应答了却没有报告任何几何的
    /// 介质不产生行，使表格与选择框只提供设备真正提供的介质；结束时设备切回探测前的介质。</para>
    /// </summary>
    /// <param name="rows">Row list under construction.<para>正在构建的行列表。</para></param>
    /// <param name="session">Open RKDL session in loader mode.<para>loader 模式下已打开的 RKDL 会话。</para></param>
    /// <param name="language">Localized string lookup for the row labels.
    /// <para>行标签使用的本地化字符串查询。</para></param>
    /// <returns>The ordinals of the media that produced a group, and the current medium's ordinal when it
    /// produced one (otherwise <c>-1</c>); <see langword="null"/> ordinals mean the current medium could not
    /// be named, so the selector is left as it was.
    /// <para>产生了分组的介质序号，以及当前介质产生分组时的序号（否则为 <c>-1</c>）；序号为
    /// <see langword="null"/> 表示当前介质无法命名，选择框保持原样。</para></returns>
    private static (List<int>? Storages, int CurrentStorage) AppendRkFlashRows(
        List<FlashDeviceStatusEntry> rows, RkdlSession session, ILocalizer language)
    {
        var initialMask = session.ReadStorageMask();
        var initialIndex = RkdlStorage.IndexOfMask(initialMask);
        if (initialIndex < 0)
        {
            rows.Add(new FlashDeviceStatusEntry
            {
                Key = FlashDeviceStatusRowKeys.Storage,
                Name = $"0x{initialMask:X8}",
                Value = string.Empty,
            });
            return (null, -1);
        }

        // 当前介质先行：官方表里排在它前面的候选介质不会把它的几何挤到表格下半部分。
        var media = new List<(string Key, int Ordinal, IReadOnlyList<string> Geometry)>(RkdlStorage.Names.Length)
        {
            (FlashDeviceStatusRowKeys.Storage, initialIndex,
                DescribeRkFlashGeometry(session.ReadFlashInfo(), language)),
        };
        for (var index = 0; index < RkdlStorage.Names.Length; index++)
        {
            if (index == initialIndex)
            {
                continue;
            }

            try
            {
                session.ChangeStorage(RkdlStorage.WireBits[index]);
                if (session.ReadStorageMask() != 1u << RkdlStorage.WireBits[index])
                {
                    continue;
                }

                media.Add((FlashDeviceStatusRowKeys.StorageProbe, index,
                    DescribeRkFlashGeometry(session.ReadFlashInfo(), language)));
            }
            catch (Exception exception) when (exception is RkdlException or IOException or TimeoutException
                or InvalidOperationException or ObjectDisposedException)
            {
                // 无法服务的介质不产生行：日志侧已在同一动作的第一遍探测中记录失败。
                _ = exception;
            }
        }

        try
        {
            session.ChangeStorage(RkdlStorage.WireBits[initialIndex]);
        }
        catch (Exception exception) when (exception is RkdlException or IOException or TimeoutException
            or InvalidOperationException or ObjectDisposedException)
        {
            // 切回失败的提示同样由第一遍探测的日志给出，不在表格里补介质行。
            _ = exception;
        }

        var storages = AppendRkStorageRows(rows, media);
        return (storages, storages.Contains(initialIndex) ? initialIndex : -1);
    }

    /// <summary>
    /// Appends one group per medium that reported geometry: the plain medium name leads the group and each
    /// geometry value gets its own row under it. A medium that reported nothing is left out.
    /// <para>为每个报告了几何的介质追加一组：纯介质名作为组首行，其下每个几何取值各占一行；没有报告任何内容的
    /// 介质不列出。</para>
    /// </summary>
    /// <param name="rows">Row list under construction.<para>正在构建的行列表。</para></param>
    /// <param name="media">Probed media in display order: group key, medium ordinal and the geometry it
    /// reported.<para>按显示顺序排列的探测结果：分组键、介质序号与其报告的几何。</para></param>
    /// <returns>The ordinals of the media that produced a group.<para>产生了分组的介质序号。</para></returns>
    internal static List<int> AppendRkStorageRows(
        List<FlashDeviceStatusEntry> rows,
        IReadOnlyList<(string Key, int Ordinal, IReadOnlyList<string> Geometry)> media)
    {
        var storages = new List<int>(media.Count);
        foreach (var (key, ordinal, geometry) in media)
        {
            if (ordinal < 0 || ordinal >= RkdlStorage.Names.Length || geometry.Count == 0)
            {
                continue;
            }

            storages.Add(ordinal);
            rows.Add(new FlashDeviceStatusEntry
            {
                Key = key,
                Name = RkdlStorage.Names[ordinal],
                Value = string.Empty,
            });
            foreach (var line in geometry)
            {
                rows.Add(new FlashDeviceStatusEntry { Key = key, Name = string.Empty, Value = line });
            }
        }

        return storages;
    }
}