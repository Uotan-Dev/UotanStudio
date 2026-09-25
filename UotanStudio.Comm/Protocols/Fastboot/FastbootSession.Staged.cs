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
    /// Waits for a snapshot merge operation to complete within the specified timeout.
    /// <para>在指定超时时间内等待快照合并操作完成。</para>
    /// </summary>
    /// <param name="timeoutSeconds">
    /// Maximum seconds to poll the snapshot-update-status variable; merges are re-checked every two seconds.
    /// <para>轮询 snapshot-update-status 变量的最长秒数；合并状态每两秒复查一次。</para>
    /// </param>
    public void WaitForSnapshotMerge(int timeoutSeconds = 600)
    {
        var start = Stopwatch.GetTimestamp();
        while ((Stopwatch.GetTimestamp() - start) < Stopwatch.Frequency * timeoutSeconds)
        {
            var res = GetVariable(FastbootConstants.VarSnapshotUpdateStatus);
            if (res == "merging")
            {
                Thread.Sleep(2000);
                continue;
            }

            if (res is "none" or "completed")
            {
                return;
            }

            break;
        }
    }

    /// <summary>
    /// Cancels a pending snapshot update if one is in progress.
    /// <para>如果正在进行快照更新，则取消挂起的快照更新。</para>
    /// </summary>
    public void CancelSnapshotIfNeeded()
    {
        try
        {
            var status = GetVariable(FastbootConstants.VarSnapshotUpdateStatus);
            if (!string.IsNullOrEmpty(status) && status != "none")
            {
                SnapshotUpdate("cancel");
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// Wipes user data by erasing and formatting userdata, cache, and metadata partitions.
    /// <para>通过擦除并格式化 userdata、cache 和 metadata 分区来清除用户数据。</para>
    /// </summary>
    public void WipeUserData()
    {
        foreach (var partition in new[] { "userdata", "cache", "metadata" })
        {
            try
            {
                var partitionType = GetPartitionType(partition);
                if (string.IsNullOrEmpty(partitionType))
                {
                    continue;
                }

                ErasePartition(partition);
                FormatPartition(partition);
            }
            catch
            {
            }
        }
    }

}