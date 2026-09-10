using Avalonia.Input;
using Avalonia.Platform.Storage;
using UotanStudio.Services;

namespace UotanStudio.Views.Controls;

/// <summary>Reads a local drag payload synchronously before an asynchronous operation consumes its paths.</summary>
internal static class FileDropPaths
{
    /// <summary>Preserves universal and platform file-drop format handling.</summary>
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

    /// <summary>Preserves universal and platform file-drop format handling.</summary>
    internal static IReadOnlyList<string> TryExtractPaths(IDataTransfer transfer, string logSource)
    {
        var items = transfer.TryGetFiles();
        if (items is { Length: > 0 })
        {
            return items.Select(static item => item.Path.LocalPath).ToList();
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
                            return storage.Select(static s => s.Path.LocalPath).ToList();
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