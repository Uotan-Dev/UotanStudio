using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.ViewModels.Devices;

namespace UotanStudio.Services.Flash;

/// <summary>
/// Applies a UFS provisioning descriptor file to a device, writing descriptor slots only after explicit
/// confirmation and reporting the LUN table it would send.
/// <para>把一份 UFS provisioning 描述符文件落到设备，只在显式确认后写出描述符槽位，并报告将要发送的 LUN 表。</para>
/// </summary>
internal static class EdlUfsProvisionService
{
    /// <summary>
    /// Reports the UFS configuration that would be sent without touching the device: it reads the storage
    /// geometry once and never frames a <c>&lt;ufs&gt;</c> command.
    /// <para>报告将要发送的 UFS 配置而不接触设备：只读取一次存储几何，绝不成帧任何
    /// <c>&lt;ufs&gt;</c> 命令。</para>
    /// </summary>
    /// <param name="device">The target EDL device. <para>目标 EDL 设备。</para></param>
    /// <param name="profilePath">Path to the descriptor profile file. <para>描述符文件路径。</para></param>
    /// <param name="progress">Phase progress. <para>阶段进度。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <param name="programmerPath">Optional programmer override path. <para>可选的编程器覆盖路径。</para></param>
    /// <param name="initialization">Optional session authentication options. <para>可选的会话认证选项。</para></param>
    /// <returns>A result describing what would be sent. <para>描述将要发送内容的结果。</para></returns>
    internal static async Task<FlashResult> PreviewAsync(DeviceInfo device, string profilePath,
        IProgress<FlashProgress>? progress, CancellationToken ct, string? programmerPath = null,
        EdlInitializationOptions? initialization = null)
    {
        var profile = await UfsProvisionProfileLoader.LoadAsync(profilePath, ct).ConfigureAwait(false);
        return await App.DeviceMonitor.RunEdlAsync(device.Device, async session =>
        {
            using var programmer = OpenProgrammer(programmerPath);
            await session.EnsureInitializedAsync(programmer, initialization, ct).ConfigureAwait(false);
            return await PreviewCoreAsync(RequireFirehose(session), profile, progress, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the descriptor slots; an unconfirmed call is refused before any byte is issued.
    /// <para>写入描述符槽位；未确认时在发出任何字节之前拒绝。</para>
    /// </summary>
    /// <param name="device">The target EDL device. <para>目标 EDL 设备。</para></param>
    /// <param name="profilePath">Path to the descriptor profile file. <para>描述符文件路径。</para></param>
    /// <param name="confirmed">Whether the write was explicitly confirmed. <para>是否已显式确认写入。</para></param>
    /// <param name="progress">Phase progress. <para>阶段进度。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <param name="programmerPath">Optional programmer override path. <para>可选的编程器覆盖路径。</para></param>
    /// <param name="initialization">Optional session authentication options. <para>可选的会话认证选项。</para></param>
    /// <returns>The provisioning result. <para>写入结果。</para></returns>
    internal static async Task<FlashResult> ProvisionAsync(DeviceInfo device, string profilePath, bool confirmed,
        IProgress<FlashProgress>? progress, CancellationToken ct, string? programmerPath = null,
        EdlInitializationOptions? initialization = null)
    {
        var profile = await UfsProvisionProfileLoader.LoadAsync(profilePath, ct).ConfigureAwait(false);
        return await App.DeviceMonitor.RunEdlAsync(device.Device, async session =>
        {
            using var programmer = OpenProgrammer(programmerPath);
            await session.EnsureInitializedAsync(programmer, initialization, ct).ConfigureAwait(false);
            return await ProvisionCoreAsync(RequireFirehose(session), profile, confirmed, progress, ct)
                .ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Discards the pending UFS configuration on the target; an unconfirmed call is refused before any byte
    /// is issued.
    /// <para>丢弃目标上待生效的 UFS 配置；未确认时在发出任何字节之前拒绝。</para>
    /// </summary>
    /// <param name="device">The target EDL device. <para>目标 EDL 设备。</para></param>
    /// <param name="confirmed">Whether the reset was explicitly confirmed. <para>是否已显式确认复位。</para></param>
    /// <param name="progress">Phase progress. <para>阶段进度。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <param name="programmerPath">Optional programmer override path. <para>可选的编程器覆盖路径。</para></param>
    /// <param name="initialization">Optional session authentication options. <para>可选的会话认证选项。</para></param>
    /// <returns>The reset result. <para>复位结果。</para></returns>
    internal static async Task<FlashResult> ResetAsync(DeviceInfo device, bool confirmed,
        IProgress<FlashProgress>? progress, CancellationToken ct, string? programmerPath = null,
        EdlInitializationOptions? initialization = null)
    {
        return await App.DeviceMonitor.RunEdlAsync(device.Device, async session =>
        {
            using var programmer = OpenProgrammer(programmerPath);
            await session.EnsureInitializedAsync(programmer, initialization, ct).ConfigureAwait(false);
            return await ResetCoreAsync(RequireFirehose(session), confirmed, progress, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Preview core: reads the device geometry, validates the capacities and reports the LUN table that
    /// would be sent, framing no <c>&lt;ufs&gt;</c> command at all.
    /// <para>预览核心：读取设备几何、校验容量并报告将要发送的 LUN 表，全程不成帧任何
    /// <c>&lt;ufs&gt;</c> 命令。</para>
    /// </summary>
    /// <param name="firehose">The configured Firehose protocol. <para>已配置的 Firehose 协议实例。</para></param>
    /// <param name="profile">The validated descriptor set. <para>已经校验过的描述符集合。</para></param>
    /// <param name="progress">Phase progress. <para>阶段进度。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>A result describing what would be sent. <para>描述将要发送内容的结果。</para></returns>
    /// <exception cref="InvalidOperationException">The device did not report UFS storage. <para>设备未上报
    /// UFS 存储。</para></exception>
    /// <exception cref="InvalidDataException">A declared capacity disagrees with the reported block width.
    /// <para>声明容量与设备上报的块宽度不符。</para></exception>
    internal static async Task<FlashResult> PreviewCoreAsync(FirehoseProtocol firehose,
        UfsProvisionProfile profile, IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(firehose);
        ArgumentNullException.ThrowIfNull(profile);
        ct.ThrowIfCancellationRequested();
        RequireUfsStorage(firehose);
        var geometry = await firehose.GetStorageGeometryAsync(0, ct).ConfigureAwait(false);
        profile.ValidateAgainstGeometry(geometry);
        var plan = DescribePlan(profile, geometry, firehose.Configuration.Slot);
        progress?.Report(new FlashProgress(null, 100,
            $"UFS provisioning preview: {plan} 以上为将要发送的配置，本次预览未向设备发送任何 provisioning 命令。"));
        return new FlashResult
        {
            Success = true,
            Summary = $"Previewed {profile.Bodies.Count} UFS LUN descriptor(s) ({profile.TotalSizeInKb} KB total) " +
                $"without sending a provisioning command. " +
                $"已预览 {profile.Bodies.Count} 个 UFS LUN 描述符（合计 {profile.TotalSizeInKb} KB），未发送任何配置命令。 {plan}"
        };
    }

    /// <summary>
    /// Write core: frames and sends the descriptors only after confirmation, the device-reported storage
    /// technology and the capacity alignment have all passed.
    /// <para>写入核心：确认、设备存储类型、容量对齐三者都通过后，才把描述符交给协议成帧并发送。</para>
    /// </summary>
    /// <param name="firehose">The configured Firehose protocol. <para>已配置的 Firehose 协议实例。</para></param>
    /// <param name="profile">The validated descriptor set. <para>已经校验过的描述符集合。</para></param>
    /// <param name="confirmed">Whether writing the descriptor slots was explicitly confirmed. <para>是否已
    /// 显式确认写入描述符槽位。</para></param>
    /// <param name="progress">Phase progress. <para>阶段进度。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The provisioning result. <para>写入结果。</para></returns>
    /// <exception cref="InvalidOperationException">The write was not confirmed, or the device did not report
    /// UFS storage. <para>未确认，或设备未上报 UFS 存储。</para></exception>
    /// <exception cref="InvalidDataException">A declared capacity disagrees with the reported block width.
    /// <para>声明容量与设备上报的块宽度不符。</para></exception>
    internal static async Task<FlashResult> ProvisionCoreAsync(FirehoseProtocol firehose,
        UfsProvisionProfile profile, bool confirmed, IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(firehose);
        ArgumentNullException.ThrowIfNull(profile);
        if (!confirmed)
        {
            throw new InvalidOperationException(
                "UFS provisioning rewrites device descriptor slots and requires explicit confirmation. " +
                "写入 UFS 描述符槽位需要显式确认。");
        }

        ct.ThrowIfCancellationRequested();
        RequireUfsStorage(firehose);
        var geometry = await firehose.GetStorageGeometryAsync(0, ct).ConfigureAwait(false);
        profile.ValidateAgainstGeometry(geometry);
        var slot = firehose.Configuration.Slot;
        var commands = profile.FrameCommands(slot).Count;
        progress?.Report(new FlashProgress(null, 10,
            $"Sending {commands} UFS provisioning command(s). 正在发送 {commands} 条 UFS 配置命令。"));
        await firehose.ProvisionUfsAsync(profile.Common, profile.Bodies, profile.Epilogue, profile.Commit, ct)
            .ConfigureAwait(false);
        var plan = DescribePlan(profile, geometry, slot);
        progress?.Report(new FlashProgress(null, 100,
            $"UFS provisioning finished: {plan} UFS 配置命令已发送完成。"));
        return new FlashResult
        {
            Success = true,
            Summary = $"{commands} UFS provisioning command(s) sent on slot {slot} " +
                $"({profile.Bodies.Count} LUN descriptor(s), {profile.TotalSizeInKb} KB total, " +
                $"commit={(profile.Commit ? "persisted" : "pending")}). " +
                $"已在槽位 {slot} 发送 {commands} 条 UFS 配置命令（{profile.Bodies.Count} 个 LUN 描述符，" +
                $"合计 {profile.TotalSizeInKb} KB，{(profile.Commit ? "已提交持久化" : "待后续提交")}）。 {plan}"
        };
    }

    /// <summary>
    /// Reset core: sends one epilogue with a zero commit value to discard a pending configuration; an
    /// unconfirmed call issues no bytes.
    /// <para>复位核心：发送一条 commit 为 0 的结语，丢弃待生效配置；未确认时不发出任何字节。</para>
    /// </summary>
    /// <param name="firehose">The configured Firehose protocol. <para>已配置的 Firehose 协议实例。</para></param>
    /// <param name="confirmed">Whether the reset was explicitly confirmed. <para>是否已显式确认复位。</para></param>
    /// <param name="progress">Phase progress. <para>阶段进度。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The reset result. <para>复位结果。</para></returns>
    /// <exception cref="InvalidOperationException">The call was not confirmed, or the device did not report
    /// UFS storage. <para>未确认，或设备未上报 UFS 存储。</para></exception>
    internal static async Task<FlashResult> ResetCoreAsync(FirehoseProtocol firehose, bool confirmed,
        IProgress<FlashProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(firehose);


        if (!confirmed)
        {
            throw new InvalidOperationException(
                "Discarding a pending UFS configuration requires explicit confirmation. " +
                "丢弃待生效的 UFS 配置需要显式确认。");
        }

        ct.ThrowIfCancellationRequested();
        RequireUfsStorage(firehose);
        progress?.Report(new FlashProgress(null, 50,
            "Discarding the pending UFS configuration. 正在丢弃待生效的 UFS 配置。"));
        await firehose.UfsResetAsync(ct).ConfigureAwait(false);
        progress?.Report(new FlashProgress(null, 100,
            "Pending UFS configuration discarded. 已丢弃待生效的 UFS 配置。"));
        return new FlashResult
        {
            Success = true,
            Summary = "Pending UFS provisioning configuration discarded with a commit=0 epilogue; nothing was " +
                "persisted. 已通过 commit=0 的结语丢弃待生效的 UFS provisioning 配置，未持久化任何内容。"
        };
    }

    /// <summary>
    /// Renders the descriptor set together with the device-reported block width as one readable LUN table,
    /// shared by the preview and write summaries.
    /// <para>把描述符集合与设备上报的块宽度排成一行可读的 LUN 表，供预览与写入摘要共用。</para>
    /// </summary>
    /// <param name="profile">The descriptor set. <para>描述符集合。</para></param>
    /// <param name="geometry">The device-reported LUN 0 geometry. <para>设备上报的 LUN 0 几何。</para></param>
    /// <param name="slot">The Firehose slot. <para>Firehose 槽位。</para></param>
    /// <returns>A single-line summary. <para>单行摘要文本。</para></returns>
    internal static string DescribePlan(UfsProvisionProfile profile, FirehoseStorageGeometry geometry, int slot)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(geometry);
        var table = string.Join("; ", profile.Bodies.Select(body =>
            $"LUN {body.LUNum} = {body.SizeInKb} KB (enable {body.BLUEnable}, boot {body.BBootLUNID}, " +
            $"provisioning {body.BProvisioningType}, block size index {body.BLogicalBlockSize})"));
        return $"slot {slot}, device block width {geometry.SectorSize} bytes over {geometry.SectorCount} blocks, " +
            $"bNumberLU {profile.Common.BNumberLU}, LUNtoGrow {profile.Epilogue.LUNumToGrow}, " +
            $"commit={(profile.Commit ? "true" : "false")}, commands {profile.Bodies.Count + 2}: {table}";
    }

    /// <summary>
    /// Accepts only storage the device itself reported as UFS: the MemoryName returned in the configure
    /// acknowledgement is the evidence, not an assumed storage type.
    /// <para>只接受设备自己上报为 UFS 的存储：configure 应答里回报的 MemoryName 才是判据，猜测的存储类型
    /// 不作数。</para>
    /// </summary>
    /// <param name="firehose">The configured Firehose protocol. <para>已配置的 Firehose 协议实例。</para></param>
    /// <exception cref="InvalidOperationException">The device reported a storage type that is not UFS.
    /// <para>设备上报的存储类型不是 UFS。</para></exception>
    private static void RequireUfsStorage(FirehoseProtocol firehose)
    {
        if (firehose.Configuration.StorageType != FirehoseStorageType.Ufs)
        {
            throw new InvalidOperationException(
                $"The target reported {firehose.Configuration.StorageType} storage, so UFS provisioning was " +
                "refused before any command was framed. " +
                $"设备上报的存储类型为 {firehose.Configuration.StorageType}，已在成帧任何命令之前拒绝 UFS 配置。");
        }
    }

    private static FirehoseProtocol RequireFirehose(EdlSession session) => session.Firehose
        ?? throw new InvalidOperationException(
            "EDL session has no Firehose protocol instance. EDL 会话缺少 Firehose 实例。");

    private static Stream? OpenProgrammer(string? programmerPath) =>
        programmerPath is { } loader && File.Exists(loader) ? File.OpenRead(loader) : null;
}