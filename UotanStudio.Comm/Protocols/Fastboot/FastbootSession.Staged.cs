using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using UotanStudio.Comm.Protocols.Common;
using UotanStudio.Comm.Protocols.Fastboot.Images;
using UotanStudio.Comm.Transports.Abstractions;


namespace UotanStudio.Comm.Protocols.Fastboot;

public sealed partial class FastbootSession
{
    /// <summary>
    /// Retrieves staged data from the device and writes it to the specified stream.
    /// <para>从设备获取暂存数据并写入指定流。</para>
    /// </summary>
    /// <param name="output">
    /// Stream that receives the staged payload; the caller retains ownership and the session only writes to it.
    /// <para>接收暂存负载的流；所有权由调用方保留，会话仅向其写入。</para>
    /// </param>
    /// <returns>The trailing OKAY/FAIL response after the staged payload. <para>暂存负载之后的终止 OKAY/FAIL 响应。</para></returns>
    /// <exception cref="ObjectDisposedException">The session has been disposed. <para>会话已释放。</para></exception>
    public FastbootResponse GetStagedToStream(Stream output)
        => UploadData(FastbootConstants.CommandGetStaged, output);

    /// <summary>
    /// Retrieves staged data from the device and writes it to a file.
    /// <para>从设备获取暂存数据并写入文件。</para>
    /// </summary>
    /// <param name="outputPath">Destination file path, created (or truncated) and closed when the call returns.
    /// <para>目标文件路径；调用返回时已创建（或截断）并关闭。</para></param>
    public void GetStaged(string outputPath)
    {
        using var fs = File.Create(outputPath);
        GetStagedToStream(fs);
    }

    /// <summary>
    /// Waits for a snapshot merge operation to complete within the specified timeout. Every poll
    /// forces a fresh <c>getvar</c> round trip — a cached status would freeze the first answer and
    /// make the wait unable to ever observe completion — and a timeout or unrecognized status
    /// throws instead of returning silently, so callers never flash over an in-progress merge.
    /// <para>在指定超时时间内等待快照合并操作完成。每次轮询都强制重新执行 <c>getvar</c> ——
    /// 缓存会冻结首次应答，使等待永远无法观察到完成 —— 超时或无法识别的状态会抛出异常而非
    /// 静默返回，以免调用方在合并进行中时继续刷写。</para>
    /// </summary>
    /// <param name="timeoutSeconds">
    /// Maximum seconds to poll the snapshot-update-status variable; merges are re-checked every two seconds.
    /// <para>轮询 snapshot-update-status 变量的最长秒数；合并状态每两秒复查一次。</para>
    /// </param>
    /// <exception cref="FastbootException">The merge did not complete within the timeout, or the device
    /// reported an unrecognized status. <para>合并未在超时内完成，或设备报告了无法识别的状态。</para></exception>
    public void WaitForSnapshotMerge(int timeoutSeconds = 600)
    {
        var start = Stopwatch.GetTimestamp();
        while ((Stopwatch.GetTimestamp() - start) < Stopwatch.Frequency * timeoutSeconds)
        {
            var res = GetVariable(FastbootConstants.VarSnapshotUpdateStatus, useCache: false);
            if (res == "merging")
            {
                Thread.Sleep(2000);
                continue;
            }

            if (res is "none" or "completed")
            {
                return;
            }

            throw new FastbootException(FastbootResponseState.Fail,
                $"snapshot merge ended with unrecognized status '{res}'.");
        }

        throw new FastbootException(FastbootResponseState.Timeout,
            $"snapshot merge did not complete within {timeoutSeconds}s.");
    }

    /// <summary>
    /// Cancels a pending snapshot update if one is in progress. Only the status query is treated as
    /// best-effort; when the device confirms a pending merge, a failing <c>snapshot-update:cancel</c>
    /// propagates instead of being swallowed, because an uncancelled merge rewrites partitions at
    /// the next boot.
    /// <para>如果正在进行快照更新则将其取消。仅状态查询按尽力而为处理；当设备确认存在挂起合并时，
    /// <c>snapshot-update:cancel</c> 失败会向上传播而非被吞掉，因为未取消的合并会在下次重启时改写分区。</para>
    /// </summary>
    /// <exception cref="FastbootException">A pending merge was confirmed but the cancel command failed.
    /// <para>确认存在挂起合并但取消命令失败。</para></exception>
    public void CancelSnapshotIfNeeded()
    {
        string status;
        try
        {
            status = GetVariable(FastbootConstants.VarSnapshotUpdateStatus, useCache: false);
        }
        catch (FastbootException)
        {
            return;
        }

        if (!string.IsNullOrEmpty(status) && status != "none")
        {
            SnapshotUpdate("cancel").ThrowIfFailed();
        }
    }

    /// <summary>
    /// Wipes user data by erasing and formatting userdata, cache, and metadata partitions. Every
    /// erase/format response is checked: this is the destructive factory-wipe equivalent, so a
    /// skipped or failed partition surfaces as an exception instead of a silent partial wipe.
    /// <para>通过擦除并格式化 userdata、cache 和 metadata 分区来清除用户数据。每个擦除/格式化
    /// 响应都会被检查：这是破坏性的恢复出厂等价操作，被跳过或失败的分区会以异常呈现而非静默的部分清除。</para>
    /// </summary>
    /// <exception cref="FastbootException">A partition could not be erased or formatted.
    /// <para>某分区无法擦除或格式化。</para></exception>
    public void WipeUserData()
    {
        foreach (var partition in new[] { "userdata", "cache", "metadata" })
        {
            var partitionType = GetPartitionType(partition);
            if (string.IsNullOrEmpty(partitionType))
            {
                throw new FastbootException(FastbootResponseState.Fail,
                    $"wipe aborted: device does not report a partition-type for '{partition}'.");
            }

            ErasePartition(partition).ThrowIfFailed();
            FormatPartition(partition).ThrowIfFailed();
        }
    }

}