using Avalonia.Input;
using Avalonia.Platform.Storage;
using UotanStudio.Services;

namespace UotanStudio.Views.Controls;

/// <summary>Reads a local drag payload synchronously before an asynchronous operation consumes its paths.<para>在异步操作消费路径之前同步读取本地拖放载荷。</para></summary>
internal static class FileDropPaths
{
    /// <summary>Preserves universal and platform file-drop format handling.<para>保留通用与平台两种文件拖放格式的处理。</para></summary>
    /// <param name="transfer">The drag payload to probe.<para>要探测的拖放数据。</para></param>
    /// <param name="logSource">Source label recorded with error log entries.<para>写入错误日志条目的来源标签。</para></param>
    /// <returns>True when the payload carries a file-drop format; otherwise false.<para>载荷带有文件拖放格式时为 true，否则为 false。</para></returns>
    internal static bool HasFilePayload(IDataTransfer transfer, string logSource)
    {
        if (transfer.Contains(DataFormat.File))
        {
            return true;
        }

        try
        {
            if (transfer.TryGetFiles() is { Length: > 0 })
            {
                return true;
            }
        }
        catch (Exception ex)
        {
            LogService.Instance.Write(LogLevel.Error, logSource, ex.Message);
        }

        foreach (var format in transfer.Formats)
        {
            if (string.Equals(format.Identifier, "FileDrop", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Preserves universal and platform file-drop format handling.<para>保留通用与平台两种文件拖放格式的处理。</para></summary>
    /// <param name="transfer">The drag payload to read.<para>要读取的拖放数据。</para></param>
    /// <param name="logSource">Source label recorded with error log entries.<para>写入错误日志条目的来源标签。</para></param>
    /// <returns>The local paths found, or an empty list when none are present.<para>找到的本地路径；没有时为空列表。</para></returns>
    internal static IReadOnlyList<string> TryExtractPaths(IDataTransfer transfer, string logSource)
    {
        var items = transfer.TryGetFiles();
        if (items is { Length: > 0 })
        {
            return [.. items.Select(static item => item.Path.LocalPath)];
        }

        foreach (var format in transfer.Formats)
        {
            if (!string.Equals(format.Identifier, "FileDrop", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var item in transfer.Items)
            {
                if (!item.Contains(format))
                {
                    continue;
                }

                try
                {
                    if (item.TryGetRaw(format) is System.Collections.IEnumerable enumerable)
                    {
                        var storage = enumerable.OfType<IStorageItem>().ToArray();
                        if (storage.Length > 0)
                        {
                            return [.. storage.Select(static s => s.Path.LocalPath)];
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogService.Instance.Write(LogLevel.Error, logSource, ex.Message);
                }
            }
        }

        return [];
    }
}