using UotanStudio.Comm.Protocols.Edl;
using UotanStudio.Services.Devices;

namespace UotanStudio.Services.Flash;

/// <summary>
/// The result of an on-target SHA-256 query: the partition name, its range and the hex digest reported by
/// the target.
/// <para>目标侧 SHA-256 摘要读取结果：分区名、范围与目标上报的十六进制摘要。</para>
/// </summary>
/// <param name="Partition">The partition that was hashed. <para>被摘要的分区名。</para></param>
/// <param name="StartSector">The partition start sector. <para>分区起始扇区。</para></param>
/// <param name="SectorCount">The sector count covered by the digest. <para>参与摘要的扇区数。</para></param>
/// <param name="Lun">The owning physical partition number. <para>所在物理分区号。</para></param>
/// <param name="Sha256Hex">The hex digest reported by the target; <see langword="null"/> when the target
/// reported none. <para>目标上报的十六进制摘要；目标未上报时为 <see langword="null"/>。</para></param>
public sealed record EdlPartitionDigest(string Partition, long StartSector, long SectorCount, int Lun,
    string? Sha256Hex);

/// <summary>
/// Runs the Firehose setbootablestoragedrive and getsha256digest verbs with input validation. Core methods
/// take an initialized <see cref="FirehoseProtocol"/>; entry points open an EDL session and delegate.
/// <para>带输入校验地执行 Firehose 的 setbootablestoragedrive 与 getsha256digest 动词。核心方法接受已初始化的
/// <see cref="FirehoseProtocol"/>，入口负责打开 EDL 会话后转调。</para>
/// </summary>
internal static class EdlStorageControlService
{
    /// <summary>
    /// Sets the target bootable storage drive to the given number, accepting values from 0 through 255.
    /// A target NAK raises <see cref="EdlException"/> from the protocol and becomes a failure result here.
    /// <para>把目标的可引导存储驱动器设为指定编号，接受 0 到 255。目标返回 NAK 时协议层抛出
    /// <see cref="EdlException"/>，本方法把它转成失败结果。</para>
    /// </summary>
    /// <param name="firehose">An initialized Firehose protocol. <para>已初始化的 Firehose 协议实例。</para></param>
    /// <param name="value">The bootable storage drive number. <para>可引导存储驱动器编号。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The set result. <para>设置结果。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 through 255. <para>编号不在 0 到 255
    /// 之间。</para></exception>
    internal static async Task<FlashResult> SetBootableStorageDriveCoreAsync(FirehoseProtocol firehose, int value,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(firehose);
        var response = await firehose.SetBootableStorageDriveAsync(value, ct).ConfigureAwait(false);
        return new FlashResult
        {
            Success = response.IsAck,
            Summary = $"Set bootable storage drive to {value}. 已将可引导存储驱动器设为 {value}。"
        };
    }

    /// <summary>
    /// Asks the target to compute a SHA-256 over the full range of the named partition in its own GPT without
    /// reading the partition data back. The name is matched exactly, and a null LUN scans across LUNs.
    /// <para>请求目标对自身 GPT 中具名分区的完整范围计算 SHA-256，无需把分区数据读回。分区名精确匹配，
    /// LUN 为空时跨 LUN 扫描定位。</para>
    /// </summary>
    /// <param name="firehose">An initialized Firehose protocol. <para>已初始化的 Firehose 协议实例。</para></param>
    /// <param name="partition">The partition name to digest. <para>要摘要的分区名。</para></param>
    /// <param name="lun">The explicit LUN; <see langword="null"/> scans by name. <para>显式 LUN；为空时按名
    /// 扫描。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The digest result. <para>摘要结果。</para></returns>
    /// <exception cref="KeyNotFoundException">No readable GPT carries the partition name. <para>没有可读 GPT
    /// 上报该分区名。</para></exception>
    internal static async Task<EdlPartitionDigest> GetPartitionSha256CoreAsync(FirehoseProtocol firehose,
        string partition, int? lun, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(firehose);
        var (snapshot, entry) = await EdlGptPartitionService.ResolvePartitionSnapshotAsync(firehose, partition, lun, ct)
            .ConfigureAwait(false);
        var start = checked((long)entry.FirstLba);
        var sectors = checked((long)(entry.LastLba - entry.FirstLba + 1UL));
        var digest = await firehose.GetSha256DigestAsync(start, sectors, snapshot.Geometry.PhysicalPartitionNumber, ct)
            .ConfigureAwait(false);
        return new EdlPartitionDigest(partition, start, sectors, snapshot.Geometry.PhysicalPartitionNumber,
            digest is { } bytes ? Convert.ToHexStringLower(bytes) : null);
    }

    /// <summary>
    /// Opens an EDL session and sets the device bootable storage drive to the given number through
    /// <see cref="SetBootableStorageDriveCoreAsync"/>.
    /// <para>打开 EDL 会话，经 <see cref="SetBootableStorageDriveCoreAsync"/> 把设备端可引导存储驱动器设为
    /// 指定编号。</para>
    /// </summary>
    /// <param name="device">The target EDL device. <para>目标 EDL 设备。</para></param>
    /// <param name="value">The bootable storage drive number, UFS→1 and eMMC→0. <para>可引导存储驱动器编号，
    /// UFS→1、eMMC→0。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <param name="programmerPath">Optional programmer override path. <para>可选的编程器覆盖路径。</para></param>
    /// <param name="initialization">Optional session authentication options. <para>可选的会话认证选项。</para></param>
    /// <returns>The set result. <para>设置结果。</para></returns>
    internal static Task<FlashResult> SetBootableStorageDriveAsync(DeviceTarget device, int value, CancellationToken ct,
        string? programmerPath = null, EdlInitializationOptions? initialization = null)
        => App.DeviceMonitor.RunEdlAsync(device.Endpoint, async session =>
        {
            using var programmer = OpenProgrammer(programmerPath);
            await session.EnsureInitializedAsync(programmer, initialization, ct).ConfigureAwait(false);
            var firehose = session.Firehose
                ?? throw new InvalidOperationException("EDL session has no Firehose protocol instance. EDL 会话缺少 Firehose 实例。");
            return await SetBootableStorageDriveCoreAsync(firehose, value, ct).ConfigureAwait(false);
        }, ct);

    /// <summary>
    /// Opens an EDL session and asks the target for the SHA-256 digest of the named partition through
    /// <see cref="GetPartitionSha256CoreAsync"/>.
    /// <para>打开 EDL 会话，经 <see cref="GetPartitionSha256CoreAsync"/> 请求目标计算具名分区的 SHA-256
    /// 摘要。</para>
    /// </summary>
    /// <param name="device">The target EDL device. <para>目标 EDL 设备。</para></param>
    /// <param name="partition">The partition name to digest. <para>要摘要的分区名。</para></param>
    /// <param name="lun">The explicit LUN; <see langword="null"/> scans by name. <para>显式 LUN；为空时按名
    /// 扫描。</para></param>
    /// <param name="ct">Cancellation token. <para>取消令牌。</para></param>
    /// <param name="programmerPath">Optional programmer override path. <para>可选的编程器覆盖路径。</para></param>
    /// <param name="initialization">Optional session authentication options. <para>可选的会话认证选项。</para></param>
    /// <returns>The digest result. <para>摘要结果。</para></returns>
    internal static Task<EdlPartitionDigest> GetPartitionSha256Async(DeviceTarget device, string partition, int? lun,
        CancellationToken ct, string? programmerPath = null, EdlInitializationOptions? initialization = null)
        => App.DeviceMonitor.RunEdlAsync(device.Endpoint, async session =>
        {
            using var programmer = OpenProgrammer(programmerPath);
            await session.EnsureInitializedAsync(programmer, initialization, ct).ConfigureAwait(false);
            var firehose = session.Firehose
                ?? throw new InvalidOperationException("EDL session has no Firehose protocol instance. EDL 会话缺少 Firehose 实例。");
            return await GetPartitionSha256CoreAsync(firehose, partition, lun, ct).ConfigureAwait(false);
        }, ct);

    private static Stream? OpenProgrammer(string? programmerPath) =>
        programmerPath is { } loader && File.Exists(loader) ? File.OpenRead(loader) : null;
}