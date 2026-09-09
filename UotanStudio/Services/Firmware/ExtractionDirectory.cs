namespace UotanStudio.Services.Firmware;

/// <summary>Restores existing extraction contents on failure. 解包失败时恢复既有目录内容。</summary>
internal static class ExtractionDirectory
{
    internal static async Task<FirmwareOpResult> ReplaceAsync(string target, Func<Task<FirmwareOpResult>> extract)
    {
        var backupRoot = Path.Combine(Path.GetDirectoryName(target)!, ".deleted");
        var backup = Path.Combine(backupRoot, "extract-" + Guid.NewGuid().ToString("N"));
        var hadOriginal = Directory.Exists(target);
        if (hadOriginal)
        {
            Directory.CreateDirectory(backupRoot);
            Directory.Move(target, backup);
        }
        var success = false;
        try
        {
            Directory.CreateDirectory(target);
            var result = await extract();
            success = result.Success;
            return result;
        }
        finally
        {
            // Large extraction trees must not be deleted on the caller's UI thread.
            // 大型解包目录的清理和回滚不能阻塞调用方的 UI 线程。
            await Task.Run(() =>
            {
                if (!success)
                {
                    if (Directory.Exists(target)) Directory.Delete(target, true);
                    if (hadOriginal) Directory.Move(backup, target);
                }
                else if (hadOriginal) Directory.Delete(backup, true);
            }).ConfigureAwait(false);
        }
    }
}
