using System.Diagnostics;


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
    /// <param name="progress">Receives the cumulative received byte count after each chunk; may be <see langword="null"/>.
    /// <para>每个分块接收后接收累计字节数；可为 <see langword="null"/>。</para></param>
    /// <param name="cancellationToken">Checked before every transport read; cancelling aborts the transfer mid-payload.
    /// <para>每次传输读取前检查；取消会在传输中途终止。</para></param>
    /// <returns>The trailing OKAY/FAIL response after the staged payload; callers must check it, because a
    /// failed <c>get_staged</c> leaves only a truncated payload in <paramref name="output"/>.
    /// <para>暂存负载之后的终止 OKAY/FAIL 响应；调用方必须检查它，因为失败的 <c>get_staged</c>
    /// 只会在 <paramref name="output"/> 中留下截断的负载。</para></returns>
    /// <exception cref="FastbootException">The device did not answer the command with a DATA frame. <para>设备未以 DATA 帧应答该命令。</para></exception>
    /// <exception cref="EndOfStreamException">The device stopped sending before the announced payload size. <para>设备在声明的负载大小传完前停止发送。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled. <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session has been disposed. <para>会话已释放。</para></exception>
    public FastbootResponse GetStagedToStream(Stream output, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
        => UploadData(FastbootConstants.CommandGetStaged, output, progress, cancellationToken);

    /// <summary>
    /// Retrieves staged data from the device and writes it to a file. The terminal response is
    /// returned rather than discarded: the destination file exists and holds whatever the device
    /// streamed, so a FAIL after a partial payload would otherwise leave a silently truncated file.
    /// <para>从设备获取暂存数据并写入文件。终止响应被返回而非丢弃：目标文件已经存在并保存了设备流式
    /// 送出的内容，因此负载中途出现 FAIL 时，否则会留下一个被静默截断的文件。</para>
    /// </summary>
    /// <param name="outputPath">Destination file path, created (or truncated) and closed when the call returns.
    /// <para>目标文件路径；调用返回时已创建（或截断）并关闭。</para></param>
    /// <param name="progress">Receives the cumulative received byte count after each chunk; may be <see langword="null"/>.
    /// <para>每个分块接收后接收累计字节数；可为 <see langword="null"/>。</para></param>
    /// <param name="cancellationToken">Checked before every transport read; cancelling aborts the transfer mid-payload and leaves the partial file in place.
    /// <para>每次传输读取前检查；取消会在传输中途终止，并保留部分文件。</para></param>
    /// <returns>The trailing OKAY/FAIL response after the staged payload. <para>暂存负载之后的终止 OKAY/FAIL 响应。</para></returns>
    /// <exception cref="IOException">The destination path cannot be created or written. <para>目标路径无法创建或写入。</para></exception>
    /// <exception cref="FastbootException">The device did not answer the command with a DATA frame. <para>设备未以 DATA 帧应答该命令。</para></exception>
    /// <exception cref="EndOfStreamException">The device stopped sending before the announced payload size. <para>设备在声明的负载大小传完前停止发送。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled. <para><paramref name="cancellationToken"/> 被取消。</para></exception>
    /// <exception cref="ObjectDisposedException">The session has been disposed. <para>会话已释放。</para></exception>
    public FastbootResponse GetStaged(string outputPath, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        using var fs = File.Create(outputPath);
        return GetStagedToStream(fs, progress, cancellationToken);
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
    /// the next boot. This never reboots the device: some bootloaders can only cancel a merge from
    /// fastbootd and say so in free-form text, and rebooting would invalidate the caller's session,
    /// so that request is reported back for the caller to honor explicitly.
    /// <para>如果正在进行快照更新则将其取消。仅状态查询按尽力而为处理；当设备确认存在挂起合并时，
    /// <c>snapshot-update:cancel</c> 失败会向上传播而非被吞掉，因为未取消的合并会在下次重启时改写分区。
    /// 本方法绝不重启设备：某些 bootloader 只能在 fastbootd 中取消合并并以自由文本如此说明，而重启会使
    /// 调用方的会话失效，因此该请求被回报给调用方由其显式执行。</para>
    /// </summary>
    /// <returns><see langword="true"/> when a pending merge was found and the device answered that it
    /// needs a fastbootd reboot before the cancel takes effect, in which case the caller must issue
    /// <c>reboot-fastboot</c> itself; <see langword="false"/> when no merge was pending, the status
    /// query failed, or the cancel completed without that request.
    /// <para>发现挂起合并且设备应答需要先重启到 fastbootd 取消才能生效时返回 <see langword="true"/>，
    /// 此时调用方必须自行发送 <c>reboot-fastboot</c>；没有挂起合并、状态查询失败或取消未附带该请求时
    /// 返回 <see langword="false"/>。</para></returns>
    /// <exception cref="FastbootException">A pending merge was confirmed but the cancel command failed.
    /// <para>确认存在挂起合并但取消命令失败。</para></exception>
    public bool CancelSnapshotIfNeeded()
    {
        string status;
        try
        {
            status = GetVariable(FastbootConstants.VarSnapshotUpdateStatus, useCache: false);
        }
        catch (FastbootException)
        {
            return false;
        }

        if (string.IsNullOrEmpty(status) || status == "none")
        {
            return false;
        }

        var response = SnapshotUpdate("cancel");
        response.ThrowIfFailed();
        return response.RequestsFastbootReboot;
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