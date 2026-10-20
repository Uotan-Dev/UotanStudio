using System.Globalization;
using Avalonia;
using UotanStudio.Comm.Protocols.Fastboot;
using UotanStudio.Services;
using UotanStudio.Services.Execution;

namespace UotanStudio.ViewModels.NodeViewModels;

/// <summary>
/// Device partition read node. Reads partition content over the managed Fastboot session and publishes it to a
/// file through a same-directory temporary file, so a failed read never replaces an existing backup. The node
/// never opens a device handle itself; it consumes the host's session bridge.
/// <para>设备分区读取节点。通过受管 Fastboot 会话读取分区内容，并经同目录临时文件发布到文件，读取失败时不会替换既有备份。
/// 节点自身不打开设备句柄，只消费宿主的会话桥。</para>
/// </summary>
public sealed class DevicePartitionReadNodeViewModel : FlowNodeViewModel
{
    /// <summary>
    /// Creates a partition read node with the specified display title.
    /// <para>创建具有指定显示标题的分区读取节点。</para>
    /// </summary>
    /// <param name="title">The display title of the node. <para>节点的显示标题。</para></param>
    public DevicePartitionReadNodeViewModel(string title) : base(title, NodeCategory.Flash)
    {
    }

    /// <summary>
    /// Creates the partition content read node.
    /// <para>创建分区内容读取节点。</para>
    /// </summary>
    /// <returns>The configured node instance. <para>配置完成的节点实例。</para></returns>
    public static DevicePartitionReadNodeViewModel CreateReadNode()
    {
        var node = new DevicePartitionReadNodeViewModel("Read Partition")
        {
            Location = new Point(100, 100),
            TypeKey = "Device.Partition.Read"
        };
        node.Input.Add(new NodeConnectorViewModel { Title = "Device", PortType = NodePortType.Device, IsRequired = true });
        node.Input.Add(new NodeConnectorViewModel { Title = "Partition", PortType = NodePortType.Text, IsRequired = true });
        node.Input.Add(new NodeConnectorViewModel { Title = "Output Path", PortType = NodePortType.Path, IsRequired = true });
        node.Output.Add(new NodeConnectorViewModel { Title = "Path", PortType = NodePortType.Path });
        node.Output.Add(new NodeConnectorViewModel { Title = "Bytes", PortType = NodePortType.Number });
        return node;
    }

    /// <summary>
    /// Reads the requested partition and publishes it to the output path.
    /// <para>读取请求的分区并发布到输出路径。</para>
    /// </summary>
    /// <param name="context">The execution context. <para>执行上下文。</para></param>
    /// <returns>A task representing the asynchronous execution. <para>表示异步执行的任务。</para></returns>
    /// <exception cref="InvalidOperationException">The device, partition or output path input is missing. <para>缺少设备、分区或输出路径输入。</para></exception>
    /// <exception cref="NotSupportedException">The target protocol cannot read partition content. <para>目标协议无法回读分区内容。</para></exception>
    public override async Task ExecuteAsync(INodeExecutionContext context)
    {
        if (!string.Equals(TypeKey, "Device.Partition.Read", StringComparison.Ordinal))
        {
            await base.ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        var device = NodeDataUtil.ToDevice(ReadInput("Device"))
            ?? throw new InvalidOperationException("The Device input is required before reading a partition.");
        var partition = Convert.ToString(ReadInput("Partition"), CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(partition))
        {
            throw new InvalidOperationException("The Partition input is required before reading a partition.");
        }
        var output = Convert.ToString(ReadInput("Output Path"), CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(output))
        {
            throw new InvalidOperationException("The Output Path input is required before reading a partition.");
        }

        var destination = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = Path.Combine(
            Path.GetDirectoryName(destination)!,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");

        long length;
        try
        {
            length = await Task.Run(() => App.DeviceMonitor.RunFastboot(device, session =>
            {
                using var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    128 * 1024, FileOptions.SequentialScan);
                var response = session.FetchToStream(partition, file, offset: 0, size: -1,
                    cancellationToken: context.CancellationToken);
                if (response.State != FastbootResponseState.Success)
                {
                    throw new InvalidOperationException(
                        $"Reading partition '{partition}' failed: {response.State}.  分区回读失败。");
                }
                file.Flush(flushToDisk: true);
                return file.Length;
            }, context.CancellationToken), context.CancellationToken).ConfigureAwait(false);

            context.CancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        WriteOutput("Path", destination);
        WriteOutput("Bytes", (double)length);
        context.Log.Write(LogLevel.Info, Title, $"Read {length} bytes from partition '{partition}'.  已回读分区。");
    }
}