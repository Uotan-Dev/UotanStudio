using System.Globalization;
using System.Text;

namespace UotanStudio.Comm.Protocols.Edl.Crash;

/// <summary>
/// One named memory region a crash-dump run downloads.
/// <para>崩溃转储运行要下载的一个命名内存区域。</para>
/// </summary>
/// <param name="Name">The region name used for the output file name.
/// <para>用于输出文件名的 region 名。</para></param>
/// <param name="Address">The physical start address. <para>物理起始地址。</para></param>
/// <param name="SizeBytes">The region size in bytes. <para>区域大小（字节）。</para></param>
public sealed record CrashDumpRegion(string Name, long Address, long SizeBytes)
{
    /// <summary>
    /// Validates the geometry; every field must stay inside the 32-bit DIAG address space.
    /// <para>校验几何范围；所有字段都必须落在 32 位 DIAG 地址空间内。</para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The geometry is negative or leaves the
    /// address space. <para>几何为负或离开地址空间。</para></exception>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(SizeBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(Address);
        if (Address + SizeBytes > 0x1_0000_0000L)
        {
            throw new ArgumentOutOfRangeException(nameof(Address),
                $"Region '{Name}' leaves the 32-bit DIAG address space.");
        }
    }
}

/// <summary>
/// Progress report for one crash-dump run.
/// <para>一次崩溃转储运行的进度报告。</para>
/// </summary>
/// <param name="Region">The region being downloaded. <para>正在下载的 region。</para></param>
/// <param name="BytesDone">Bytes downloaded inside the region. <para>region 内已下载字节数。</para></param>
/// <param name="RegionBytes">The region's total size. <para>region 总大小。</para></param>
public sealed record CrashDumpProgress(CrashDumpRegion Region, long BytesDone, long RegionBytes);

/// <summary>
/// The result of one crash-dump run: the dump folder, the generated device information file and
/// every region file that completed.
/// <para>一次崩溃转储运行的结果：转储目录、生成的设备信息文件与每个完成的 region 文件。</para>
/// </summary>
public sealed record CrashDumpResult
{
    /// <summary>The directory every dump file was written to. <para>全部转储文件写入的目录。</para></summary>
    public required string OutputDirectory { get; init; }

    /// <summary>The <c>device_info.txt</c> path, when one was written.
    /// <para><c>device_info.txt</c> 路径；已写入时提供。</para></summary>
    public string? DeviceInfoPath { get; init; }

    /// <summary>Region files that completed, with their byte counts.
    /// <para>完成的 region 文件及其字节数。</para></summary>
    public required IReadOnlyList<(string Region, string Path, long Bytes)> Downloads { get; init; }
}

/// <summary>
/// Orchestrates a Qualcomm crash-dump run over an initialized firehose session: downloads each
/// region through <see cref="FirehoseProtocol"/>, writes timestamped output files and generates the
/// device information summary. The storage must already be up; the run issues only reads.
/// <para>在已初始化的 firehose 会话上编排 Qualcomm 崩溃转储：经 <see cref="FirehoseProtocol"/>
/// 下载每个 region，写出带时间戳的输出文件并生成设备信息摘要。存储必须已就绪；本运行只发读取。</para>
/// </summary>
public sealed class CrashDumpCollector
{
    /// <summary>
    /// Maximum region size accepted per file (512 MiB), keeping every single dump bounded.
    /// <para>单个文件接受的 region 大小上限（512 MiB），保证每个转储有界。</para>
    /// </summary>
    public const long MaxRegionBytes = 512L * 1024 * 1024;

    private readonly Func<FirehoseProtocol> _firehoseFactory;

    /// <summary>
    /// Creates the collector against a factory returning the current, initialized firehose session.
    /// <para>以返回当前已初始化 firehose 会话的工厂创建收集器。</para>
    /// </summary>
    /// <param name="firehoseFactory">The session factory. <para>会话工厂。</para></param>
    public CrashDumpCollector(Func<FirehoseProtocol> firehoseFactory)
    {
        _firehoseFactory = firehoseFactory ?? throw new ArgumentNullException(nameof(firehoseFactory));
    }

    /// <summary>
    /// Downloads the given regions into a timestamped folder below <paramref name="outputRoot"/> and
    /// writes <c>device_info.txt</c>. A region the device refuses is reported and skipped; the run
    /// never writes to the device.
    /// <para>把给定 region 下载到 <paramref name="outputRoot"/> 下带时间戳的目录并写出
    /// <c>device_info.txt</c>。设备拒绝的 region 会被上报并跳过；本运行绝不写设备。</para>
    /// </summary>
    /// <param name="regions">The regions to download. <para>待下载 region。</para></param>
    /// <param name="outputRoot">The root directory for the timestamped run folder.
    /// <para>带时间戳运行目录的根目录。</para></param>
    /// <param name="deviceDescription">Text written into the device information file.
    /// <para>写入设备信息文件的文本。</para></param>
    /// <param name="skip8K">Whether to skip regions named for the 8K dump.
    /// <para>是否跳过 8K 转储命名的 region。</para></param>
    /// <param name="skip9K">Whether to skip regions named for the 9K dump.
    /// <para>是否跳过 9K 转储命名的 region。</para></param>
    /// <param name="progress">Optional per-region progress. <para>可选的逐 region 进度。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The run result. <para>运行结果。</para></returns>
    public async Task<CrashDumpResult> CollectAsync(IReadOnlyList<CrashDumpRegion> regions, string outputRoot,
        string deviceDescription, bool skip8K = false, bool skip9K = false,
        IProgress<CrashDumpProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        foreach (var region in regions)
        {
            region.Validate();
            if (region.SizeBytes > MaxRegionBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(regions),
                    $"Region '{region.Name}' is {region.SizeBytes} bytes; at most {MaxRegionBytes} are accepted per region.");
            }
        }

        var runDirectory = Path.Combine(outputRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(runDirectory);
        var firehose = _firehoseFactory();
        var downloads = new List<(string, string, long)>();
        foreach (var region in regions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (skip8K && region.Name.Contains("8k", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (skip9K && region.Name.Contains("9k", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var safeName = MakeSafeFileName(region.Name);
            var destinationPath = Path.Combine(runDirectory, safeName + ".bin");
            long written;
            try
            {
                await using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write,
                    FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await firehose.ReadToStreamAsync(destination, region.Address, region.SizeBytes,
                    safeName + ".bin", 0, cancellationToken).ConfigureAwait(false);
                written = destination.Length;
            }
            catch (EdlException)
            {
                TryDelete(destinationPath);
                continue;
            }

            downloads.Add((region.Name, destinationPath, written));
        }

        var infoPath = Path.Combine(runDirectory, "device_info.txt");
        await File.WriteAllTextAsync(infoPath, BuildDeviceInfo(deviceDescription, downloads), cancellationToken)
            .ConfigureAwait(false);
        return new CrashDumpResult
        {
            OutputDirectory = runDirectory,
            DeviceInfoPath = infoPath,
            Downloads = downloads,
        };
    }

    private static string BuildDeviceInfo(string deviceDescription, IReadOnlyList<(string, string, long)> downloads)
    {
        var builder = new StringBuilder();
        builder.AppendLine("device_description: " + deviceDescription);
        builder.AppendLine("collected_at: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        builder.AppendLine("regions: " + downloads.Count);
        foreach (var (region, path, bytes) in downloads)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"region {region} file {Path.GetFileName(path)} bytes {bytes}");
        }

        return builder.ToString();
    }

    private static string MakeSafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return safe.Length == 0 ? "region" : safe;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }
}